// <copyright file="ShellSelection.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverterExtension
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Runtime.InteropServices.ComTypes;
    using System.Text;

    /// <summary>
    /// Reads the file system paths of the items Explorer passes to <c>IShellExtInit::Initialize</c>.
    ///
    /// SharpShell does this with a single <c>IDataObject::GetData(CF_HDROP)</c> + <c>DragQueryFile</c> into a 260 character
    /// buffer, throws when the format is not offered, and cannot say why an item was dropped. Virtual folders such as
    /// search results do not have to offer CF_HDROP, so the data object is queried through several channels, in order:
    ///   1. IShellItemArray (SHCreateShellItemArrayFromDataObject), the channel Microsoft recommends;
    ///   2. CF_HDROP with a buffer sized by the shell;
    ///   3. CFSTR_SHELLIDLIST, resolving each child PIDL against the parent PIDL.
    /// The first channel that yields at least one path wins. Every skipped item and every failed channel is logged.
    /// </summary>
    public static class ShellSelection
    {
        private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const short CF_HDROP = 15;

        private static readonly Guid IID_IShellItemArray = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int GetAttributes(uint mask, out uint attrs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int order);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(int attribFlags, uint mask, out uint attrs);
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetItemAt(uint index, out IShellItem item);
            [PreserveSig] int EnumItems(out IntPtr ppenum);
        }

        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHCreateShellItemArrayFromDataObject(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref Guid riid, out IShellItemArray array);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint index, StringBuilder buffer, uint cch);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder path, uint cch, int flags);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint cch);

        /// <summary>
        /// Reads the selected file system paths from the data object behind <paramref name="dataObjectPointer"/>.
        /// Never throws; returns an empty list (and logs why) when nothing can be read.
        /// </summary>
        public static List<string> Read(IntPtr dataObjectPointer)
        {
            var paths = new List<string>();
            try
            {
                if (dataObjectPointer == IntPtr.Zero)
                {
                    ExtensionLog.Warn("Initialize: Explorer passed no data object.");
                    return paths;
                }

                var dataObject = Marshal.GetObjectForIUnknown(dataObjectPointer) as System.Runtime.InteropServices.ComTypes.IDataObject;
                if (dataObject == null)
                {
                    ExtensionLog.Warn("Initialize: the data object does not implement IDataObject.");
                    return paths;
                }

                if (TryShellItemArray(dataObject, paths) && paths.Count > 0)
                {
                    return Normalize(paths);
                }

                paths.Clear();
                if (TryHdrop(dataObject, paths) && paths.Count > 0)
                {
                    return Normalize(paths);
                }

                paths.Clear();
                if (TryShellIdList(dataObject, paths) && paths.Count > 0)
                {
                    return Normalize(paths);
                }

                ExtensionLog.Warn("Initialize: no channel produced a file system path for the selection (IShellItemArray, CF_HDROP and Shell IDList Array all failed or were empty).");
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: unexpected failure while reading the selection: " + exception);
            }

            paths.Clear();
            return paths;
        }

        /// <summary>
        /// Drops empty entries and duplicates (ordinal, case-insensitive; order kept) and expands 8.3 aliases, which Explorer
        /// hands out for paths longer than MAX_PATH. Exposed for tests.
        /// </summary>
        public static List<string> Normalize(IEnumerable<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths)
            {
                string path = raw?.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                path = ExpandShortName(path);
                if (!seen.Add(path))
                {
                    ExtensionLog.Info("Duplicate selected path ignored: " + path);
                    continue;
                }

                result.Add(path);
            }

            return result;
        }

        /// <summary>Whether a parsing name looks like a file system path (drive letter or UNC), as opposed to a virtual item.</summary>
        public static bool LooksLikeFileSystemPath(string parsingName)
        {
            if (string.IsNullOrEmpty(parsingName))
            {
                return false;
            }

            if (parsingName.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return !parsingName.StartsWith(@"\\?\", StringComparison.Ordinal) || parsingName.Length > 4;
            }

            return parsingName.Length >= 3 && char.IsLetter(parsingName[0]) && parsingName[1] == ':' && (parsingName[2] == '\\' || parsingName[2] == '/');
        }

        private static string ExpandShortName(string path)
        {
            if (path.IndexOf('~') < 0)
            {
                return path;
            }

            try
            {
                string extended;
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    extended = path;
                }
                else if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    extended = @"\\?\UNC\" + path.Substring(2);
                }
                else
                {
                    extended = @"\\?\" + path;
                }

                var buffer = new StringBuilder(1024);
                uint length = GetLongPathNameW(extended, buffer, (uint)buffer.Capacity);
                if (length == 0)
                {
                    return path;
                }

                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetLongPathNameW(extended, buffer, (uint)buffer.Capacity);
                    if (length == 0)
                    {
                        return path;
                    }
                }

                string expanded = buffer.ToString();

                // Paths shorter than MAX_PATH go back to their usual form, longer ones keep the \\?\ prefix.
                if (expanded.Length < 260)
                {
                    if (expanded.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    {
                        expanded = @"\\" + expanded.Substring(8);
                    }
                    else if (expanded.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        expanded = expanded.Substring(4);
                    }
                }

                return expanded;
            }
            catch
            {
                return path;
            }
        }

        private static bool TryShellItemArray(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, List<string> paths)
        {
            try
            {
                Guid iid = IID_IShellItemArray;
                int hr = SHCreateShellItemArrayFromDataObject(dataObject, ref iid, out IShellItemArray array);
                if (hr != 0 || array == null)
                {
                    ExtensionLog.Warn($"Initialize: SHCreateShellItemArrayFromDataObject failed (0x{hr:X8}).");
                    return false;
                }

                array.GetCount(out uint count);
                for (uint index = 0; index < count; index++)
                {
                    if (array.GetItemAt(index, out IShellItem item) != 0 || item == null)
                    {
                        ExtensionLog.Warn($"Initialize: item {index} of the selection can't be retrieved.");
                        continue;
                    }

                    int hr1 = item.GetDisplayName(SIGDN_FILESYSPATH, out string fileSystemPath);
                    if (hr1 == 0 && !string.IsNullOrEmpty(fileSystemPath))
                    {
                        paths.Add(fileSystemPath);
                        continue;
                    }

                    // Not a plain file system item (or a provider that only exposes a parsing name).
                    item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out string parsingName);
                    if (LooksLikeFileSystemPath(parsingName))
                    {
                        paths.Add(parsingName);
                        continue;
                    }

                    ExtensionLog.Warn($"Initialize: selected item {index} skipped, it has no file system path (SIGDN_FILESYSPATH hr=0x{hr1:X8}, parsing name '{parsingName}').");
                }

                ExtensionLog.Info($"IShellItemArray: {paths.Count} path(s) out of {count} item(s).");
                return true;
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: IShellItemArray channel failed: " + exception.Message);
                return false;
            }
        }

        private static bool TryHdrop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, List<string> paths)
        {
            var format = new FORMATETC
            {
                cfFormat = CF_HDROP,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };

            STGMEDIUM medium;
            try
            {
                dataObject.GetData(ref format, out medium);
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: CF_HDROP is not offered by the data object (" + exception.Message.Trim() + ").");
                return false;
            }

            try
            {
                if (medium.unionmember == IntPtr.Zero)
                {
                    ExtensionLog.Warn("Initialize: CF_HDROP storage medium is empty.");
                    return false;
                }

                uint count = DragQueryFileW(medium.unionmember, 0xFFFFFFFF, null, 0);
                for (uint index = 0; index < count; index++)
                {
                    uint length = DragQueryFileW(medium.unionmember, index, null, 0);
                    var buffer = new StringBuilder((int)length + 1);
                    DragQueryFileW(medium.unionmember, index, buffer, length + 1);
                    if (buffer.Length == 0)
                    {
                        ExtensionLog.Warn($"Initialize: CF_HDROP entry {index} is empty and was skipped.");
                        continue;
                    }

                    paths.Add(buffer.ToString());
                }

                ExtensionLog.Info($"CF_HDROP: {paths.Count} path(s) out of {count} entrie(s).");
                return true;
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: CF_HDROP channel failed: " + exception.Message);
                return false;
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }

        private static bool TryShellIdList(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, List<string> paths)
        {
            var format = new FORMATETC
            {
                cfFormat = unchecked((short)RegisterClipboardFormat("Shell IDList Array")),
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };

            STGMEDIUM medium;
            try
            {
                dataObject.GetData(ref format, out medium);
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: Shell IDList Array is not offered by the data object (" + exception.Message.Trim() + ").");
                return false;
            }

            IntPtr block = IntPtr.Zero;
            try
            {
                block = GlobalLock(medium.unionmember);
                if (block == IntPtr.Zero)
                {
                    return false;
                }

                // CIDA: UINT cidl; UINT aoffset[cidl + 1]; aoffset[0] is the parent folder PIDL, then the children.
                int count = Marshal.ReadInt32(block, 0);
                IntPtr parent = IntPtr.Add(block, Marshal.ReadInt32(block, 4));
                for (int index = 0; index < count; index++)
                {
                    IntPtr child = IntPtr.Add(block, Marshal.ReadInt32(block, 8 + (4 * index)));
                    IntPtr absolute = ILCombine(parent, child);
                    if (absolute == IntPtr.Zero)
                    {
                        ExtensionLog.Warn($"Initialize: Shell IDList item {index} can't be combined with its parent and was skipped.");
                        continue;
                    }

                    try
                    {
                        var buffer = new StringBuilder(32768);
                        if (SHGetPathFromIDListEx(absolute, buffer, (uint)buffer.Capacity, 0))
                        {
                            paths.Add(buffer.ToString());
                        }
                        else
                        {
                            ExtensionLog.Warn($"Initialize: Shell IDList item {index} skipped, it is not a file system item.");
                        }
                    }
                    finally
                    {
                        ILFree(absolute);
                    }
                }

                ExtensionLog.Info($"Shell IDList Array: {paths.Count} path(s) out of {count} item(s).");
                return true;
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn("Initialize: Shell IDList Array channel failed: " + exception.Message);
                return false;
            }
            finally
            {
                if (block != IntPtr.Zero)
                {
                    GlobalUnlock(medium.unionmember);
                }

                ReleaseStgMedium(ref medium);
            }
        }
    }
}
