// ShellProbe: reproduces what Explorer hands to a context-menu extension, without needing Explorer.
//
// It binds a folder (an ordinary folder or a search-ms: result folder), enumerates its items, asks the
// folder for the IDataObject of a selection (IShellFolder::GetUIObjectOf, which is what Explorer does before it
// calls IShellExtInit::Initialize on every registered extension) and then runs, on that data object:
//
//   A. the SharpShell 2.7.x way: IDataObject.GetData(CF_HDROP) + DragQueryFile with a 260 char buffer;
//   B. the shell-item-array way: SHCreateShellItemArrayFromDataObject + SIGDN_FILESYSPATH / SIGDN_DESKTOPABSOLUTEPARSING;
//   C. the CFSTR_SHELLIDLIST ("Shell IDList Array") way.
//
// Usage:  ShellProbe.exe <folder | search-ms:...> <selection>
//   selection: "all", or comma separated 1-based item indexes, or "match:<substring>[;<substring>...]" (file path contains).
// Build:  csc /platform:x64 /out:ShellProbe.exe ShellProbe.cs   (see Tests/README.md)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

internal static class ShellProbe
{
    private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDataObject = new Guid("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItemArray = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
    private static readonly Guid BHID_SFObject = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");

    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SHCONTF_FOLDERS = 0x20;
    private const uint SHCONTF_NONFOLDERS = 0x40;
    private const short CF_HDROP = 15;

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IEnumIDList ppenum);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr strret);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr pidl, out uint fetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList ppenum);
    }

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

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp(bool enter);
        [PreserveSig] int InsertMenusSB(IntPtr a, IntPtr b);
        [PreserveSig] int SetMenuSB(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int RemoveMenusSB(IntPtr a);
        [PreserveSig] int SetStatusTextSB(IntPtr a);
        [PreserveSig] int EnableModelessSB(bool enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr a, short b);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr r);
        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
        [PreserveSig] int OnViewWindowActive(IntPtr shv);
        [PreserveSig] int SetToolbarItems(IntPtr a, uint b, uint c);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out long pt);
        [PreserveSig] int GetSpacing(out long pt);
        [PreserveSig] int GetDefaultSpacing(out long pt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count, IntPtr[] pidls, IntPtr pts, uint flags);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemWithParent(IntPtr pidlParent, IShellFolder psfParent, IntPtr pidl, ref Guid riid, out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromDataObject(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref Guid riid, out IShellItemArray array);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder buffer, uint cch);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder buffer, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr p);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: ShellProbe <folder | search-ms:...> <all | 1,2,3 | match:a;b>");
            return 2;
        }

        Console.OutputEncoding = Encoding.UTF8;
        int exit = 0;
        var thread = new Thread(() =>
        {
            if (args[0].StartsWith("explorer:")) exit = RunExplorer(args[0].Substring(9), args[1]);
            else if (args[0].StartsWith("fm:")) exit = RunFileManager(args[0].Substring(3), args[1], false);
            else if (args[0].StartsWith("fmabs:")) exit = RunFileManager(args[0].Substring(6), args[1], true);
            else exit = Run(args[0], args[1]);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int Run(string location, string selection)
    {
        Guid iidItem = IID_IShellItem;
        IShellItem folderItem;
        int hr = SHCreateItemFromParsingName(location, IntPtr.Zero, ref iidItem, out folderItem);
        Console.WriteLine("Location: " + location);
        if (hr != 0)
        {
            Console.WriteLine("SHCreateItemFromParsingName failed 0x{0:X8}", hr);
            return 1;
        }

        string folderParsing;
        folderItem.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out folderParsing);
        Console.WriteLine("Folder parsing name: " + folderParsing);

        Guid bhid = BHID_SFObject;
        Guid iidFolder = IID_IShellFolder;
        IntPtr pFolder;
        hr = folderItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidFolder, out pFolder);
        if (hr != 0)
        {
            Console.WriteLine("BindToHandler(SFObject) failed 0x{0:X8}", hr);
            return 1;
        }

        var folder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
        IEnumIDList enumerator;
        hr = folder.EnumObjects(IntPtr.Zero, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS, out enumerator);
        if (hr < 0 || enumerator == null)
        {
            Console.WriteLine("EnumObjects failed 0x{0:X8}", hr);
            return 1;
        }

        var pidls = new List<IntPtr>();
        var paths = new List<string>();
        while (true)
        {
            IntPtr pidl;
            uint fetched;
            if (enumerator.Next(1, out pidl, out fetched) != 0 || fetched == 0)
            {
                break;
            }

            IShellItem item;
            if (SHCreateItemWithParent(IntPtr.Zero, folder, pidl, ref iidItem, out item) != 0)
            {
                continue;
            }

            string path;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out path) != 0)
            {
                item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out path);
                path = "<not filesystem> " + path;
            }

            uint attrs;
            item.GetAttributes(0x20000000 /* SFGAO_FOLDER */, out attrs);
            if ((attrs & 0x20000000) != 0)
            {
                continue; // we only select files
            }

            pidls.Add(pidl);
            paths.Add(path);
        }

        Console.WriteLine("Items enumerated: " + pidls.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            Console.WriteLine("  [{0}] {1}", i + 1, paths[i]);
        }

        var selected = new List<int>();
        if (selection == "all")
        {
            for (int i = 0; i < pidls.Count; i++) selected.Add(i);
        }
        else if (selection.StartsWith("match:"))
        {
            string[] needles = selection.Substring(6).Split(';');
            foreach (string needle in needles)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (paths[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 && !selected.Contains(i)) selected.Add(i);
                }
            }
        }
        else
        {
            foreach (string s in selection.Split(',')) selected.Add(int.Parse(s) - 1);
        }

        Console.WriteLine();
        Console.WriteLine("Selection: " + string.Join(", ", selected.ConvertAll(i => (i + 1).ToString()).ToArray()));

        var apidl = new IntPtr[selected.Count];
        for (int i = 0; i < selected.Count; i++) apidl[i] = pidls[selected[i]];

        Guid iidData = IID_IDataObject;
        IntPtr pData;
        hr = folder.GetUIObjectOf(IntPtr.Zero, (uint)apidl.Length, apidl, ref iidData, IntPtr.Zero, out pData);
        Console.WriteLine("GetUIObjectOf(IDataObject) -> 0x{0:X8}", hr);
        if (hr != 0 || pData == IntPtr.Zero)
        {
            return 1;
        }

        var dataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(pData);
        DumpFormats(dataObject);
        TryHdrop(dataObject);
        TryShellItemArray(dataObject);
        TryShellIdList(dataObject);
        TryExtension(dataObject);
        return 0;
    }

    // Attaches to an Explorer window that already shows the folder / search results (LocationURL contains <urlPart>),
    // selects items in the real view and takes the selection's data object from the view itself.
    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder folder);

    // What a Total Commander-like file manager does for its "search results" panel (the items come from many folders):
    //   fm:<root>     asks the ROOT folder's IShellFolder for the items given as paths RELATIVE to the root (multi-level child
    //                 PIDLs such as "sub1\a.avi"), then GetUIObjectOf(IContextMenu / IDataObject) on it;
    //   fmabs:<root>  uses the desktop folder and absolute PIDLs instead.
    // Selection: relative paths separated by ';'. The registered extension is loaded by the shell itself (real QueryContextMenu).
    private static int RunFileManager(string root, string selection, bool absolute)
    {
        Guid iidItem = IID_IShellItem;
        IShellFolder folder;
        if (absolute)
        {
            SHGetDesktopFolder(out folder);
        }
        else
        {
            IShellItem rootItem;
            int hr0 = SHCreateItemFromParsingName(root, IntPtr.Zero, ref iidItem, out rootItem);
            if (hr0 != 0) { Console.WriteLine("root not found 0x{0:X8}", hr0); return 1; }
            Guid bhid = BHID_SFObject, iidFolder = IID_IShellFolder;
            IntPtr pFolder;
            hr0 = rootItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidFolder, out pFolder);
            if (hr0 != 0) { Console.WriteLine("bind failed 0x{0:X8}", hr0); return 1; }
            folder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
        }

        Console.WriteLine("Mode: {0}, root: {1}", absolute ? "desktop folder + absolute PIDLs" : "root folder + relative (multi-level) PIDLs", root);
        var pidls = new List<IntPtr>();
        foreach (string relative in selection.Split(';'))
        {
            string name = absolute ? System.IO.Path.Combine(root, relative) : relative;
            uint eaten, attrs = 0;
            IntPtr pidl;
            int hr = folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, name, out eaten, out pidl, ref attrs);
            Console.WriteLine("  ParseDisplayName('{0}') -> 0x{1:X8}", name, hr);
            if (hr == 0) pidls.Add(pidl);
        }

        IntPtr[] apidl = pidls.ToArray();

        Guid iidData = IID_IDataObject;
        IntPtr pData;
        int hrData = folder.GetUIObjectOf(IntPtr.Zero, (uint)apidl.Length, apidl, ref iidData, IntPtr.Zero, out pData);
        Console.WriteLine("GetUIObjectOf(IDataObject) -> 0x{0:X8}", hrData);
        if (hrData == 0 && pData != IntPtr.Zero)
        {
            var dataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(pData);
            DumpFormats(dataObject);
            TryHdrop(dataObject);
            TryShellItemArray(dataObject);
            TryShellIdList(dataObject);
            TryExtension(dataObject);
        }

        Console.WriteLine();
        Console.WriteLine("GetUIObjectOf(IContextMenu) + QueryContextMenu (the shell loads every registered handler):");
        Guid iidMenu = new Guid("000214e4-0000-0000-c000-000000000046");
        IntPtr pMenu;
        int hrMenu = folder.GetUIObjectOf(IntPtr.Zero, (uint)apidl.Length, apidl, ref iidMenu, IntPtr.Zero, out pMenu);
        Console.WriteLine("  -> 0x{0:X8}", hrMenu);
        if (hrMenu == 0 && pMenu != IntPtr.Zero)
        {
            IntPtr hmenu = CreatePopupMenu();
            int hrq = VtableFn<QueryContextMenuFn>(pMenu, 3)(pMenu, hmenu, 0, 1, 0x7FFF, 0x4 | 0x100 /* CMF_EXPLORE | CMF_EXTENDEDVERBS */);
            int items = GetMenuItemCount(hmenu);
            Console.WriteLine("  QueryContextMenu -> 0x{0:X8}, {1} items:", hrq, items);
            bool found = false;
            for (int i = 0; i < items; i++)
            {
                var sb = new StringBuilder(256);
                GetMenuString(hmenu, (uint)i, sb, sb.Capacity, 0x400);
                if (sb.ToString().IndexOf("FileConverter", StringComparison.OrdinalIgnoreCase) >= 0) found = true;
                if (sb.Length > 0) Console.WriteLine("    {0}", sb);
            }

            Console.WriteLine("  FileConverter++ entry in the shell's context menu: {0}", found ? "YES" : "NO");
            DestroyMenu(hmenu);
        }

        return 0;
    }

    private static int RunExplorer(string urlPart, string selection)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application");
        dynamic shell = Activator.CreateInstance(shellType);
        object window = null;
        string foundUrl = null;
        foreach (dynamic w in shell.Windows())
        {
            // Search-result folders have an empty LocationURL: match the title (LocationName) as well.
            string url, name;
            try { url = (string)w.LocationURL; name = (string)w.LocationName; } catch { continue; }
            string haystack = url + "|" + name;
            if (haystack.IndexOf(urlPart, StringComparison.OrdinalIgnoreCase) >= 0) { window = w; foundUrl = haystack; break; }
        }

        if (window == null) { Console.WriteLine("No Explorer window whose LocationURL contains '" + urlPart + "'."); return 1; }
        Console.WriteLine("Explorer window: " + foundUrl);

        IntPtr unk = Marshal.GetIUnknownForObject(window);
        Guid iidSp = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
        IntPtr pSp;
        Marshal.QueryInterface(unk, ref iidSp, out pSp);
        var sp = (IServiceProvider)Marshal.GetObjectForIUnknown(pSp);
        Guid sidTop = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        Guid iidBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        IntPtr pBrowser;
        int hr = sp.QueryService(ref sidTop, ref iidBrowser, out pBrowser);
        if (hr != 0) { Console.WriteLine("QueryService(IShellBrowser) failed 0x{0:X8}", hr); return 1; }
        var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
        IntPtr pView;
        hr = browser.QueryActiveShellView(out pView);
        if (hr != 0) { Console.WriteLine("QueryActiveShellView failed 0x{0:X8}", hr); return 1; }
        Guid iidFv = new Guid("cde725b0-ccc9-4519-917e-325d72fab4ce");
        IntPtr pFv;
        Marshal.QueryInterface(pView, ref iidFv, out pFv);
        var view = (IFolderView)Marshal.GetObjectForIUnknown(pFv);

        Guid iidFolder = IID_IShellFolder;
        IntPtr pFolder;
        view.GetFolder(ref iidFolder, out pFolder);
        var folder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);

        int count;
        view.ItemCount(2 /* SVGIO_ALLVIEW */, out count);
        Console.WriteLine("Items in the view: " + count);
        var paths = new List<string>();
        Guid iidItem = IID_IShellItem;
        for (int i = 0; i < count; i++)
        {
            IntPtr pidl;
            view.Item(i, out pidl);
            IShellItem item;
            string path = "<unresolved>";
            if (SHCreateItemWithParent(IntPtr.Zero, folder, pidl, ref iidItem, out item) == 0)
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out path) != 0) item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out path);
            }

            paths.Add(path);
            Console.WriteLine("  [{0}] {1}", i + 1, path);
        }

        var selected = new List<int>();
        if (selection == "all") { for (int i = 0; i < count; i++) selected.Add(i); }
        else if (selection.StartsWith("match:"))
        {
            foreach (string needle in selection.Substring(6).Split(';'))
                for (int i = 0; i < paths.Count; i++)
                    if (paths[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 && !selected.Contains(i)) selected.Add(i);
        }
        else { foreach (string s in selection.Split(',')) selected.Add(int.Parse(s) - 1); }

        Console.WriteLine();
        Console.WriteLine("Selecting: " + string.Join(", ", selected.ConvertAll(i => (i + 1).ToString()).ToArray()));
        for (int k = 0; k < selected.Count; k++)
        {
            // SVSI_SELECT (1) | SVSI_ENSUREVISIBLE (8), plus SVSI_DESELECTOTHERS (4) on the first item.
            view.SelectItem(selected[k], 1u | 8u | (k == 0 ? 4u : 0u));
        }

        Guid iidData = IID_IDataObject;
        IntPtr pData;
        hr = view.Items(1 /* SVGIO_SELECTION */, ref iidData, out pData);
        Console.WriteLine("IFolderView::Items(SVGIO_SELECTION, IDataObject) -> 0x{0:X8}", hr);
        if (hr != 0 || pData == IntPtr.Zero) return 1;
        var dataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(pData);
        DumpFormats(dataObject);
        TryHdrop(dataObject);
        TryShellItemArray(dataObject);
        TryShellIdList(dataObject);
        TryExtension(dataObject);
        return 0;
    }

    private static void DumpFormats(System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        Console.WriteLine();
        Console.WriteLine("Formats offered by the data object:");
        IEnumFORMATETC enumFormats = dataObject.EnumFormatEtc(DATADIR.DATADIR_GET);
        var formats = new FORMATETC[1];
        int[] fetched = new int[1];
        enumFormats.Reset();
        while (enumFormats.Next(1, formats, fetched) == 0 && fetched[0] == 1)
        {
            var sb = new StringBuilder(256);
            string name = GetClipboardFormatName((uint)(ushort)formats[0].cfFormat, sb, sb.Capacity) > 0 ? sb.ToString() : "CF_" + formats[0].cfFormat;
            Console.WriteLine("  {0,-40} tymed={1}", name, formats[0].tymed);
        }
    }

    // A: exactly what SharpShell 2.7.x does in IDataObjectExtensions.GetFileList.
    private static void TryHdrop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        Console.WriteLine();
        Console.WriteLine("[A] SharpShell-style CF_HDROP + DragQueryFile(MAX_PATH):");
        var formatEtc = new FORMATETC
        {
            cfFormat = CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };

        int queryHr = dataObject.QueryGetData(ref formatEtc);
        Console.WriteLine("    QueryGetData(CF_HDROP) -> 0x{0:X8}", queryHr);

        STGMEDIUM medium;
        try
        {
            dataObject.GetData(ref formatEtc, out medium);
        }
        catch (COMException e)
        {
            Console.WriteLine("    GetData(CF_HDROP) THROWS 0x{0:X8} {1}  => SharpShell Initialize() fails, extension is never shown", e.ErrorCode, e.Message.Trim());
            return;
        }

        try
        {
            uint count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            Console.WriteLine("    DragQueryFile count = " + count);
            for (uint i = 0; i < count; i++)
            {
                var sb = new StringBuilder(260);
                uint len = DragQueryFile(medium.unionmember, i, sb, (uint)sb.Capacity);
                Console.WriteLine("      [{0}] len={1}: {2}", i + 1, len, sb);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    // B: IShellItemArray, as recommended for modern extensions.
    private static void TryShellItemArray(System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        Console.WriteLine();
        Console.WriteLine("[B] SHCreateShellItemArrayFromDataObject:");
        Guid iid = IID_IShellItemArray;
        IShellItemArray array;
        int hr = SHCreateShellItemArrayFromDataObject(dataObject, ref iid, out array);
        if (hr != 0)
        {
            Console.WriteLine("    failed 0x{0:X8}", hr);
            return;
        }

        uint count;
        array.GetCount(out count);
        Console.WriteLine("    count = " + count);
        for (uint i = 0; i < count; i++)
        {
            IShellItem item;
            array.GetItemAt(i, out item);
            string fs, parsing, display;
            int hrFs = item.GetDisplayName(SIGDN_FILESYSPATH, out fs);
            int hrParsing = item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out parsing);
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out display);
            Console.WriteLine("      [{0}] FILESYSPATH(0x{1:X8})={2}", i + 1, hrFs, fs);
            Console.WriteLine("          PARSING(0x{0:X8})={1}", hrParsing, parsing);
        }
    }

    // C: CFSTR_SHELLIDLIST.
    private static void TryShellIdList(System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        Console.WriteLine();
        Console.WriteLine("[C] CFSTR_SHELLIDLIST (Shell IDList Array):");
        var formatEtc = new FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat("Shell IDList Array"),
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };

        STGMEDIUM medium;
        try
        {
            dataObject.GetData(ref formatEtc, out medium);
        }
        catch (COMException e)
        {
            Console.WriteLine("    GetData throws 0x{0:X8}", e.ErrorCode);
            return;
        }

        try
        {
            IntPtr p = GlobalLock(medium.unionmember);
            int cidl = Marshal.ReadInt32(p, 0);
            Console.WriteLine("    cidl = " + cidl + " (parent folder pidl + " + cidl + " child pidls)");
            GlobalUnlock(medium.unionmember);
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    // D: what Explorer does with a registered handler: CoCreateInstance, IShellExtInit::Initialize, IContextMenu::QueryContextMenu.
    // The CLSID comes from the FCPP_EXT_CLSID environment variable (default: FileConverter++ extension).
    // The in-proc CLR would hand back the managed object itself (and not a COM interface), so the vtables are called by hand,
    // like a native Explorer does.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeFn(IntPtr self, IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryContextMenuFn(IntPtr self, IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid, out IntPtr ppv);

    private static T VtableFn<T>(IntPtr comPtr, int slot) where T : class
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comPtr);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
    }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hmenu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hmenu, uint item, StringBuilder text, int max, uint flags);

    private static void TryExtension(System.Runtime.InteropServices.ComTypes.IDataObject dataObject)
    {
        Console.WriteLine();
        string clsid = Environment.GetEnvironmentVariable("FCPP_EXT_CLSID") ?? "AF9B72B5-F4E4-44B0-A3D9-B55B748EFE90";
        Console.WriteLine("[D] Registered extension {0}: Initialize + QueryContextMenu", clsid);
        Guid guidClsid = new Guid(clsid);
        Guid iidInit = new Guid("000214e8-0000-0000-c000-000000000046");
        Guid iidMenu = new Guid("000214e4-0000-0000-c000-000000000046");
        IntPtr pInit, pMenu;
        int hr;

        // FCPP_EXT_ASSEMBLY: load a freshly built extension DLL from a path instead of the registered one (no registration needed);
        // it is still called through its COM callable wrapper, like Explorer calls it.
        string assemblyPath = Environment.GetEnvironmentVariable("FCPP_EXT_ASSEMBLY");
        if (!string.IsNullOrEmpty(assemblyPath))
        {
            Console.WriteLine("    (loading {0})", assemblyPath);
            var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
            object instance = Activator.CreateInstance(assembly.GetType("FileConverterExtension.FileConverterExtension"));
            IntPtr unknown = Marshal.GetIUnknownForObject(instance);
            hr = Marshal.QueryInterface(unknown, ref iidInit, out pInit);
        }
        else
        {
            hr = CoCreateInstance(ref guidClsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iidInit, out pInit);
        }

        if (hr != 0)
        {
            Console.WriteLine("    creating IShellExtInit failed 0x{0:X8}", hr);
            return;
        }

        hr = Marshal.QueryInterface(pInit, ref iidMenu, out pMenu);
        Console.WriteLine("    QueryInterface(IContextMenu) -> 0x{0:X8}", hr);

        // FCPP_HIDE=hdrop,idlist: simulate a virtual folder that does not offer these clipboard formats.
        string hide = Environment.GetEnvironmentVariable("FCPP_HIDE");
        if (!string.IsNullOrEmpty(hide))
        {
            Console.WriteLine("    (data object filtered: hiding {0})", hide);
            dataObject = new FilterDataObject(dataObject, hide.ToLowerInvariant());
        }

        IntPtr pData = Marshal.GetIUnknownForObject(dataObject);
        hr = VtableFn<InitializeFn>(pInit, 3)(pInit, IntPtr.Zero, pData, IntPtr.Zero);
        Console.WriteLine("    IShellExtInit.Initialize -> 0x{0:X8}", hr);
        if (hr < 0)
        {
            Console.WriteLine("    => Explorer drops the extension: no menu entry at all.");
            return;
        }

        IntPtr hmenu = CreatePopupMenu();
        hr = VtableFn<QueryContextMenuFn>(pMenu, 3)(pMenu, hmenu, 0, 1, 0x7FFF, 0 /* CMF_NORMAL */);
        int items = GetMenuItemCount(hmenu);
        Console.WriteLine("    IContextMenu.QueryContextMenu -> 0x{0:X8} (command ids used: {1}), menu items inserted: {2}", hr, hr & 0xFFFF, items);
        for (int i = 0; i < items; i++)
        {
            var sb = new StringBuilder(256);
            GetMenuString(hmenu, (uint)i, sb, sb.Capacity, 0x400 /* MF_BYPOSITION */);
            Console.WriteLine("      item {0}: '{1}'", i, sb);
        }

        DestroyMenu(hmenu);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

    // Delegates to a real data object but refuses some clipboard formats with DV_E_FORMATETC.
    private sealed class FilterDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private const int DV_E_FORMATETC = unchecked((int)0x80040064);
        private readonly System.Runtime.InteropServices.ComTypes.IDataObject inner;
        private readonly HashSet<short> hidden = new HashSet<short>();

        public FilterDataObject(System.Runtime.InteropServices.ComTypes.IDataObject inner, string hide)
        {
            this.inner = inner;
            if (hide.Contains("hdrop")) hidden.Add(CF_HDROP);
            if (hide.Contains("idlist")) hidden.Add((short)RegisterClipboardFormat("Shell IDList Array"));
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (hidden.Contains(format.cfFormat)) throw new COMException("format hidden by probe", DV_E_FORMATETC);
            inner.GetData(ref format, out medium);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { inner.GetDataHere(ref format, ref medium); }
        public int QueryGetData(ref FORMATETC format) { return hidden.Contains(format.cfFormat) ? DV_E_FORMATETC : inner.QueryGetData(ref format); }
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { return inner.GetCanonicalFormatEtc(ref formatIn, out formatOut); }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) { inner.SetData(ref formatIn, ref medium, release); }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) { return inner.EnumFormatEtc(direction); }
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { return inner.DAdvise(ref pFormatetc, advf, adviseSink, out connection); }
        public void DUnadvise(int connection) { inner.DUnadvise(connection); }
        public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { return inner.EnumDAdvise(out enumAdvise); }
    }
}
