# Tests

## Unit tests (`FileConverter.Tests`, xUnit, .NET Framework 4.8 x64)

```powershell
MSBuild Tests\FileConverter.Tests\FileConverter.Tests.csproj -restore /p:Configuration=Release /p:Platform=x64
& "<Visual Studio>\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" Tests\FileConverter.Tests\bin\x64\Release\net48\FileConverter.Tests.dll /Platform:x64
```

Visual Studio's Test Explorer runs them too (the project is in `FileConverter.sln`).

| File | What it checks |
|---|---|
| `RemuxPlannerTests` | Source codecs x output container x preset settings -> copy / encode / convert per stream, or a full re-encode (embedded `remux-compat.json`) |
| `VideoEncoderArgumentsTests` | Preset quality and speed -> NVENC / AMF / QSV / CPU encoder parameters (calibrated offsets, clamping) |
| `ImageRoutingTests` | Job type per input/output, libvips or ImageMagick per format and setting, rotations, output sizes (ImageMagick's computation) |
| `HardwareEncoderDetectorTests` | Detection with a fake process runner (success, failure, timeout, exception), cache by fingerprint, Re-detect, vendor priority, encoder summary |
| `SchedulingAndSettingsTests` | Limits per lane, thread budgets, settings migrations (v5 hardware acceleration, v6 simultaneous conversions), defaults |
| `RadialMenuTests` | Radial menu: preset filtering (compatible with every file), pager (8 per page, wrap), slice angles and hit test, work area clamping, hotkey parsing and native values, agent action table, settings v7 defaults, A/B facade, "B wins", v6 migration |
| `ExtensionSettingsTests` | Shell extension: menu mode table (master / A / B), reading v6 and v7 `Settings.user.xml` files (temporary folder) |
| `RadialAgentControllerTests` | When the tray agent should run, start / reload / stop table, Run value written and removed (in a test key), mutex detection |
| `ConversionProcessLauncherTests` | Agent conversion command line: quoted files, switch to a list file past 8000 characters, escaped preset names |

The tests don't touch the user's settings or the GPU, and the registry only under a temporary
`HKCU\Software\FileConverterPlusPlus.Tests` key (`RadialAgentControllerTests`, deleted afterwards; never the real Run
key). The libvips routing cases load the libvips DLLs copied next to `FileConverterPlusPlus.exe` (xUnit shadow copying
is disabled for that, `xunit.runner.json`). Logs go to a `Diagnostics-*` folder in `%LOCALAPPDATA%\FileConverter`, like
the application's.

## Integration tests (`integration.ps1`)

```powershell
powershell -ExecutionPolicy Bypass -File Tests\integration.ps1          # all cases -> Tests\integration.csv
powershell -ExecutionPolicy Bypass -File Tests\integration.ps1 -Case video_*
```

Converts the benchmark corpus (`bench/samples`, created by `bench/make-corpus.ps1`) with the built application, the
way the shell extension does, and checks each case: the application exits by itself (all conversions succeeded),
each file's log line names the expected conversion path (remux, hardware encoder, CPU fallback, vips, ImageMagick...),
and each output is readable (ffprobe and a full decode report no error) with the expected streams, dimensions and
duration (within 0.5 s of the source). 37 cases: stream copies, NVENC / AMF fallback / CPU encodes, every video
container, audio formats, image formats through libvips and ImageMagick, ICO, PDF. `-NoGpu` makes the hardware
cases expect the CPU path on a machine without a hardware encoder.

Known issue (upstream, not caused by this fork): the `To Ogv` output's Theora video does not decode cleanly with
ffmpeg 8 ("error in unpack_block_qpis", most frames lost), a long-standing problem of ffmpeg's libtheora encoder on
64-bit builds (FFmpeg trac #8040, #8775). The case is reported as `known`.

## Manual checklist: radial menu

The shell extension, the global hotkey, OLE drag-and-drop, monitors and the installer can't be driven reliably by a
script. Run this list on an **installed MSI** (Release build), on Windows 11 and, if possible, Windows 10, before a
release that touches `RadialMenu/`, `RadialMenuWindow`, the shell extension or `Product.wxs`. Logs:
`%LOCALAPPDATA%\FileConverter\Diagnostics-*` (lines start with `Radial menu`). Unless stated otherwise, select a few
files with at least one common preset (e.g. 3 JPEGs) and more than 8 compatible presets for the paging checks.

Off by default

- [ ] Fresh install, or upgrade with a version 6 `Settings.user.xml`: the context menu is exactly the classic one, no
      `FileConverterPlusPlus.exe --agent` process, no `FileConverterPlusPlus.RadialAgent` value under
      `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The file is rewritten as version 7 with the six
      `RadialMenu*` elements set to `false` / `Ctrl+Alt+C`.
- [ ] Settings → Radial menu: everything below "Enable the radial menu" is greyed out while it is unchecked.

A / B: context menu (installed MSI)

- [ ] A (Add a radial menu entry): right-click the files → Windows 11 **Show more options** (and Shift+right-click) →
      the FileConverter++ cascade is unchanged and "FileConverter++ (radial menu)…" follows it. It opens the donut at
      the cursor. With the application language French, the label is "FileConverter++ (menu radial)…".
- [ ] B (Replace the preset submenu): a single non-cascading "FileConverter++" item with the icon; it opens the donut.
- [ ] Hand-edit `Settings.user.xml` with A and B both `true`: the menu shows B's single item; opening and saving
      Settings stores A as `false`.
- [ ] Master off with A or B still `true` in the file: classic menu.
- [ ] More than ~8000 characters of paths (e.g. 200 files with long names): the donut still gets every file (list file).
- [ ] Right-clicking does not slow Explorer down compared with the classic menu.

Donut (any trigger)

- [ ] Hover highlights a slice and shows the preset's full name in the hub; click converts all the files with that
      preset, same outputs as the context-menu preset entry.
- [ ] Esc, a click outside the ring, or switching to another window closes it without converting.
- [ ] More than 8 presets: indicator `1/3`, Tab / mouse wheel go forward, Shift+Tab / wheel back, wrapping; 8 or fewer:
      no indicator.
- [ ] Mixed selection with no common preset (e.g. a JPEG and an MP3): "No compatible preset".
- [ ] Opened near each screen edge and corner: fully visible, inside the work area (not under the taskbar).

Mixed DPI

- [ ] Two monitors at different scaling (e.g. 100 % and 150 %, also 200 %): open the donut on each, from each trigger.
      It is scaled like the other windows of that monitor, sharp, centered on the cursor (or clamped), never straddling
      the two monitors, and the slice under the cursor is the one highlighted.
- [ ] Negative coordinates: a monitor left of or above the primary one.

C: global hotkey

- [ ] Enable the hotkey and Save: an agent process starts (one tray icon), the Run value exists.
- [ ] Ctrl+Alt+C in an Explorer window with files selected: donut with those files. Folders in the selection are left out.
- [ ] **Tabbed Explorer (Windows 11)**: two tabs with different selections; the hotkey uses the selection of the active
      tab. Switch tab and repeat. Also with two Explorer windows: the foreground one is used.
- [ ] No selection, desktop focused, or another application in front: nothing happens (log line only).
- [ ] Change the hotkey (e.g. Ctrl+Shift+F9) and Save: the new one works within ~2 s, the old one doesn't, no restart.
- [ ] A combination taken by another application (or by a second tool registering it first): tray balloon warning,
      Ctrl+drag still works if enabled.
- [ ] Hotkey box: a key without Ctrl / Alt / Win is ignored, Backspace resets to Ctrl+Alt+C.
- [ ] Tray icon menu: Settings opens the settings, Exit ends the agent (it comes back at the next logon or Settings save).

D: Ctrl+drag

- [ ] **Drop on a slice**: drag files from Explorer with Ctrl held (also pressing Ctrl only after the drag has started):
      the donut opens at the cursor, the slice under the cursor is highlighted with a copy cursor, dropping converts
      with that preset. **No copy** of the files is made anywhere (check the source and target folders).
- [ ] **Drop elsewhere**: on the hub, outside the ring, on another Explorer window, on the desktop, on another
      application, on another monitor: nothing is copied or moved, the donut closes on release.
- [ ] **Esc** during the drag: the drag is cancelled, nothing is copied, the donut closes at the latest when the button
      is released.
- [ ] Paging during a drag: with more than 8 presets, leave the hub then come back and stay ~0.6 s: next page; staying
      longer doesn't flip again until you leave and re-enter.
- [ ] **Plain drag** (no Ctrl): normal Explorer move / copy, no donut. Right-button drag → "Copy here" works.
- [ ] **Ctrl rubber-band**: Ctrl + drag on empty space in an Explorer folder to add to the selection: the selection
      works, no donut, Explorer stays responsive afterwards.
- [ ] Ctrl+drag of folders only: no donut, nothing copied.
- [ ] Ctrl+drag starting outside Explorer (desktop, browser, another application): unaffected.
- [ ] **Multi-monitor**: start a Ctrl+drag on monitor 1 and drop on monitor 2 (outside the donut): nothing copied.
      Start one on monitor 2 (secondary, other DPI): the donut opens there and a drop on a slice converts.
- [ ] Disable Ctrl+drag and Save: Ctrl+drag copies files normally again within ~2 s.

Agent lifecycle and resources

- [ ] Disable both C and D (or the master switch) and Save: the agent exits and the Run value is gone within ~2 s.
- [ ] Log off / on with C or D enabled: the agent starts (tray icon), hotkey and Ctrl+drag work.
- [ ] Starting `FileConverterPlusPlus.exe --agent` a second time: still a single agent process.
- [ ] Idle agent: under 50 MB private memory (Task Manager, after opening and closing the donut a few times), ~0 % CPU.

Uninstall and upgrade

- [ ] With the agent running, uninstall from Settings → Apps: no "files in use" prompt, the agent process is gone, the
      Run value is removed, the installation folder is deleted.
- [ ] Upgrade (install a newer MSI over this one) with C or D enabled: after the install the agent runs from the new
      version and the Run value points to the installed `FileConverterPlusPlus.exe`.
