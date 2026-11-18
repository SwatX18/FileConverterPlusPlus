# Explorer extension: selection handling, search results, command line

Investigation of "the menu disappears when one file selected from Explorer search results lives in a sub-folder",
what was changed, and how to test it.

## Execution path (what runs, in order)

1. Explorer loads `FileConverterPlusPlusExtension.dll` (SharpShell, registered on `HKCR\*\shellex\ContextMenuHandlers`
   through `[COMServerAssociation(AssociationType.AllFiles)]`) and calls `IShellExtInit::Initialize(pidlFolder, IDataObject, hkey)`.
2. **Before this change**: SharpShell's `ShellExtInitServer.Initialize` calls `IDataObject.GetData(CF_HDROP)` and
   `DragQueryFile` into a 260 character buffer (`IDataObjectExtensions.GetFileList`). `GetData` **throws** when the
   format is not offered; the exception leaves `Initialize` and Explorer drops the extension: the menu is simply absent.
   There is no log and no way for the extension to react (it is never reached).
3. `IContextMenu::QueryContextMenu` → `CanShowMenu()`: extensions of the selection against the presets' `InputTypes`.
4. `CreateMenu()`: one entry per compatible preset, enabled only when it accepts every selected extension.
5. Click → `ConvertFiles()` starts `FileConverterPlusPlus.exe --conversion-preset "<name>" <files…>` (or `--input-files <list>`).

## What was verified on Windows 11 (build 26300), and what was not

Tool: `Tests/ShellProbe` (see below). It attaches to a **live Explorer window showing real search results**
(`search-ms:`), selects items in the real view, takes the selection's `IDataObject` from `IFolderView::Items(SVGIO_SELECTION)`
and runs the extension on it exactly like Explorer does (COM activation, `IShellExtInit::Initialize`, `IContextMenu::QueryContextMenu`).

| Observation | Result |
|---|---|
| Search results of an indexed folder and of a non-indexed folder; root file, nested file, deep nested file, mixed root + nested + other folder | The data object offers `Shell IDList Array`, `CF_HDROP` (full paths, correct Unicode), `FileNameW`…; **the installed extension (SharpShell) shows its menu in every case** |
| Same, but the data object does not offer `CF_HDROP` (simulated by the probe) | **Installed extension: `Initialize` fails with `0x80040064` (DV_E_FORMATETC), no menu at all**; new extension: menu shown, paths read through `IShellItemArray` |
| Path longer than 260 characters (folder view) | The shell hands out the 8.3 alias (`SERIES~1.AVI`) in `CF_HDROP` and `IShellItemArray`; outputs were named after the alias. The new code expands aliases with `GetLongPathName` |

**Not reproduced here**: the exact failure reported (menu gone for a nested file in search results). On this machine the
shell gives `CF_HDROP` for every search selection I tried, so the SharpShell code path worked. I could not drive a
real right-click (the automation tool can only left-click in Explorer). The extension failure mode above is real and
matches the symptom ("menu absent, no error"), but it has **not been shown to be what your Explorer does**.
If the menu still disappears after this change, enable the log (below): the log says which channel failed and why.

Ruled out on this machine: directory depth, files from different parents, root vs nested, indexed vs non-indexed
search, `Path.GetExtension`, preset matching (unit-tested), the 15-file limit (not involved).

## What changed

- `ShellSelection` (new): reads the paths through `IShellItemArray` (`SHCreateShellItemArrayFromDataObject`,
  `SIGDN_FILESYSPATH`, then the absolute parsing name when it is a file path), then `CF_HDROP` with a buffer sized by the
  shell, then `Shell IDList Array` (child PIDLs combined with the parent PIDL). First channel with a result wins.
  Duplicates are removed, 8.3 aliases expanded. Items without a file path are skipped **and logged**.
- `FileConverterExtension` re-implements `IShellExtInit` (C# interface re-implementation over SharpShell's
  `SharpContextMenu`), so SharpShell's fragile `Initialize` is no longer called. SharpShell itself stays: COM registration,
  `IContextMenu` plumbing and the installer are unchanged. `Initialize` never throws.
