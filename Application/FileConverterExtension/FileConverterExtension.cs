// <copyright file="FileConverterExtension.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverterExtension
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Windows.Forms;

    using SharpShell.Attributes;
    using SharpShell.SharpContextMenu;

    /// <summary>
    /// FileConverter++ context menu extension class.
    /// </summary>
    [ComVisible(true), Guid("AF9B72B5-F4E4-44B0-A3D9-B55B748EFE90")]
    // AllFiles ("*") is what Explorer uses. File managers that show search results (Unreal Commander, Total Commander...) hand the shell
    // multi-level relative PIDLs ("sub\file.avi"): the shell then finds no handler under "*" and never loads the extension, but it does
    // consult AllFileSystemObjects. Registering under both loads the extension once (verified: one menu entry, no duplicate).
    [COMServerAssociation(AssociationType.AllFiles)]
    [COMServerAssociation(AssociationType.AllFilesAndFolders)]
    public class FileConverterExtension : SharpContextMenu, SharpShell.Interop.IShellExtInit
    {
        private PresetReference[] presetReferences = null;

        // The selected file system paths, read once in Initialize. Replaces SharpShell's SelectedItemPaths, which comes from a single
        // CF_HDROP query with a 260 character buffer and makes the extension fail to load when the format is not offered.
        private List<string> selectedPaths = new List<string>();
        private List<MenuEntry> menuEntries = new List<MenuEntry>();

        private HashSet<string> extensionCache = new HashSet<string>();

        private class MenuEntry
        {
            public PresetReference PresetReference;
            public bool Enabled;
            public int ExtensionRefCount;

            public MenuEntry(PresetReference presetReference)
            {
                this.PresetReference = presetReference;
                this.Enabled = false;
                this.ExtensionRefCount = 0;
            }
        }
        
        private bool DisplayPresetIcons
        {
            get
            {
                string displayPresetIcons = PathHelpers.FileConverterRegistryKey.GetValue("DisplayPresetIcons") as string;
                if (displayPresetIcons == null)
                {
                    return false;
                }

                if (!bool.TryParse(displayPresetIcons, out bool value))
                {
                    return false;
                }

                return value;
            }
        }

        private PresetReference[] PresetReferences
        {
            get
            {
                this.LoadExtensionSettingsIfNecessary();

                return this.presetReferences;
            }
        }

        /// <summary>
        /// Re-implementation of IShellExtInit.Initialize (it takes over from SharpShell's, which is no longer called).
        /// Never throws: an exception here would make Explorer drop the extension, i.e. no menu at all.
        /// </summary>
        void SharpShell.Interop.IShellExtInit.Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID)
        {
            this.selectedPaths = ShellSelection.Read(pDataObj);
            ExtensionLog.Info($"Initialize: {this.selectedPaths.Count} selected path(s): {string.Join(" | ", this.selectedPaths)}");
        }

        protected override bool CanShowMenu()
        {
            this.RefreshExtensionCacheFromSelectedItems();

            PresetReference[] presets = this.PresetReferences;
            if (presets == null)
            {
                ExtensionLog.Warn("Menu hidden: the preset list could not be loaded (Settings.user.xml / Settings.default.xml).");
                return false;
            }

            foreach (string extension in this.extensionCache)
            {
                foreach (PresetReference presetReference in presets)
                {
                    if (presetReference.InputTypes.Contains(extension))
                    {
                        return true;
                    }
                }
            }

            ExtensionLog.Info($"Menu hidden: no preset accepts the selected extension(s) [{string.Join(", ", this.extensionCache)}] ({this.selectedPaths.Count} path(s)).");
            return false;
        }

        protected override ContextMenuStrip CreateMenu()
        {
            this.RefreshPresetList();

            bool displayPresetIcons = this.DisplayPresetIcons;

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem fileConverterItem = new ToolStripMenuItem
            {
                Text = "FileConverter++",
                Image = new Icon(Properties.Resources.ApplicationIcon, SystemInformation.SmallIconSize).ToBitmap(),
            };

            int menuItemIndex = 0;
            foreach (MenuEntry menuEntry in this.menuEntries)
            {
                menuItemIndex++;
                
                ToolStripMenuItem root = fileConverterItem;
                if (menuEntry.PresetReference.Folders != null)
                {
                    foreach (string folder in menuEntry.PresetReference.Folders)
                    {
                        ToolStripItem[] folderItems = root.DropDownItems.Find(folder, false);
                        if (folderItems.Length == 0)
                        {
                            ToolStripMenuItem folderItem = new ToolStripMenuItem
                            {
                                Name = folder,
                                Text = folder,
                                Image = new Icon(Properties.Resources.FolderIcon, SystemInformation.SmallIconSize).ToBitmap(),
                            };

                            root.DropDownItems.Add(folderItem);
                            root = folderItem;
                        }
                        else
                        {
                            root = folderItems[0] as ToolStripMenuItem;
                        }

                        if (root == null)
                        {
                            break;
                        }
                    }
                }

                if (root == null)
                {
                    // Fallback when something went wrong during folder creation.
                    root = fileConverterItem;
                }

                // Make each menu item text unique using invisible zero-width spaces
                // This prevents Windows Forms menu collision while being completely invisible to users
                string uniqueSuffix = new string('\u200B', menuItemIndex); // Zero-Width Space characters
                string displayText = menuEntry.PresetReference.Name + uniqueSuffix;

                ToolStripMenuItem subItem = new ToolStripMenuItem
                {
                    Text = displayText,
                    Enabled = menuEntry.Enabled
                };

                if (displayPresetIcons)
                {
                    subItem.Image = new Icon(Properties.Resources.PresetIcon, SystemInformation.SmallIconSize).ToBitmap();
                }

                root.DropDownItems.Add(subItem);
                subItem.Click += (sender, args) => this.ConvertFiles(menuEntry.PresetReference.FullName);
            }

            if (this.menuEntries.Count > 0)
            {
                fileConverterItem.DropDownItems.Add(new ToolStripSeparator());
            }

            {
                ToolStripMenuItem subItem = new ToolStripMenuItem
                {
                    Text = "Configure presets...",
                    Image = new Icon(Properties.Resources.SettingsIcon, SystemInformation.SmallIconSize).ToBitmap(),
                };

                fileConverterItem.DropDownItems.Add(subItem);
                subItem.Click += (sender, args) => this.OpenSettings();
            }

            menu.Items.Add(fileConverterItem);

            return menu;
        }

        private void RefreshExtensionCacheFromSelectedItems()
        {
            // Retrieve selected files extensions.
            this.extensionCache.Clear();
            foreach (string filePath in this.selectedPaths)
            {
                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(extension))
                {
                    ExtensionLog.Info($"Selected path without extension ignored: {filePath}");
                    continue;
                }

                extension = extension.Substring(1).ToLowerInvariant();

                this.extensionCache.Add(extension);
            }
        }

        private void RefreshPresetList()
        {
            this.RefreshExtensionCacheFromSelectedItems();

            // Activate compatible menu entries.
            PresetReference[] presets = this.PresetReferences ?? new PresetReference[0];
            this.menuEntries.Clear();
            foreach (string extension in this.extensionCache)
            {
                foreach (PresetReference presetReference in presets)
                {
                    if (!presetReference.InputTypes.Contains(extension))
                    {
                        continue;
                    }

                    MenuEntry menuEntry = this.menuEntries.Find(entry => entry.PresetReference.FullName == presetReference.FullName);
                    if (menuEntry == null)
                    {
                        menuEntry = new MenuEntry(presetReference);
                        this.menuEntries.Add(menuEntry);
                    }

                    menuEntry.ExtensionRefCount++;
                }
            }

            // Enable presets compatible with all input files.
            foreach (MenuEntry menuEntry in this.menuEntries)
            {
                menuEntry.Enabled = menuEntry.ExtensionRefCount == this.extensionCache.Count;
            }
        }

        private void LoadExtensionSettingsIfNecessary()
        {
            if (this.presetReferences != null)
            {
                return;
            }

            if (File.Exists(PathHelpers.UserSettingsFilePath))
            {
                try
                {
                    XmlHelpers.LoadFromFile("Settings", PathHelpers.UserSettingsFilePath, out this.presetReferences);
                    return;
                }
                catch
                {
                    // Can't handle this error in the explorer extension.
                }
            }

            try
            {
                XmlHelpers.LoadFromFile("Settings", PathHelpers.DefaultSettingsFilePath, out this.presetReferences);
            }
            catch
            {
                // Can't handle this error in the explorer extension.
            }
        }

        private void OpenSettings()
        {
            if (string.IsNullOrEmpty(PathHelpers.FileConverterPath))
            {
                MessageBox.Show("Can't retrieve the FileConverter++ executable path. You should try to reinstall the application.");
                return;
            }

            if (!File.Exists(PathHelpers.FileConverterPath))
            {
                MessageBox.Show($"Can't find the FileConverter++ executable ({PathHelpers.FileConverterPath}). You should try to reinstall the application.");
                return;
            }

            ProcessStartInfo processStartInfo = new ProcessStartInfo(PathHelpers.FileConverterPath)
            {
                CreateNoWindow = false, 
                UseShellExecute = false, 
                RedirectStandardOutput = false,
            };

            // Build arguments string.
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("--settings");
            
            processStartInfo.Arguments = stringBuilder.ToString();
            Process exeProcess = Process.Start(processStartInfo);
        }

        private void ConvertFiles(string presetName)
        {
            if (string.IsNullOrEmpty(PathHelpers.FileConverterPath))
            {
                MessageBox.Show("Can't retrieve the FileConverter++ executable path. You should try to reinstall the application.");
                return;
            }

            if (!File.Exists(PathHelpers.FileConverterPath))
            {
                MessageBox.Show($"Can't find the FileConverter++ executable ({PathHelpers.FileConverterPath}). You should try to reinstall the application.");
                return;
            }

            if (this.selectedPaths.Count == 0)
            {
                ExtensionLog.Warn($"Conversion with preset '{presetName}' requested but no file is selected.");
                return;
            }

            string[] filePaths = this.selectedPaths.ToArray();
            string listPath = null;
            try
            {
                // Windows command-line quoting; a private, uniquely named list file when the selection does not fit on the command line.
                ConversionCommandLine.Invocation invocation = ConversionCommandLine.Build(PathHelpers.FileConverterPath, presetName, filePaths, Path.GetTempPath());
                listPath = invocation.InputListPath;
                ExtensionLog.Info($"Starting '{PathHelpers.FileConverterPath}' for {filePaths.Length} file(s), preset '{presetName}', input list: {listPath ?? "<none>"}");

                var processStartInfo = new ProcessStartInfo(PathHelpers.FileConverterPath)
                {
                    CreateNoWindow = false,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    Arguments = invocation.Arguments,
                };

                Process exeProcess = Process.Start(processStartInfo);
                if (exeProcess == null)
                {
                    throw new InvalidOperationException("Process.Start returned no process.");
                }

                if (listPath != null)
                {
                    // The application deletes its own list once read; this is the safety net if it never got that far.
                    string listToDelete = listPath;
                    exeProcess.EnableRaisingEvents = true;
                    exeProcess.Exited += (sender, args) => ConversionCommandLine.TryDeleteInputList(listToDelete);
                }
            }
            catch (Exception exception)
            {
                ConversionCommandLine.TryDeleteInputList(listPath);
                ExtensionLog.Warn("Can't start the conversion: " + exception);
                MessageBox.Show($"FileConverter++ can't start the conversion: {exception.Message}");
            }
        }
    }
}
