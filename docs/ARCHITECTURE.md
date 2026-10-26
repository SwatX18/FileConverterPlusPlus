# File Converter architecture (as of upstream `integration` @ 6c157a4, v2.2)

This is a discovery summary for the `fast-convert` fork (see `PLAN.md`). Paths are relative to `Application/FileConverter/` unless noted.

## 1. Process model and entry point

- The shell extension (`Application/FileConverterExtension`, SharpShell) builds the right-click menu from the presets in `%LOCALAPPDATA%\FileConverter\Settings.user.xml`. It then launches
  `FileConverterPlusPlus.exe --conversion-preset "<preset name>" --input-files <tmp list>` (or with the paths as plain arguments).
- `Application.xaml.cs` `Initialize()` parses the arguments, then `RunConversions()` creates one `ConversionJob` per file with `ConversionJobFactory.Create()`, registers it with `ConversionService`, and calls `ConvertFilesAsync()`.
- At the end, the app exits by itself only if `Settings.ExitApplicationWhenConversionsFinished` is true, after `DurationBetweenEndOfConversionsAndApplicationExit` seconds (0–10). The default is **false**.
- The app resolves its install directory, and therefore `Settings.default.xml`, from `HKCU\Software\FileConverter\Path`, which the installer writes. User data and diagnostics logs live in `%LOCALAPPDATA%\FileConverter\` (not `%APPDATA%`).
- Services are registered with `Microsoft.Extensions.DependencyInjection` and resolved through `CommunityToolkit.Mvvm` `Ioc.Default` (`ISettingsService`, `IConversionService`, `INavigationService`, `IUpgradeService`).

## 2. ConversionJob hierarchy

```
ConversionJob                       (ConversionJobs/ConversionJob.cs)
├── ConversionJob_FFMPEG            (.cs + .Converters.cs, partial): all audio/video, plus Png/Jpg via ffmpeg when used as a sub-job
├── ConversionJob_ImageMagick       Jpg / Png / Webp / Avif / Pdf outputs (Magick.NET Q16)
├── ConversionJob_Gif               composite: [ImageMagick → png] → FFMPEG (palettegen + paletteuse, 2 passes)
├── ConversionJob_Ico               composite: ImageMagick → png (clamped to a power of 2, ≤256) → FFMPEG → ico
├── ConversionJob_ExtractCDA        audio CD tracks (Ripper.dll / yeti.mmedia) → wav → FFMPEG sub-job
└── ConversionJob_Office (abstract) Office COM automation through NetOffice
    ├── ConversionJob_Word
    ├── ConversionJob_Excel
    └── ConversionJob_PowerPoint
