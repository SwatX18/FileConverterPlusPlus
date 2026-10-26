# Performance settings and diagnostics (Phase 5)

![Settings → Performance](images/settings-performance.png)

## Settings → Performance

The settings added by Phases 1 to 4 were in the Application tab, or had no UI. They are now grouped in a Performance
tab (`Views/SettingsWindow.xaml`); the Application tab keeps language, exit, update and clipboard options.

| Group | Setting | Default | Details |
|---|---|---|---|
| Video | Hardware acceleration mode (`HardwareAccelerationMode`) | Auto | `docs/HARDWARE-ENCODING.md` |
| Video | Detected hardware encoders | — | One line per vendor, in priority order, with the GPU names and codecs, e.g. `NVIDIA NVENC (NVIDIA GeForce RTX 5070 Ti): H.264, HEVC, AV1` / `AMD AMF (AMD Radeon(TM) Graphics): H.264, HEVC`. "None: videos are encoded by the CPU" when no test encode succeeds. |
| Video | Re-detect | — | Runs the test encodes again (`IHardwareEncoderDetector.Redetect`) and rewrites `hw-cache.json`, so the next conversions use the new result. Runs in the background; the button is disabled and the list shows "Detecting..." meanwhile. |
| Video | Copy streams without re-encoding when possible (`CopyStreamsWhenPossible`) | on | `docs/REMUXING.md` |
| Images | Image engine (`ImageEngine`) | Auto | `docs/IMAGES.md`. Below it: "libvips 8.18.7 is available", or why libvips can't be loaded (then ImageMagick converts every image). |
| Simultaneous conversions | Maximum number of simultaneous conversions (`MaximumNumberOfSimultaneousConversions`) | Auto (one per physical core) | `docs/SCHEDULING.md` |
| Simultaneous conversions | GPU video encodes (`MaximumNumberOfSimultaneousGpuConversions`) | 4 | 1 to 8. GPU lane limit and GPU session limiter. |
| Simultaneous conversions | CPU video encodes (`MaximumNumberOfSimultaneousCpuVideoConversions`, new) | Auto (physical cores / 4, within 2..4) | 0 = Auto, up to the number of physical cores. A missing element means Auto: no migration. |
| Diagnostics | Open the diagnostics folder | — | Opens `%LOCALAPPDATA%\FileConverter`, which holds a `Diagnostics-<time>` folder per run (kept one day). |

The GPU names come from WMI (`Win32_VideoController`), matched to the encoder vendor by PCI vendor id (NVIDIA 10DE,
AMD 1002, Intel 8086): `Hardware/HardwareEncoderSummary.cs`. The detection itself is unchanged: an encoder is listed
only if its test encode succeeded, so a GPU without AV1 (the Radeon iGPU above) doesn't list it.

Strings are in English and French (`Properties/Resources.resx`, `Resources.fr-FR.resx`); other languages fall back to
English. The French file also gets the hardware acceleration mode strings, which it was missing since upstream.

## Diagnostics

Each job ends its log with the path that converted it and how long it took, and the queue thread logs a summary:

```
Job done: path vips, 0.17 s (Light, 10 threads): ...\img_002.jpg
Job done: path imagemagick, 2.07 s (Light, 10 threads): ...\canon_5d3.CR2
Job done: path amf-cpu-decode, 6.50 s (Gpu, 16 threads): ...\h264_422_10bit.mkv
Conversions finished in 9.43 s: 2 done, 0 failed, paths: amf-cpu-decode x2.
```

The duration runs from the start of the conversion (after preparation) to the end, fallbacks included. The thread
count is the job's budget (`docs/SCHEDULING.md`); hardware encodes don't use it.