- `ConversionCommandLine` (new): Windows quoting (`CommandLineToArgvW` rules, verified by round-trip tests with spaces,
  parentheses, `&`, `%`, Unicode, trailing backslashes, quotes). When the command line would exceed 8000 characters the
  files go to `file-converter-input-list-<guid>.txt` (UTF-8, one path per line, created with `CreateNew`, so concurrent
  right-clicks cannot collide; the old `File.Exists` + `OpenWrite` loop could). Leftover lists older than a day are
  removed. The application deletes its own list after reading it (only `file-converter-input-list*.txt` in the temp
  folder); the extension deletes it too when the process exits, as a safety net.
- `FileConverterPlusPlus.exe`: blank lines in the list are ignored.
- `ExtensionLog` (new): `%LOCALAPPDATA%\FileConverter\ShellExtension.log` (1 MB, then `.old`). Anomalies (failed channel,
  skipped item, hidden menu because the preset list could not be loaded, failed process start) are always logged.
  Routine traces (selection, extensions, command line) only with the registry value
  `HKCU\Software\FileConverter\ExtensionLog` = `true`. Also sent to `OutputDebugString`.

### Is SharpShell the root cause?

It is the **fragile point** (single channel, no tolerance, 260 character buffer, exception kills the extension), shown by
the probe with a data object without `CF_HDROP`. Replacing it is not needed: re-implementing one interface is the smallest
change. Moving to a native `IExplorerCommand` (the Windows 11 modern menu) would be a larger, separate project (a
sparse package + native/WinRT host); the entry stays under "Show more options" on Windows 11 as before.

## Test matrix

Automated: `Tests/FileConverter.Tests/ShellExtensionTests.cs` (menu logic, command line, list files).
Probe: `Tests/ShellProbe` against a live Explorer view (COM level). Manual: a real right-click.

| Test | Expected | Automated | Probe (live Explorer) | Manual right-click |
|---|---|---|---|---|
| One AVI in an ordinary folder | Menu visible | `OneAviAnywhereShowsTheMenu` | ordinary-folder mode: menu item inserted | to do |
| Several AVI in an ordinary folder | Menu visible | `FilesFromDifferentSubdirectories…` | ordinary-folder mode | to do |
| One root-level AVI in search results | Menu visible | – | **passed** (installed + new build) | to do |
| One nested AVI in search results | Menu visible | `OneAviAnywhereShowsTheMenu` (depth 12, UNC) | **passed** (installed + new build) | to do |
| Several nested AVI in search results | Menu visible | – | **passed** (new build; mixed root + nested + deeper) | to do |
| AVI files from different sub-directories / drives | Menu visible, all paths kept | `FilesFromDifferentSubdirectories…`, `SmallSelection…` | **passed** | to do |
| Mixed supported extensions (avi + mkv) | Only presets accepting every extension enabled, others greyed | `MixedExtensionsEnableOnlyThePresetsCompatibleWithAllOfThem` | – | to do |
| Nothing convertible (txt, no extension) | No menu | `NothingConvertibleHidesTheMenu` | – | to do |
| Data object without `CF_HDROP` | Menu visible | – | old: **fails** (`0x80040064`), new: **passed** | n/a |
| Data object without `Shell IDList Array` | Menu visible | – | new: **passed** (`CF_HDROP` channel) | n/a |
| Only `Shell IDList Array` usable | Menu visible | – | **not verifiable** (the shell builds the array from the same IDs); the code path exists but has no test | n/a |
| Spaces, parentheses, `&`, `%`, Unicode, trailing `\` | Arguments parsed back unchanged | `QuotedArgumentIsParsedBackUnchanged` (9 cases) | – | – |
| 600 files, ~115 KB of paths | Input list, nothing lost, none duplicated | `LargeSelectionGoesThroughAListFile…` | – | – |
| 64 simultaneous invocations | 64 distinct lists, correct content | `ConcurrentInvocations…` | – | – |
| Stale lists | Only old `file-converter-input-list-*` removed | `OnlyStaleListsAreRemoved…` | – | – |
| Path > 260 characters | Long (not 8.3) name used for the output | `NormalizeExpandsShortAliases…` | folder mode shows the alias | to do |

### Running the probe

```powershell
csc /platform:x64 /r:Microsoft.CSharp.dll /r:System.Core.dll /out:ShellProbe.exe Tests\ShellProbe\ShellProbe.cs   # .NET Framework csc