```

**Lifecycle** (`ConversionJob.cs`):

1. `PrepareConversion()` checks input/output compatibility (`Helpers.IsOutputTypeCompatibleWithCategory`), generates unique output paths, creates folders, handles the "output == input" case, sets the `CdDriveExtraction` flag, then calls the virtual `Initialize()` (FFMPEG builds its argument list here). State becomes `Ready`.
2. `StartConversion()` sets state `InProgress`, then calls the virtual `Convert()`. Any exception turns into `ConversionFailed(message)`.
3. `OnConversionSucceed()` copies the input's timestamps to the output, applies the post-conversion action on the input (none / move to archive / delete), and sets state `Done`. `OnConversionFailed()` deletes partial outputs.
4. `Cancel()` sets `CancelIsRequested`. Each job polls it: FFMPEG kills the process, ImageMagick cancels through its progress event.

**Job selection** (`ConversionJobFactory.Create`) is evaluated top to bottom. It looks at the **input extension** first, then the **output type**:

| Condition | Job |
|---|---|
| input `cda` | ExtractCDA |
| input `doc/docx/odt` / `xls/xlsx/ods` / `ppt/pptx/odp` | Word / Excel / PowerPoint |
| output `Ico` | Ico |
| output `Gif` | Gif |
| output `Pdf`, `Avif`, `Jpg`, `Png`, `Webp` | ImageMagick |
| anything else (`Aac, Avi, Flac, Mkv, Mp3, Mp4, Ogg, Ogv, Wav, Webm`) | FFMPEG |

So image routing depends only on the **output** type, and any image input (RAW, HEIC, PSD, SVG, PDF…) goes through ImageMagick. A future `ConversionJob_Vips` slots in at the `Jpg/Png/Webp/Avif` branch, with an input-extension check added in front.

**Output types** (`OutputType.cs`): `Aac, Avi, Avif, Flac, Gif, Ico, Jpg, Mkv, Mp3, Mp4, Ogg, Ogv, Pdf, Png, Wav, Webm, Webp`. There is **no** `Mov`, `M4v`, `M4a`, `Tiff` or `Heic` output. The enum value is serialized by name into `Settings.user.xml` and read by the shell extension.

### FFMPEG job details

- `FillFFMpegArgumentsList()` has one `switch (OutputType)` branch per output, building one or more `FFMpegPass` (Gif uses 2 passes). All passes start with `-n -progress pipe:1`.
- A per-preset **custom command** (`EnableFFMPEGCustomCommand`) replaces everything: `-i in <custom> out`.
- Mp4/Mkv branch: `libx264 -preset <speed> -crf (51 - VideoQuality)`, audio `aac -qscale:a`, `-vf` from `ComputeTransformArgs` (scale with even dimensions, transpose, `format=yuv420p`). There is **no** `-map`, so ffmpeg's default stream selection keeps only one video and one audio stream and drops subtitles. There is no `-movflags +faststart`.
- `Convert()` starts the process, reads **only stderr** line by line, parses `Duration:` and the `size=… time=…` stats lines for progress, and flags failure when a line contains `Error`, `Exiting.`, `Unsupported dimensions` or `No such file or directory` (file names are stripped first). **The ffmpeg exit code is never checked.**
- ⚠️ **Deadlock bug (shipped in v2.2):** stdout is redirected (`RedirectStandardOutput = true`) and receives `-progress pipe:1` key/value blocks, but nothing reads it. Once the pipe buffer fills, after about 10 s of encoding, ffmpeg blocks on write and the job hangs forever at a frozen progress value. Reproduced in isolation with the same `ProcessStartInfo` (4K x264 medium: stderr stopped after 10.3 s of encoding). This matches open upstream issues #700, #722, #739, #774 (which diagnoses it), #780 and #800. Any conversion that runs longer than about 10 s is affected. See §7.

## 3. Hardware acceleration (the AMF mode as the model)

> Upstream state. The fork's implementation (Auto mode, detection, fallback, calibration) is described in `docs/HARDWARE-ENCODING.md`.

| Piece | Location |
|---|---|
| Enum `HardwareAccelerationMode { Off, CUDA, AMF }` | `Helpers.cs` (bottom) |
| Global setting (not per preset), `[XmlElement]`, default `Off` | `Settings.cs` `HardwareAccelerationMode` |
| Mode list shown in the combo box | `ViewModels/SettingsViewModel.cs` field `hardwareAccelerationModes` |
| UI combo box | `Views/SettingsWindow.xaml` around line 404 |
| Display names | `ValueConverters/HardwareAccelerationModeToString.cs` + `Resources.resx` keys `HardwareAccelerationModeOffName/CUDAName/AMFName`. Unused keys `D3D11/DXVA2/OpenCL/Vulkan` also exist. |
| Command generation | `ConversionJob_FFMPEG.cs`, Mp4/Mkv branch only, and `ComputeTransformArgs(preset, hwAccel)` |
| Speed mappings | `ConversionJob_FFMPEG.Converters.cs`: `H264EncodingSpeedToNVENCPreset` (p1…p7), `H264EncodingSpeedToAMFQuality` (speed/balanced/quality) |

Current behavior:

- **CUDA**: `-hwaccel cuda -hwaccel_output_format cuda` on input, `h264_nvenc -preset pN -rc constqp -qp (51-Q)`, and the filter **always** includes `scale_cuda=…:format=yuv420p`, even at scale 1.0. Rotation adds a CPU `transpose` after `scale_cuda` on GPU frames without `hwdownload`, so CUDA + rotate presets will most likely fail. A source NVDEC can't decode makes ffmpeg fall back to CPU frames, and then `scale_cuda` fails. Neither case is caught by a fallback.
- **AMF**: CPU decode, `h264_amf -usage transcoding -quality <q> -qp_i/-qp_p Q -qp_b Q+2`, CPU `format=yuv420p`.
- Only Mp4/Mkv use the setting. WebM/Ogv/Avi/Gif always use the CPU. There is no detection: picking CUDA on a machine without NVIDIA simply fails the conversion.

To add **Auto**: append `Auto` to the enum (serialized by name, so this stays backward compatible), add it to `hardwareAccelerationModes`, the converter and both resx files (en, fr-FR), and branch in the Mp4/Mkv case. **Migration caveat:** `XmlSerializer` writes every property, so existing `Settings.user.xml` files already contain `<HardwareAccelerationMode>Off</HardwareAccelerationMode>`. Changing the C# default to `Auto` only affects fresh installs, unless `Settings.Version` is bumped (4 → 5) and `SettingsService.Migration.cs` rewrites `Off → Auto`. That needs an explicit decision, because some users chose Off on purpose.

## 4. Job queue and concurrency

`Services/ConversionService.cs`:

- `numberOfConversionThread` = `Settings.MaximumNumberOfSimultaneousConversions`. It is **2** in `Settings.default.xml`. If ≤ 0, it becomes `max(1, Environment.ProcessorCount / 2)` (16 on the 7950X3D, which matches the physical cores). It is read once, in the constructor of a singleton service.
- `ConvertFiles()` runs on a dedicated thread:
  1. It runs `PrepareConversion()` on **all** jobs serially before starting any of them. For PDF inputs this includes reading the whole PDF to count pages.
  2. Polling loop: compute the OR of the `StateFlags` of running jobs. Find the **first** `Ready` job whose `CanStartConversion(flags)` is true (only the CD-drive flag is ever used, to serialize CD reads). Give it a free slot in a fixed `Thread[]` (a new `Thread` per job), spin with `Sleep(20)` until its state leaves `Ready`, then `break`. Then `Sleep(50)`.
  3. So **at most one job starts per loop iteration, which caps starts at about 15–20 per second**. For 200 small images that is ≥ 10 s of pure scheduling latency, which becomes significant once libvips makes each image take ~100 ms (Phase 3/4).
- There is no notion of job class (GPU / CPU / IO): one global limit applies to everything. The ffmpeg CPU jobs don't get `-threads`. ImageMagick uses OpenMP across all cores per image.
- `ConversionJob.State`'s setter does a synchronous `Application.Current.Dispatcher.Invoke` (UI thread round-trip) on every state change.

## 5. Middleware (`Middleware/`)

| File | Notes |
|---|---|
| `ffmpeg/ffmpeg.exe` | **ffmpeg 8.0.1 essentials_build, www.gyan.dev** (99 MB, static). Copied next to the exe by the post-build event. |
| `gs/gswin64c.exe`, `gs/gsdll64.dll` | Ghostscript 10.08.0 (AGPL), used by Magick.NET for PDF. Source, hashes and license: `gs/README.md`, `gs/LICENSE` (installed as `ghostscript-*`). |
| `vcruntime/*.dll` | Visual C++ 14.44 runtime (`msvcp140`, `vcruntime140`, `vcruntime140_1`), needed by Ghostscript and installed next to the exe so that no Visual C++ Redistributable is required (`vcruntime/README.md`). |
| `Markdown.Xaml.dll`, `Ripper.dll`, `yeti.mmedia.dll` | Help/changelog rendering, CD ripping |
| **`ffprobe.exe`** | **Not present upstream.** Added in the fork: `Middleware/ffmpeg/ffprobe.exe` from the same gyan.dev 8.0.1 essentials build (its `ffmpeg.exe` has the same SHA-256 as the bundled one), plus a post-build copy line in `FileConverter.csproj` and a `<File>` in `Installer/Product.wxs`. |

Magick.NET (Q16-AnyCPU 14.17.2) comes from NuGet (`Magick.Native-Q16-x64.dll`).

**Installer:** `Installer/Product.wxs` lists **every file by hand** (one `<Component>`/`<File>` each, sourced from `$(var.FileConverter.TargetDir)`). Only `Languages\` is harvested. Any new binary (ffprobe.exe, NetVips.dll, libvips-42.dll) needs an explicit entry there.

### Bundled ffmpeg encoders (`ffmpeg -encoders`)

The essentials build has every hardware encoder family: `h264/hevc/av1_nvenc`, `h264/hevc/av1_amf`, `h264/hevc/av1_qsv` (via libvpl), `*_mf` (MediaFoundation), plus `libx264`, `libx265`, `libaom-av1`, `libvpx(-vp9)`, `libwebp`. It does **not** include `libsvtav1` or `libdav1d`: AV1 decoding falls back to libaom or `av1_cuvid`.

PLAN.md's test encode (`-f lavfi -i color=black:s=256x256:d=0.1 -frames:v 1 -c:v <enc> -f null -`) on the dev machine (RTX 5070 Ti, driver 610.88, Radeon iGPU):

| Encoder | Exit code | |
|---|---|---|
| h264_nvenc / hevc_nvenc / av1_nvenc | 0 / 0 / 0 | ✅ |
| h264_amf / hevc_amf | 0 / 0 | ✅ (iGPU) |
| av1_amf | 8 | ❌ (the Raphael RDNA2 iGPU has no AV1 encode) |
| h264_qsv / hevc_qsv / av1_qsv | 171 | ❌ (no Intel GPU) |

The detection method works as designed: exit codes separate available and unavailable encoders cleanly.

## 6. Target framework and NetVips

- Both projects target **.NET Framework 4.8** (legacy csproj with `PackageReference`). The solution only builds **x64** (`Release|x64`, `Debug|x64`). The x86 property groups in `FileConverter.csproj` are dead.
- **NetVips 3.2.0** ships `lib/net462` (dependency: System.Buffers, already present), so it is ✅ compatible with net48.
- **NetVips.Native.win-x64 8.18.7** ships a single `runtimes/win-x64/native/libvips-42.dll` and **no MSBuild targets**. Legacy .NET Framework projects with PackageReference usually do copy `runtimes/<rid>/native` assets when `PlatformTarget=x64`, but this must be verified in Phase 3. If it doesn't work, add an explicit copy in the post-build event, like ffmpeg. The DLL must also be added to `Product.wxs`.
- libvips build contents (`versions.json`): mozjpeg, libpng, libwebp, libtiff, cgif, **libheif 1.23.5 with aom only**, librsvg, lcms, libexif, imagequant, uhdr.
  - ✅ JPEG, PNG, WebP, TIFF, GIF, **AVIF** (read/write), SVG (read)
  - ❌ **HEIC** (no libde265/x265 decoder), ❌ RAW (no libraw), ❌ PDF (no pdfium/poppler), ❌ JXL. All of these stay on ImageMagick, as PLAN.md anticipates.

## 7. Contradictions and complications relative to PLAN.md

1. **The baseline has a hang bug (high impact).** As shipped, any ffmpeg job longer than about 10 s deadlocks (§2). The "unmodified baseline" for real-world video conversions is therefore "hangs", not a duration. A one-line class of fix (drain stdout asynchronously with `BeginOutputReadLine`, or drop `-progress pipe:1`, since progress is parsed from stderr anyway) must come before any video speed comparison is meaningful. It is also a good first upstream PR.
2. **Failure detection is string-based, with no exit code check.** Phase 1.3's automatic CPU fallback needs a reliable failure signal: check `ExitCode != 0` and capture the last stderr lines. Hardware encoders print warnings that can contain "Error" without failing, and the opposite also happens.
3. **The CUDA mode already exists (v2.1)** but uses `-rc constqp -qp`, not PLAN's `-rc vbr -cq … -b:v 0`. Switching changes output sizes for current CUDA users. Calibrate against the libx264 CRF baseline as planned. The existing `scale_cuda` / rotation interaction is already broken (§3).
4. *(Resolved in the fork: redefined as "To Aac", see `docs/REMUXING.md`.)* **Output types missing from the remux table:** there is no `Mov`, `M4v`, `M4a` or `Tiff` output. The "M4A audio extraction via stream copy" exit criterion needs a new `OutputType.M4a` (enum, presets in `Settings.default.xml`, `IsOutputTypeCompatibleWithCategory`, converters, icons, resx), or it can be redefined as "To Aac" (ADTS) via `-c:a copy`. The same applies to the Phase 1 HEVC/AV1 "extra presets": adding a per-preset `VideoCodec` setting key is less invasive than new OutputTypes.
5. *(Resolved in the fork: only the remux attempt maps streams, one by one, see `docs/REMUXING.md`.)* **`-map 0` changes behavior.** Today Mp4/Mkv keep one video and one audio stream and drop subtitles. Phase 2's `-map 0` keeps all tracks (multi-language audio, subtitles, attachments, data/timecode tracks in iPhone MOVs that MP4 can't hold). That is probably desirable, but it is a visible change and needs exclusion rules (`-map -0:d`, `-map -0:t` for MP4, and so on).
6. *(Resolved in the fork: settings v5 (Auto acceleration) and v6 (Auto simultaneous conversions) migrations.)* **New defaults won't reach existing users.** `MaximumNumberOfSimultaneousConversions` (2) and `HardwareAccelerationMode` (Off) are persisted in each user's `Settings.user.xml`. Phase 1's "Auto by default" and Phase 4's "core count by default" need a settings version bump and migration, and a decision on whether to override values users set explicitly.
7. **Cache location:** PLAN says `%APPDATA%\FileConverter\hw-cache.json`, but the app's data folder is `%LOCALAPPDATA%\FileConverter\`. Use the latter (`PathHelpers.GetUserDataFolderPath`).
8. **Phase 3 x86:** there is no x86 build in the solution, so drop `NetVips.Native.win-x86`. PLAN's "TIFF compression" output mapping has no TIFF output to apply to (TIFF is input-only).
9. **Image resize semantics:** ImageMagick uses `image.Scale(percentage)` (box filter), and "maximum size" uses `Scale(w,h)`. *(Corrected in Phase 3: `Scale(w,h)` keeps the aspect ratio, it fits the image in the box; verified, see `docs/IMAGES.md`.)* `Image.Thumbnail` (lanczos3) will give *better* but not bit-identical results, so the SSIM ≥ 0.98 target is reasonable but shouldn't be judged against byte equality.
10. *(Resolved in the fork: event-driven scheduler with lanes, see `docs/SCHEDULING.md`.)* **The scheduler is a polling loop** that starts at most one job per ~50–70 ms (§4). Phase 4 should replace it, otherwise fast image batches stay scheduler-bound.
11. *(Resolved in the fork: Scale presets now use the `VideoResolution` setting, see `docs/HARDWARE-ENCODING.md`.)* **Several default presets are ffmpeg custom commands** that hard-code the encoder: all `Scale 720p/…` and `Scale 1080p/…` presets (`-c:v libx264 -preset medium -crf 21 … -vf scale=1280:720`), and the `Tempo - Pitch/…` presets. Custom commands bypass the whole argument builder, so Phase 1 (GPU encoding) and Phase 2 (remux) won't apply to them as written. Either convert these presets to regular settings (needs a "target height" setting, since `VideoScale` is a factor), or rewrite the encoder inside custom commands (fragile). Users' own custom commands should be left alone.
12. **No test project exists.** Phase 6 has to create one (the legacy csproj style makes an SDK-style test project referencing a net48 exe slightly awkward. Consider moving testable logic into a class library).
13. `Installer.sign` must be stubbed locally to build Release (see `docs/BUILD.md`).

## 8. Radial menu (FileConverter++)

> Fork addition, not upstream. Optional "donut" menu, every trigger off by default (settings: `docs/SETTINGS.md`). Code: `RadialMenu/`, `RadialMenu/Agent/`, `Views/RadialMenuWindow.xaml(.cs)`, `Application/FileConverterExtension/ExtensionSettings.cs`. Clean-room implementation inspired by Filee (MIT).

Four triggers: **A** extra context-menu entry, **B** context-menu entry replaced by the donut, **C** global hotkey, **D** Ctrl+drag from Explorer. A and B go through the shell extension and need no background process; C and D need the per-user tray agent.

### Process model

All modes are the same `FileConverterPlusPlus.exe`, chosen in `Application.xaml.cs` `Initialize()`:

| Command line | Started by | What it does |
|---|---|---|
| `--radial [--input-files <list.txt>] [file ...]` | Shell extension (A, B) | Shows the donut at the cursor (click mode). On a pick: `RunConversions(files, preset.FullName)` then `StartConversions()` in the same process, i.e. the main window and the same pipeline as `--conversion-preset`; the donut closes after the main window is shown. Esc / click outside / focus loss: the window closes and the process exits, nothing converted. The upgrade check and the hardware encoder warm-up live in `RunConversions`, so they only run after a pick. Logs `Radial menu shown in N ms (from process start).` |
| `--agent` | Settings save, post-install initialization, HKCU Run value at logon | Tray agent for C and D (`RadialMenu/Agent/RadialAgent.cs`, `ShutdownMode.OnExplicitShutdown`, no main window). A pick starts a **new** process `--conversion-preset "<FullName>" "<file>" ...` (or `--input-files <tmp list>` once the command line reaches 8000 characters, the list is deleted when that process exits): `ConversionProcessLauncher`, same command line as the shell extension. The agent never converts. Logs `Radial menu shown in N ms (from trigger).` |
| `--stop-agent` | Installer, on uninstall | Sets the Exit event, waits up to 2 s for the agent's mutex to disappear, deletes the Run value, exits. No UI. |

DPI: for `--radial` and `--agent` only, the static constructor of `FileConverter.Application` calls `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before any window exists, and `App.config` sets `Switch.System.Windows.DoNotScaleForDpiChanges=false`. The other modes keep their DPI behavior.

### Shell extension (A, B)

The extension now deserializes a subset of `Settings.user.xml` into `ExtensionSettings` (root `Settings`): the presets as before, plus `RadialMenuEnabled`, `RadialMenuExtraContextMenuEntry`, `RadialMenuReplaceContextMenu` and `ApplicationLanguageName`. Missing elements (older files) keep their default, off.

| Master | A (extra entry) | B (replace) | `ExtensionMenuMode` | Menu |
|---|---|---|---|---|
| off | any | any | `Classic` | Unchanged |
| on | any | on | `RadialOnly` | One non-cascading "FileConverter++" item with the app icon (B wins over A) |
| on | on | off | `ClassicWithRadialEntry` | Unchanged cascade, then "FileConverter++ (radial menu)…" ("FileConverter++ (menu radial)…" when `ApplicationLanguageName` starts with `fr`) |
| on | off | off | `Classic` | Unchanged |

Both radial items run `LaunchFileConverter("--radial")`, the old `ConvertFiles` refactored to take the mode arguments (`--conversion-preset "<name>"` for the classic items), with the same 8000-character switch to `--input-files`. `CanShowMenu` is unchanged, and the extension does nothing but start the process, so Explorer is never blocked by the donut.

### Donut window (`Views/RadialMenuWindow.xaml.cs`)

- Borderless, transparent, topmost, not in the taskbar, 320 DIP square (`RadialGeometry`: outer radius 150, hub radius 55). Not routed through `NavigationService`.
- Placement in physical pixels: `MonitorFromPoint(cursor)`, `GetMonitorInfo` (work area) and `GetDpiForMonitor` give the size in pixels, `RadialGeometry.ClampToWorkArea` centers it on the cursor and moves it inside the work area, `SetWindowPos` applies it before the first frame.
- Presets: `PresetCompatibility.GetPresetsCompatibleWithAll` keeps a preset (in its original order) only if its input types contain the extension of **every** selected file; files without an extension are ignored, and a selection with no extension at all gives nothing. Empty result: "No compatible preset" in the hub.
- Pages of 8 slices (`RadialPager`), wrapping, indicator `1/3` in the hub only when there is more than one page. Slice 0 is centered at 12 o'clock, clockwise. A slice shows the preset's short name; hovering shows its full name in the hub.
- **Click mode** (A, B, C): the window is activated. Esc, a click on the window outside the slices and the hub, or deactivation dismisses it. Tab / Shift+Tab and the mouse wheel change pages.
- **Drop mode** (D): not activated, `AllowDrop`, no dismissal on deactivation. `DragOver` answers `Copy` over a slice (highlighted) and `None` elsewhere. `Drop` on a slice sets `Effects = None` and raises `PresetChosen`. Paging: hovering the hub for 600 ms shows the next page; since the menu opens with the cursor on the hub, this is only armed after the cursor has left the hub, and each flip needs a new exit and re-entry.
- In the agent the window is created once, hidden (`EnsureHandle`), and hidden instead of closed (`HideInsteadOfClose`), so showing it is cheap.

### Tray agent and IPC

Names (`RadialMenu/RadialAgentNames.cs`), all per user session:

| Name | Kind | Use |
|---|---|---|
| `Local\FileConverterPlusPlus.RadialAgent` | Mutex | Owned by the agent for its whole life: single instance, and "is it running" (`Mutex.TryOpenExisting`) |
| `Local\FileConverterPlusPlus.RadialAgent.Reload` | Auto-reset event, created by the agent | Re-read the settings |
| `Local\FileConverterPlusPlus.RadialAgent.Exit` | Auto-reset event, created by the agent | Exit |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `FileConverterPlusPlus.RadialAgent` | Registry string | `"<exe path>" --agent`, autostart at logon |

Agent start (`RadialAgent.Start`): take the mutex, or, if another agent owns it, set its Reload event and exit. Create both events and wait on them with `ThreadPool.RegisterWaitForSingleObject`, marshalled to the UI dispatcher. Load the settings; if `ShouldRunRadialAgent` is false (master off, or neither C nor D), remove the Run value and exit. Otherwise create the tray icon (Settings → starts `--settings`, Exit), the hidden donut, and the triggers, then trim the working set (`SetProcessWorkingSetSize(-1, -1)`, again after each dismiss).

Reload: `ISettingsService.RevertSettings()` (re-reads the file), then the triggers are re-applied: hotkey re-registered, mouse hook installed or removed. If nothing is left to run, the agent removes the Run value and exits.

The settings process drives it with `RadialAgentController.ApplySettings(settings, exePath)`, called after every Settings save (`SettingsViewModel`) and at the end of `PostInstallationInitialization` (an upgrade can change the executable path). It writes or removes the Run value according to `ShouldRunRadialAgent`, then:

| Should run | Agent running | Action |
|---|---|---|
| yes | no | Start: `Process.Start(exe, "--agent")` |
| yes | yes | Reload: set the Reload event |
| no | yes | Stop: set the Exit event |
| no | no | None |

Installer (`Installer/Product.wxs`): custom action `StopRadialAgent` runs `--stop-agent` (immediate, `Return="ignore"`), before `InstallValidate`, when `REMOVE~="ALL"`, so the agent is gone before its files are removed. Since the names are per session and the Run value is in HKCU, it stops the agent and removes the Run value of the user running the uninstall only.

### Hotkey (C)

`HotkeyListener`: `RegisterHotKey` on a message-only `HwndSource` (`HWND_MESSAGE`), with `MOD_NOREPEAT`. An unparsable hotkey or a failed registration (combination already taken) is logged and shown as a tray balloon; trigger C stays inactive, D is unaffected.

On `WM_HOTKEY`, `ExplorerSelection.TryGetSelectedFiles(GetForegroundWindow())`:

1. Only if the foreground window's class is `CabinetWClass` (an Explorer window; the desktop and other applications are ignored).
2. On a dedicated STA thread with a 1000 ms timeout (shell COM calls can hang while Explorer is busy).
3. Windows 11 tabs: the active tab is the visible `ShellTabWindowClass` child of the window. Each `Shell.Application.Windows()` item is matched to it through `IServiceProvider` → `SID_STopLevelBrowser` → `IShellBrowser.GetWindow()`.
4. `Document.SelectedItems()` paths, existing files only (folders are dropped). No file: nothing happens.

### Ctrl+drag (D)

Explorer starts an OLE drag-and-drop (`DoDragDrop`) with the files; the agent has to see it start, show the donut, and make sure the drop does nothing.

1. **Detection**, `CtrlDragDetector`: a low-level mouse hook (`WH_MOUSE_LL`, no DLL injection) installed on its own background thread with its own `GetMessage` loop, so the callback never waits for the UI thread (a slow low-level hook delays every mouse event of the session, and Windows skips it after a timeout). The callback always calls `CallNextHookEx`, never swallows an event, and catches every exception. Installed only while D is enabled (removed by posting `WM_QUIT` to the thread).
   - `WM_LBUTTONDOWN`: remember the point, and whether `GetAncestor(WindowFromPoint(pt), GA_ROOT)` is a `CabinetWClass` window.
   - `WM_MOUSEMOVE` with the button down on Explorer, moved beyond `SM_CXDRAG` / `SM_CYDRAG`, and Ctrl down (`GetAsyncKeyState`): raise `CtrlDragStarted` once per press, on the UI dispatcher. Ctrl may be pressed after the drag has started.
   - `WM_LBUTTONUP` after a start: raise `MouseReleased`.
2. **Drop shields**: on `CtrlDragStarted`, one `DropShieldForm` per screen (`Screen.AllScreens` bounds): borderless WinForms window, topmost, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, opacity 0.004 (invisible but still a drop target), `AllowDrop`. Its `DragEnter` and `DragOver` always answer `DragDropEffects.None`, so no window below can receive the files.
3. **Menu**: the first `DragEnter` on a shield proves that an OLE file drag is in progress. If its data has `DataFormats.FileDrop` with at least one existing file, the donut opens at the cursor in drop mode, above the shields, with those files.
4. **Drop**: over a slice the donut answers `Copy` (so the cursor shows a valid target); on drop it sets `Effects = None`, so Explorer's `DoDragDrop` gets `DROPEFFECT_NONE` and does **no file operation**. The agent then starts the conversion process. A drop anywhere else lands on a shield, which refuses it.
5. **Teardown**: on `MouseReleased`, a 150 ms grace timer lets the OLE drop reach the donut first, then the shields are hidden and disposed and the donut is dismissed if it was shown for this drag.

Consequences:

- A plain drag (no Ctrl) and any drag starting outside an Explorer window are not affected. Right-button drags are not watched.
- A Ctrl+drag starting in Explorer can't copy files anywhere while D is on: this is the documented trade-off (Settings tooltip, README).
- A Ctrl+click rubber-band selection starts no OLE drag: the shields appear, never get a `DragEnter`, and are removed on release; the selection works as usual.
- A Ctrl+drag of folders only opens no menu, and the drop is still refused.
- Esc during the drag: Explorer's drag loop cancels it (no drop, no file operation); the donut closes when the button is released.

### Settings (version 7)

`Settings.Version` 6 → 7, six new `[XmlElement]` properties (`RadialMenuEnabled`, `RadialMenuExtraContextMenuEntry`, `RadialMenuReplaceContextMenu`, `RadialMenuHotkeyEnabled`, `RadialMenuHotkey`, `RadialMenuCtrlDragEnabled`) and two `[XmlIgnore]` helpers: `RadialMenuContextMenuMode` (the A/B choice as one `Helpers.RadialContextMenuMode` value for the combo box: `None`, `ExtraEntry`, `ReplaceSubmenu`) and `ShouldRunRadialAgent` (= master && (C || D)). `OnDeserializationComplete` clears A when both A and B are set (B wins) and replaces an empty hotkey with `Ctrl+Alt+C`. The `settingsVersion <= 6` migration is a no-op: everything is off by default, so an old file loads unchanged and gives the same context menu and no new process. Details: `docs/SETTINGS.md`.