| Path | Meaning |
|---|---|
| `remux` | Streams copied without re-encoding (`docs/REMUXING.md`) |
| `nvenc` | NVDEC + CUDA filters + NVENC |
| `nvenc-cpu-filters` | NVENC with CPU filters (rotation, pixel formats the CUDA pipeline doesn't take) |
| `amf`, `qsv` | Hardware decode + AMF / Quick Sync encode |
| `amf-cpu-decode`, `qsv-cpu-decode` | CPU decode + AMF / Quick Sync encode |
| `cpu` | Software encode (hardware acceleration Off, or no hardware encoder for the codec) |
| `cpu-fallback` | Software encode after the hardware attempts failed |
| `ffmpeg` | Single-path ffmpeg jobs (audio, custom presets) |
| `vips` | Image converted by libvips |
| `imagemagick` | Image converted by ImageMagick because libvips doesn't handle it (format, setting) |
| `imagemagick-fallback` | Image converted by ImageMagick after libvips failed |
| `word`, `excel`, `powerpoint`, `extractcda`, `ico`, `gif` | Other job types (one path each) |

A failed job reports the last path it tried. Each fallback is also logged when it happens
(`Encoding path 'amf' failed (...). Falling back to 'amf-cpu-decode'.`, `Image engine: imagemagick-fallback (...)`).
`bench/try-case.ps1` prints these lines.

## Settings → Radial menu (settings version 7)

A Radial menu tab, after Performance, holds the optional radial "donut" menu (design: `docs/ARCHITECTURE.md` §8). Every
option is off by default, and the options below the master switch are greyed out while it is off.

| Setting (UI) | Element in `Settings.user.xml` | Default | Details |
|---|---|---|---|
| Enable the radial menu | `RadialMenuEnabled` | `false` | Master switch. Off: context menu, processes and autostart are exactly as without the feature, whatever the other elements say. |
| Explorer context menu: Add a radial menu entry (trigger A) | `RadialMenuExtraContextMenuEntry` | `false` | "FileConverter++ (radial menu)…" next to the usual submenu. |
| Explorer context menu: Replace the preset submenu with the radial menu (trigger B) | `RadialMenuReplaceContextMenu` | `false` | The FileConverter++ entry becomes a single item that opens the radial menu. |
| Open with a global hotkey (trigger C) | `RadialMenuHotkeyEnabled` | `false` | Needs the tray agent. |
| Hotkey | `RadialMenuHotkey` | `Ctrl+Alt+C` | Canonical text, see below. |
| Open when dragging files from Explorer with Ctrl held (trigger D) | `RadialMenuCtrlDragEnabled` | `false` | Needs the tray agent. Tooltip: while enabled, Ctrl+drag copies between Explorer windows are captured by the radial menu. |

**A and B are exclusive.** The UI shows them as one combo box (No radial menu entry / Add a radial menu entry / Replace
the preset submenu with the radial menu), backed by the `[XmlIgnore]` property `RadialMenuContextMenuMode`, which writes
both elements. If a hand-edited file sets both to `true`, **B wins**: the application clears A when it loads the file
(`Settings.OnDeserializationComplete`), and the shell extension, which reads the file itself, resolves the same way
(`ExtensionSettings.ResolveMenuMode`: master off → classic menu; B → radial entry only; A → classic menu plus the radial
entry).

**Hotkey format**: `+`-separated, case-insensitive modifiers `Ctrl` (or `Control`), `Alt`, `Shift`, `Win` (or `Windows`)
and exactly one key (a `System.Windows.Input.Key` name, digits `0`-`9`). Ctrl, Alt or Win is required (`Shift+F5` is
rejected). Stored in canonical order, e.g. `Ctrl+Alt+Shift+Win+C`. In the Settings box, press the combination to
record it; Backspace or Delete (without modifier) resets it to `Ctrl+Alt+C`; a key without Ctrl, Alt or Win is ignored.
While the master switch and the hotkey are on, an invalid hotkey blocks Save with "Invalid hotkey. Use Ctrl, Alt or Win
with a key". In the file, an empty hotkey is replaced by `Ctrl+Alt+C`; an invalid non-empty one is kept, and the agent
logs it, shows a tray warning and leaves trigger C inactive. The same warning appears when another application already
owns the combination.

**Tray agent**: it runs only when `RadialMenuEnabled` and (`RadialMenuHotkeyEnabled` or `RadialMenuCtrlDragEnabled`)
(`Settings.ShouldRunRadialAgent`). Saving the settings applies it immediately: the HKCU Run value
`FileConverterPlusPlus.RadialAgent` is written or removed, and the agent is started, told to reload (a new hotkey takes
effect without restart) or told to exit. A and B need no agent.

**Migration**: version 6 files have none of these elements; they load with the defaults above (all off), the
`settingsVersion <= 6` migration step has nothing to change, and the presets are untouched. The file is saved as
version 7 with the six elements.