# 1. ordinary folder
.\ShellProbe.exe "D:\Movies\sub1" all

# 2. a live Explorer search: open it, then attach by window title
explorer.exe "search-ms:displayname=My%20search&crumb=System.FileName%3A*.avi&crumb=location:D%3A%5CMovies"
.\ShellProbe.exe "explorer:My search" "match:root_file;nested_file"      # selection: "all", "1,3" or "match:<substr>;<substr>"

# 3. what a data object without a format does (FCPP_HIDE = hdrop,idlist), new DLL loaded from a path (FCPP_EXT_ASSEMBLY)
$env:FCPP_HIDE = 'hdrop'; $env:FCPP_EXT_ASSEMBLY = "$PWD\Application\FileConverterExtension\bin\x64\Release\FileConverterPlusPlusExtension.dll"
.\ShellProbe.exe "explorer:My search" "match:nested_file"
```

Per item it prints the formats offered, the result of the SharpShell-style `CF_HDROP` read, of `IShellItemArray`, of
`Shell IDList Array`, and what the registered extension (`FCPP_EXT_CLSID`, default FileConverter++) does with it: the
`Initialize` result and the menu items it inserts.

### Manual right-click procedure (the part that could not be automated)

1. `reg add HKCU\Software\FileConverter /v ExtensionLog /d true /f` (writes the trace to `ShellExtension.log`).
2. Explorer → the Movies folder → search `*.avi`.
3. Select: one root file; one nested file; several nested files; root + nested; avi + mkv. Right-click →
   "Show more options" (Windows 11) → "FileConverter++" must be present.
4. If it is absent, send `%LOCALAPPDATA%\FileConverter\ShellExtension.log`: it names the failing channel or skipped item.
   (A restart of Explorer is needed to load a newly installed DLL.)

## Command-line limits

`MaximumCommandLineLength = 8000` is conservative on purpose (Windows allows 32 767 for `CreateProcess`; some launchers
8 191). The check now counts the executable path, quotes and every argument; before, the decision was taken while
appending and the list file was written from scratch each time.

## File managers with a "search results" panel (Unreal Commander, Total Commander...)

Reported: the menu is there for files in an ordinary panel, but not for the files of a search-results panel when they live
in sub-folders (root-level results work). Explorer is not involved. The log showed the extension was **never called** for
those clicks (no `Initialize` line).

Cause (reproduced with `ShellProbe fm:<root> "sub\file.avi"`): the manager asks the root folder's `IShellFolder` for items
given as paths relative to the root, i.e. multi-level child PIDLs. The shell builds the context menu's handler list from the
**first** selected item; for a multi-level PIDL it finds no handler under `*` and loads nothing. The extension called
directly with the same data object reads every path correctly, so nothing inside the DLL can fix it.

| Selection (first item decides) | Handler under `*` only | Also under `AllFileSystemObjects` |
|---|---|---|
| root file | loaded | loaded |
| nested file | **not loaded** | loaded |
| root file, then nested files | loaded | loaded |
| nested file, then root file | **not loaded** | loaded |

Registrations tried for a nested file (temporary keys in HKCU, removed afterwards): `AllFileSystemObjects` works;
`Unknown`, `SystemFileAssociations\.avi` and `SystemFileAssociations\video` do not. Registered under both `*` and
`AllFileSystemObjects` the shell shows one entry (it de-duplicates by CLSID). The extension therefore carries both
`[COMServerAssociation]` attributes. Confirmed by the reporter in Unreal Commander (single and multiple nested results,
4 files remuxed, 4 outputs).

Note: the MSI rewrites `HKCU\Software\FileConverter`, so the `ExtensionLog` value must be set again after an install.
