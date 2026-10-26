// <copyright file="ExplorerSelection.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Debug = FileConverter.Diagnostics.Debug;

    /// <summary>
    /// Reads the files selected in the foreground Explorer window through the Shell.Application automation object.
    /// </summary>
    internal static class ExplorerSelection
    {
        private const string ExplorerWindowClass = "CabinetWClass";
        private const string TabWindowClass = "ShellTabWindowClass";
        private const int TimeoutMilliseconds = 1000;

        private static readonly Guid TopLevelBrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid OleWindowInterface = new Guid("00000114-0000-0000-C000-000000000046");

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
        }

        [ComImport]
        [Guid("00000114-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleWindow
        {
            [PreserveSig]
            int GetWindow(out IntPtr window);

            [PreserveSig]
            int ContextSensitiveHelp(bool enterMode);
        }

        /// <summary>
        /// Returns true with the selected files (existing files only, no folder) if <paramref name="foregroundWindow"/> is an
        /// Explorer window with a selection.
        /// </summary>
        public static bool TryGetSelectedFiles(IntPtr foregroundWindow, out IReadOnlyList<string> files)
        {
            files = new string[0];
            string className = foregroundWindow == IntPtr.Zero ? string.Empty : AgentNativeMethods.GetWindowClassName(foregroundWindow);
            if (className != ExplorerWindowClass)
            {
                Debug.Log($"Radial menu agent: the foreground window is not an Explorer window ('{className}').");
                return false;
            }

            List<string> result = new List<string>();
            string failure = null;

            // Shell COM calls can hang when Explorer is busy: use a dedicated thread and a timeout.
            Thread thread = new Thread(() =>
            {
                try
                {
                    result.AddRange(ReadSelection(foregroundWindow));
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                }
            })
            {
                IsBackground = true,
                Name = "RadialExplorerSelection",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeoutMilliseconds))
            {
                Debug.Log("Radial menu agent: timeout while reading the Explorer selection.");
                return false;
            }

            if (failure != null)
            {
                Debug.Log($"Radial menu agent: can't read the Explorer selection: {failure}");
            }

            files = result.ToArray();
            return result.Count > 0;
        }

        private static IntPtr FindActiveTab(IntPtr explorerWindow)
        {
            IntPtr activeTab = IntPtr.Zero;
            AgentNativeMethods.EnumChildWindows(
                explorerWindow,
                (child, parameter) =>
                {
                    if (AgentNativeMethods.IsWindowVisible(child) && AgentNativeMethods.GetWindowClassName(child) == TabWindowClass)
                    {
                        activeTab = child;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);

            return activeTab;
        }

        private static IntPtr GetBrowserWindow(object shellWindow)
        {
            if (!(shellWindow is IServiceProvider serviceProvider))
            {
                return IntPtr.Zero;
            }

            Guid service = TopLevelBrowserService;
            Guid interfaceId = OleWindowInterface;
            if (serviceProvider.QueryService(ref service, ref interfaceId, out IntPtr pointer) != 0 || pointer == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                IOleWindow oleWindow = (IOleWindow)Marshal.GetObjectForIUnknown(pointer);
                return oleWindow.GetWindow(out IntPtr window) == 0 ? window : IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private static List<string> ReadSelection(IntPtr explorerWindow)
        {
            List<string> paths = new List<string>();

            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                return paths;
            }

            // With tabs (Windows 11) every tab is a shell window sharing the top level HWND: match the browser window of the active tab.
            IntPtr activeTab = FindActiveTab(explorerWindow);

            dynamic shell = Activator.CreateInstance(shellType);
            dynamic selectedItems = null;
            foreach (dynamic shellWindow in shell.Windows())
            {
                try
                {
                    bool isMatch;
                    if (activeTab != IntPtr.Zero)
                    {
                        isMatch = GetBrowserWindow((object)shellWindow) == activeTab;
                    }
                    else
                    {
                        isMatch = new IntPtr((long)shellWindow.HWND) == explorerWindow;
                    }

                    if (isMatch)
                    {
                        selectedItems = shellWindow.Document.SelectedItems();
                        break;
                    }
                }
                catch (Exception)
                {
                    // Not an Explorer window (Internet Explorer...) or a window that is closing.
                }
            }

            if (selectedItems == null)
            {
                return paths;
            }

            foreach (dynamic item in selectedItems)
            {
                string path = item.Path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
    }
}
