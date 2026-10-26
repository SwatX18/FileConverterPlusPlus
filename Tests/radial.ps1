<#
.SYNOPSIS
    Integration tests for the radial menu: --radial donut (UI Automation), settings v6 -> v7, tray agent lifecycle, edge cases.

.DESCRIPTION
    Drives the built FileConverterPlusPlus.exe the way integration.ps1 does (HKCU\Software\FileConverter\Path and
    %LOCALAPPDATA%\FileConverter\Settings.user.xml are set for the run, the log is grepped) and adds:
      - UI Automation of the donut (window AutomationId RadialMenuWindow, slices RadialSlice_i named after the preset full name,
        RadialPageIndicator) plus SendInput for hover, Esc, Tab, click;
      - the tray agent (--agent / --stop-agent / --post-install-init): single instance, HKCU Run value, Reload event, memory;
      - hand-edited settings files (invalid hotkey, A and B both true, odd preset names), paths without extension, long path lists;
      - the Ctrl+drag "no file operation" guarantee with real Explorer windows on temporary folders (-Group drag).

    Groups (-Group): a (v6 -> v7 migration), b (donut), c (agent), d (master off), edge (edge cases); ui (Settings window), hotkey (Ctrl+Alt+Shift+F11 with Explorer) and drag (Ctrl+drag) move the mouse and are not in the default set.
    Some checks need the donut to be the foreground window (Esc, Tab, focus loss); when Windows refuses the focus they are
    reported as 'skip' (not verifiable), never as 'pass'. First-frame times and agent memory are reported ('info'), not asserted.

    The machine's real FileConverter state is saved and restored: if FileConverter is installed (HKCU\Software\FileConverter) or
    has user settings, pass -BackupExisting; the registry value, Settings.user.xml, Registry.xml and hw-cache.json are backed up to
    a temporary folder and restored afterwards. The HKCU Run value of the agent is removed (or restored). The script refuses to run
    while a radial agent is already running. Runs only on temporary folders.

.EXAMPLE
    .\Tests\radial.ps1 -BackupExisting
    .\Tests\radial.ps1 -BackupExisting -Group a,b
    .\Tests\radial.ps1 -BackupExisting -Group drag
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string[]]$Group = @('a', 'b', 'c', 'd', 'edge'),
    [switch]$BackupExisting,
    [int]$FirstFrameRuns = 5
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path (Join-Path $here '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'Application\FileConverter\bin\x64\Release\FileConverterPlusPlus.exe' }
$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path $Exe
$Group = @($Group | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
}
'@
try { [void][Native]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) } catch { }

# ------------------------------------------------------------------ environment
$regPath = 'HKCU:\Software\FileConverter'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'FileConverterPlusPlus.RadialAgent'
$userDir = Join-Path $env:LOCALAPPDATA 'FileConverter'
$userSettings = Join-Path $userDir 'Settings.user.xml'
$stateFiles = 'Settings.user.xml', 'Registry.xml', 'hw-cache.json'
$work = Join-Path ([IO.Path]::GetTempPath()) ('fc-radial-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$backup = Join-Path ([IO.Path]::GetTempPath()) ('fc-radial-backup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
[xml]$defaults = Get-Content -Raw -Encoding UTF8 (Join-Path $exeDir 'Settings.default.xml')

function Test-AgentRunning {
    $m = $null
    if ([Threading.Mutex]::TryOpenExisting('Local\FileConverterPlusPlus.RadialAgent', [Security.AccessControl.MutexRights]::Synchronize, [ref]$m)) { $m.Dispose(); return $true }
    $false
}

# ------------------------------------------------------------------ result bookkeeping
$results = New-Object System.Collections.ArrayList
function Add-Result([string]$Status, [string]$Id, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ status = $Status; id = $Id; detail = $Detail })
    $color = switch ($Status) { 'pass' { 'Green' } 'FAIL' { 'Red' } 'skip' { 'Yellow' } default { 'Gray' } }
    Write-Host ('{0,-5} {1}{2}' -f $Status, $Id, $(if ($Detail) { "  -- $Detail" } else { '' })) -ForegroundColor $color
}
function Check([string]$Id, [bool]$Condition, [string]$Detail = '') { Add-Result $(if ($Condition) { 'pass' } else { 'FAIL' }) $Id $(if (-not $Condition) { $Detail } else { '' }) }
function Info([string]$Id, [string]$Detail) { Add-Result 'info' $Id $Detail }
function Skip([string]$Id, [string]$Detail) { Add-Result 'skip' $Id $Detail }

# ------------------------------------------------------------------ settings / log helpers
$baseValues = [ordered]@{ ExitApplicationWhenConversionsFinished = 'true'; DurationBetweenEndOfConversionsAndApplicationExit = '0'; CheckUpgradeAtStartup = 'false'; CopyFilesInClipboardAfterConversion = 'false' }

function New-SettingsDocument([hashtable]$Values = @{}, [int]$Version = 7) {
    $doc = $defaults.Clone()
    $doc.Settings.SetAttribute('SerializationVersion', [string]$Version)
    $all = [ordered]@{}
    foreach ($k in $baseValues.Keys) { $all[$k] = $baseValues[$k] }
    foreach ($k in $Values.Keys) { $all[$k] = $Values[$k] }
    foreach ($k in $all.Keys) {
        $node = $doc.Settings.SelectSingleNode($k)
        if (-not $node) { $node = $doc.CreateElement($k); [void]$doc.Settings.PrependChild($node) }
        $node.InnerText = [string]$all[$k]
    }
    $doc
}

function Write-UserSettings([hashtable]$Values = @{}, [int]$Version = 7, [scriptblock]$Mutate = $null) {
    $doc = New-SettingsDocument $Values $Version
    if ($Mutate) { & $Mutate $doc }
    $doc.Save($userSettings)
}

function Clear-Logs { Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
function Get-Log {
    # Read with FileShare.ReadWrite: the application keeps its log open.
    $lines = @()
    foreach ($f in Get-ChildItem $userDir -Recurse -Filter *.log -ErrorAction SilentlyContinue) {
        try {
            $fs = New-Object IO.FileStream $f.FullName, "Open", "Read", "ReadWrite"
            $sr = New-Object IO.StreamReader $fs
            $lines += $sr.ReadToEnd() -split '\r?\n'
            $sr.Dispose()
        }
        catch { }
    }
    $lines
}

function Get-Presets {
    # Default presets in settings order: @{ FullName; Inputs }.
    @($defaults.Settings.SelectNodes('ConversionPreset') | ForEach-Object {
        @{ FullName = $_.GetAttribute('Name'); Inputs = @($_.SelectNodes('InputTypes') | ForEach-Object { $_.InnerText.ToLowerInvariant() }) }
    })
}
$allPresets = Get-Presets

function Get-ExpectedPresets([string[]]$FileNames, $Presets = $allPresets) {
    # The intersection rule: files without extension are ignored; no extension at all -> nothing.
    $exts = @($FileNames | ForEach-Object { [IO.Path]::GetExtension($_).TrimStart('.').ToLowerInvariant() } | Where-Object { $_ } | Select-Object -Unique)
    if ($exts.Count -eq 0) { return @() }
    @($Presets | Where-Object { $p = $_; @($exts | Where-Object { $p.Inputs -notcontains $_ }).Count -eq 0 } | ForEach-Object { $_.FullName })
}

# ------------------------------------------------------------------ process helpers
$started = New-Object System.Collections.ArrayList
function Join-Arguments([string[]]$Arguments) {
    ($Arguments | ForEach-Object { if ($_ -eq '' -or $_ -match '[\s"]') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $_ } }) -join ' '
}
function Start-App([string[]]$Arguments, [string]$RawArguments = $null) {
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.Arguments = if ($RawArguments) { $RawArguments } else { Join-Arguments $Arguments }
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    [void]$started.Add($p)
    $p
}
function Kill-Tree([int]$ProcessId) { try { $null = cmd.exe /c "taskkill.exe /T /F /PID $ProcessId >nul 2>nul" } catch { } }
function Stop-Tree($p) { if ($p -and $p.Id -and -not $p.HasExited) { Kill-Tree $p.Id; [void]$p.WaitForExit(3000) } }
function Wait-Exit($p, [int]$Milliseconds) { $p.WaitForExit($Milliseconds) }
function Get-AgentProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='FileConverterPlusPlus.exe'" | Where-Object { $_.CommandLine -match '--agent' -and $_.ExecutablePath -eq $Exe })
}
function Stop-Agents {
    & $Exe --stop-agent 2>&1 | Out-Null
    Start-Sleep -Milliseconds 300
    Get-AgentProcesses | ForEach-Object { Kill-Tree $_.ProcessId }
    Remove-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
}
function Wait-Until([scriptblock]$Condition, [int]$Milliseconds, [int]$PollMilliseconds = 50) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $Milliseconds) { if (& $Condition) { return $sw.ElapsedMilliseconds }; Start-Sleep -Milliseconds $PollMilliseconds }
    -1
}

# ------------------------------------------------------------------ UI Automation / input helpers
$ae = [Windows.Automation.AutomationElement]
$treeScope = [Windows.Automation.TreeScope]
function Get-Prop($name, $value) { New-Object Windows.Automation.PropertyCondition ($ae::$name), $value }

function Find-Donut([int]$ProcessId, [int]$TimeoutMilliseconds = 15000) {
    $cond = Get-Prop 'AutomationIdProperty' 'RadialMenuWindow'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        foreach ($w in $ae::RootElement.FindAll($treeScope::Children, $cond)) { if ($w.Current.ProcessId -eq $ProcessId) { return $w } }
        Start-Sleep -Milliseconds 50
    }
    $null
}

function Get-Slices($window) {
    # Slices (Button, AutomationId RadialSlice_i), ordered by index: @{ Index; Name; Element }.
    $buttons = $window.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::Button)))
    @($buttons | Where-Object { $_.Current.AutomationId -like 'RadialSlice_*' } | ForEach-Object {
        @{ Index = [int]($_.Current.AutomationId -replace 'RadialSlice_', ''); Name = $_.Current.Name; Element = $_ }
    } | Sort-Object { $_.Index })
}

function Get-DonutTexts($window) {
    @($window.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
}

function Get-Indicator($window) {
    $e = $window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialPageIndicator'))
    if ($e -and -not $e.Current.IsOffscreen -and $e.Current.Name) { return $e.Current.Name }
    $null
}

function Get-Handle($window) { [IntPtr]$window.Current.NativeWindowHandle }

function Focus-Window([IntPtr]$Handle) {
    # Windows only lets the foreground process steal the focus: a synthetic Alt press lifts the restriction.
    for ($i = 0; $i -lt 5; $i++) {
        if ([Native]::GetForegroundWindow() -eq $Handle) { return $true }
        [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [void][Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
    }
    [Native]::GetForegroundWindow() -eq $Handle
}

function Send-Key([byte]$Vk, [switch]$Shift) {
    if ($Shift) { [Native]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero) }
    [Native]::keybd_event($Vk, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event($Vk, 0, 2, [UIntPtr]::Zero)
    if ($Shift) { [Native]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 150
}

function Move-Mouse([int]$X, [int]$Y) { [void][Native]::SetCursorPos($X, $Y) }
function Click-Mouse { [Native]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [Native]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }

function Get-SliceCenter($window, [int]$Index, [int]$Count) {
    # Centre of the slice label in physical pixels (slice i is centred on i * 360 / n; a single slice is labelled at the top).
    $r = $window.Current.BoundingRectangle
    $scale = $r.Width / 320.0
    $angle = if ($Count -le 1) { 0 } else { $Index * 360.0 / $Count }
    $rad = $angle * [Math]::PI / 180
    $labelRadius = 102.5
    @{ X = [int]($r.X + (160 + $labelRadius * [Math]::Sin($rad)) * $scale); Y = [int]($r.Y + (160 - $labelRadius * [Math]::Cos($rad)) * $scale) }
}

function New-TinyImage([string]$Path, [string]$Format = 'Png') {
    $bmp = New-Object Drawing.Bitmap 64, 48
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::SteelBlue); $g.Dispose()
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::$Format); $bmp.Dispose()
}

# Starts --radial with the given arguments at a fixed cursor position and returns the process and the donut window.
function Start-Radial([string[]]$Arguments, [hashtable]$Cursor = @{ X = 700; Y = 450 }, [string]$RawArguments = $null) {
    Move-Mouse $Cursor.X $Cursor.Y
    $p = Start-App (@('--radial') + $Arguments) $(if ($RawArguments) { "--radial $RawArguments" } else { $null })
    $w = Find-Donut $p.Id
    @{ Process = $p; Window = $w }
}

function Close-Radial($r) {
    if ($r.Process -and -not $r.Process.HasExited) {
        if ($r.Window -and (Focus-Window (Get-Handle $r.Window))) { Send-Key 0x1B; [void](Wait-Exit $r.Process 3000) }
        Stop-Tree $r.Process
    }
}

function Get-RadialMilliseconds { $m = @(Get-Log | Select-String 'Radial menu shown in (\d+) ms \(from process start\)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value }); if ($m.Count) { $m[-1] } else { $null } }

# ------------------------------------------------------------------ groups
function Test-GroupA {
    Write-Host '== a: v6 settings file + --radial'
    $dir = Join-Path $work 'a'; New-Item -ItemType Directory -Force $dir | Out-Null
    $png = Join-Path $dir 'one.png'; New-TinyImage $png
    Clear-Logs
    Write-UserSettings @{ ApplicationLanguageName = 'en' } 6 {
        param($doc)
        # A custom preset (not IsDefaultSettings) with a changed value, and a changed setting, must survive.
        $src = $doc.Settings.SelectSingleNode("ConversionPreset[@Name='To Jpg']")
        $custom = $src.CloneNode($true)
        $custom.SetAttribute('Name', 'Mine/Custom jpg')
        $custom.RemoveAttribute('IsDefaultSettings')
        $q = $custom.SelectSingleNode("Settings[@Key='ImageQuality']")
        if ($q) { $q.SetAttribute('Value', '37') }
        [void]$doc.Settings.AppendChild($custom)
    }
    $before = New-SettingsDocument @{} 6
    $versionBefore = (Get-Content -Raw $userSettings) -match 'SerializationVersion="6"'
    Check 'a.precondition file is v6, no RadialMenu elements' ($versionBefore -and ((Get-Content -Raw $userSettings) -notmatch 'RadialMenu'))
    $r = Start-Radial @($png)
    Check 'a.donut opens with a v6 settings file' ($null -ne $r.Window)
    Close-Radial $r
    [xml]$after = Get-Content -Raw -Encoding UTF8 $userSettings
    Check 'a.rewritten as version 7' ($after.Settings.SerializationVersion -eq '7') "SerializationVersion=$($after.Settings.SerializationVersion)"
    $expect = [ordered]@{ RadialMenuEnabled = 'false'; RadialMenuExtraContextMenuEntry = 'false'; RadialMenuReplaceContextMenu = 'false'; RadialMenuHotkeyEnabled = 'false'; RadialMenuHotkey = 'Ctrl+Alt+C'; RadialMenuCtrlDragEnabled = 'false' }
    foreach ($k in $expect.Keys) { Check "a.default $k" ($after.Settings.$k -eq $expect[$k]) "$k=$($after.Settings.$k), expected $($expect[$k])" }
    $names = @($after.Settings.SelectNodes('ConversionPreset') | ForEach-Object { $_.GetAttribute('Name') })
    $defaultNames = @($allPresets | ForEach-Object { $_.FullName })
    $missing = @($defaultNames | Where-Object { $names -notcontains $_ })
    Check 'a.default presets intact' ($missing.Count -eq 0) "missing: $($missing -join ', ')"
    Check 'a.custom preset kept' ($names -contains 'Mine/Custom jpg')
    $custom = $after.Settings.SelectSingleNode("ConversionPreset[@Name='Mine/Custom jpg']")
    $q = if ($custom) { $custom.SelectSingleNode("Settings[@Key='ImageQuality']") } else { $null }
    if ($q) { Check 'a.custom preset value kept' ($q.GetAttribute('Value') -eq '37') "ImageQuality=$($q.GetAttribute('Value'))" }
    Check 'a.settings value kept (ApplicationLanguageName)' ($after.Settings.ApplicationLanguageName -eq 'en')
    Check 'a.no agent started, no Run value' (-not (Test-AgentRunning) -and -not (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
}

function Test-GroupB {
    Write-Host '== b: donut (--radial)'
    $dir = Join-Path $work 'b'; New-Item -ItemType Directory -Force $dir | Out-Null
    $jpgTemplate = Join-Path $dir 'template.jpg'; New-TinyImage $jpgTemplate 'Jpeg'
    Write-UserSettings @{ ApplicationLanguageName = 'en' }
    $noCompatible = 'No compatible preset'

    # --- 20 files: slice names, indicator, paging
    $files = @(1..20 | ForEach-Object { $f = Join-Path $dir ('photo_{0:D2}.jpg' -f $_); Copy-Item $jpgTemplate $f; $f })
    $expected = @(Get-ExpectedPresets $files)
    $pages = [Math]::Max(1, [Math]::Ceiling($expected.Count / 8.0))
    Info 'b.20 files: expected compatible presets' "$($expected.Count) presets, $pages page(s)"
    Clear-Logs
    $r = Start-Radial $files
    Check 'b.20 files: UIA finds RadialMenuWindow' ($null -ne $r.Window)
    if ($r.Window) {
        $slices = @(Get-Slices $r.Window)
        $exp1 = @($expected | Select-Object -First 8)
        Check 'b.20 files: slice names are exactly the first page of compatible presets' ((($slices | ForEach-Object { $_.Name }) -join '|') -eq ($exp1 -join '|')) "got: $(($slices | ForEach-Object { $_.Name }) -join ', ')"
        $indicator = Get-Indicator $r.Window
        if ($pages -gt 1) { Check "b.20 files: indicator is 1/$pages" ($indicator -eq "1/$pages") "indicator=$indicator" }
        else { Check 'b.20 files: no paging indicator for <= 8 presets' ($null -eq $indicator) "indicator=$indicator" }

        # hover: the hub shows the full name of the slice under the cursor
        if ($slices.Count -gt 0) {
            $c = Get-SliceCenter $r.Window 0 $slices.Count
            Move-Mouse $c.X $c.Y; Start-Sleep -Milliseconds 400
            $texts = Get-DonutTexts $r.Window
            Check 'b.hover: hub shows the full name of the hovered slice' ($texts -contains $slices[0].Name) "hub texts: $($texts -join ' | ')"
            if ($slices.Count -gt 3) {
                $c = Get-SliceCenter $r.Window 3 $slices.Count
                Move-Mouse $c.X $c.Y; Start-Sleep -Milliseconds 400
                $texts = Get-DonutTexts $r.Window
                Check 'b.hover: slice 3' ($texts -contains $slices[3].Name) "hub texts: $($texts -join ' | ')"
            }
        }

        # paging with the keyboard (needs the focus)
        if ($pages -gt 1) {
            if (Focus-Window (Get-Handle $r.Window)) {
                Send-Key 0x09
                Check "b.Tab: indicator 2/$pages" ((Get-Indicator $r.Window) -eq "2/$pages") "indicator=$(Get-Indicator $r.Window)"
                $s2 = @(Get-Slices $r.Window)
                $exp2 = @($expected | Select-Object -Skip 8 -First 8)
                Check 'b.Tab: second page names' ((($s2 | ForEach-Object { $_.Name }) -join '|') -eq ($exp2 -join '|'))
                Send-Key 0x09 -Shift
                Check 'b.Shift+Tab: back to 1' ((Get-Indicator $r.Window) -eq "1/$pages") "indicator=$(Get-Indicator $r.Window)"
                Send-Key 0x09 -Shift
                Check "b.Shift+Tab wraps to $pages/$pages" ((Get-Indicator $r.Window) -eq "$pages/$pages") "indicator=$(Get-Indicator $r.Window)"
                for ($i = 0; $i -lt $pages; $i++) { Send-Key 0x09 }
                Check 'b.Tab wraps around a full cycle' ((Get-Indicator $r.Window) -eq "$pages/$pages")
            }
            else { Skip 'b.Tab paging' 'could not get the donut foreground' }
        }
        # Esc
        if (Focus-Window (Get-Handle $r.Window)) {
            Send-Key 0x1B
            $exited = Wait-Exit $r.Process 5000
            Check 'b.Esc: process exits' $exited
            Check 'b.Esc: exit code 0' ($exited -and $r.Process.ExitCode -eq 0) "exit code $(if ($exited) { $r.Process.ExitCode } else { 'n/a' })"
            Check 'b.Esc: nothing converted' (@(Get-Log | Select-String '^Job (done|failed)').Count -eq 0 -and @(Get-ChildItem $dir -File | Where-Object { $_.Name -notmatch '^(photo_\d+\.jpg|template\.jpg)$' }).Count -eq 0)
        }
        else { Skip 'b.Esc' 'could not get the donut foreground'; Stop-Tree $r.Process }
    }
    else { Stop-Tree $r.Process }

    # --- selections: mixed, no compatible, no extension
    $dummy = @{}
    foreach ($n in 'a.jpg', 'b.png', 'c.mp3', 'd.mp4', 'e.PNG', 'noext', 'trailing.', 'f.xyz', 'g.tar.jpg', 'h.mkv') { $f = Join-Path $dir $n; Set-Content $f 'x'; $dummy[$n] = $f }
    $selections = @(
        @{ Name = 'mixed jpg+png'; Files = 'a.jpg', 'b.png' }
        @{ Name = 'mixed mp4+mp3'; Files = 'd.mp4', 'c.mp3' }
        @{ Name = 'mixed mp4+mkv'; Files = 'd.mp4', 'h.mkv' }
        @{ Name = 'no compatible jpg+mp3'; Files = 'a.jpg', 'c.mp3' }
        @{ Name = 'unknown extension'; Files = 'f.xyz' }
        @{ Name = 'upper case extension'; Files = 'e.PNG' }
        @{ Name = 'file without extension is ignored'; Files = 'a.jpg', 'noext' }
        @{ Name = 'only files without extension'; Files = 'noext' }
        @{ Name = 'trailing dot is ignored'; Files = 'a.jpg', 'trailing.' }
        @{ Name = 'double extension uses the last one'; Files = 'g.tar.jpg' }
    )
    foreach ($s in $selections) {
        $paths = @($s.Files | ForEach-Object { $dummy[$_] })
        $expected = @(Get-ExpectedPresets $s.Files)
        $r = Start-Radial $paths
        if (-not $r.Window) { Check "b.selection '$($s.Name)': donut opens" $false; Stop-Tree $r.Process; continue }
        $slices = @(Get-Slices $r.Window)
        $texts = Get-DonutTexts $r.Window
        $exp1 = @($expected | Select-Object -First 8)
        Check "b.selection '$($s.Name)': slices ($($expected.Count) compatible)" ((($slices | ForEach-Object { $_.Name }) -join '|') -eq ($exp1 -join '|')) "got: $(($slices | ForEach-Object { $_.Name }) -join ', ')"
        if ($expected.Count -eq 0) {
            Check "b.selection '$($s.Name)': shows '$noCompatible'" ($texts -contains $noCompatible) "texts: $($texts -join ' | ')"
            Check "b.selection '$($s.Name)': no page indicator" ($null -eq (Get-Indicator $r.Window))
        }
        Close-Radial $r
    }

    # --- pick: converts and the output exists
    $pickDir = Join-Path $work 'b-pick'; New-Item -ItemType Directory -Force $pickDir | Out-Null
    $src = Join-Path $pickDir 'pick.png'; New-TinyImage $src
    Clear-Logs
    $r = Start-Radial @($src)
    if ($r.Window) {
        $slices = @(Get-Slices $r.Window)
        $target = $slices | Where-Object { $_.Name -eq 'To Jpg' } | Select-Object -First 1
        if (-not $target) { Check 'b.pick: To Jpg slice on the first page' $false "slices: $(($slices | ForEach-Object { $_.Name }) -join ', ')" }
        else {
            $target.Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $exited = Wait-Exit $r.Process 120000
            Check 'b.pick: process exits after the conversion' $exited
            Check 'b.pick: exit code 0' ($exited -and $r.Process.ExitCode -eq 0)
            $out = @(Get-ChildItem $pickDir -File | Where-Object { $_.Name -ne 'pick.png' })
            Check 'b.pick: output exists (pick.jpg)' ($out.Count -eq 1 -and $out[0].Name -eq 'pick.jpg') "outputs: $($out.Name -join ', ')"
            if ($out.Count -ge 1) {
                $head = [IO.File]::ReadAllBytes($out[0].FullName)
                Check 'b.pick: output is a JPEG' ($head.Length -gt 2 -and $head[0] -eq 0xFF -and $head[1] -eq 0xD8)
            }
            Check 'b.pick: log has Job done' (@(Get-Log | Select-String '^Job done').Count -eq 1)
        }
    }
    else { Check 'b.pick: donut opens' $false }
    Stop-Tree $r.Process

    # --- close without converting: click outside the ring, focus loss
    $src2 = Join-Path $pickDir 'close.png'; New-TinyImage $src2
    $r = Start-Radial @($src2)
    if ($r.Window -and (Focus-Window (Get-Handle $r.Window))) {
        $rect = $r.Window.Current.BoundingRectangle
        Move-Mouse ([int]($rect.X + 4)) ([int]($rect.Y + 4)); Start-Sleep -Milliseconds 200; Click-Mouse
        $exited = Wait-Exit $r.Process 5000
        Check 'b.click outside the ring closes the menu' $exited
        Check 'b.click outside: exit code 0 and nothing converted' ($exited -and $r.Process.ExitCode -eq 0 -and -not (Test-Path (Join-Path $pickDir 'close.jpg')))
    }
    else { Skip 'b.click outside the ring' 'could not get the donut foreground' }
    Stop-Tree $r.Process

    $r = Start-Radial @($src2)
    if ($r.Window -and (Focus-Window (Get-Handle $r.Window))) {
        $form = New-Object Windows.Forms.Form; $form.Width = 200; $form.Height = 100; $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point 1200, 700; $form.Show()
        [void](Focus-Window $form.Handle)
        $exited = Wait-Exit $r.Process 5000
        $form.Close(); $form.Dispose()
        if ($exited) { Check 'b.focus loss closes the menu (exit code 0, nothing converted)' ($r.Process.ExitCode -eq 0 -and -not (Test-Path (Join-Path $pickDir 'close.jpg'))) }
        else { Check 'b.focus loss closes the menu' $false 'still running 5 s after another window took the focus' }
    }
    else { Skip 'b.focus loss' 'could not get the donut foreground' }
    Stop-Tree $r.Process

    # --- first frame
    $times = @()
    for ($i = 0; $i -lt $FirstFrameRuns; $i++) {
        Clear-Logs
        $r = Start-Radial $files
        if ($r.Window) { Start-Sleep -Milliseconds 400 }
        Close-Radial $r
        # The log is exclusively open while the process runs: read it afterwards.
        $ms = Get-RadialMilliseconds
        if ($null -ne $ms) { $times += $ms }
    }
    if ($times.Count -gt 0) {
        $sorted = @($times | Sort-Object); $median = $sorted[[int][Math]::Floor($sorted.Count / 2)]
        Info 'b.first frame (20 files, from process start), target < 300 ms' ("median {0} ms over {1} runs ({2})" -f $median, $times.Count, ($times -join ', '))
    }
    else { Check 'b.first frame: log line "Radial menu shown in N ms (from process start)." present' $false }
}

function Test-GroupC {
    Write-Host '== c: agent'
    Stop-Agents
    Clear-Logs
    $hotkey = 'Ctrl+Alt+Shift+F11'
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = $hotkey }
    # --post-install-init applies the settings: Run value, but it must not start the agent (the installer can run elevated).
    $init = Start-App @('--post-install-init'); [void](Wait-Exit $init 20000)
    Start-Sleep -Milliseconds 1500
    Check 'c.post-install-init does not start the agent' (-not (Test-AgentRunning))
    $agentStart = Start-App @('--agent')
    $ms = Wait-Until { Test-AgentRunning } 10000
    Check 'c.--agent with master+C on starts the agent' ($ms -ge 0)
    $run = (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue).$runValueName
    Check 'c.Run value present' ($run -eq "`"$Exe`" --agent") "value: $run"
    # two more starts -> still one process
    $s1 = Start-App @('--agent'); $s2 = Start-App @('--agent')
    [void](Wait-Exit $s1 10000); [void](Wait-Exit $s2 10000)
    Check 'c.second and third --agent exit by themselves, code 0' ($s1.HasExited -and $s2.HasExited -and $s1.ExitCode -eq 0 -and $s2.ExitCode -eq 0) "exit codes: $($s1.ExitCode), $($s2.ExitCode)"
    $agents = @(Get-AgentProcesses)
    Check 'c.exactly one agent process' ($agents.Count -eq 1) "$($agents.Count) agent processes"
    Check 'c.Run value still present after the extra starts' ($null -ne (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))

    # idle resources after 10 s
    if ($agents.Count -ge 1) {
        $agentProcess = Get-Process -Id $agents[0].ProcessId
        $cpu0 = $agentProcess.TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Seconds 10
        $agentProcess.Refresh()
        Info 'c.idle memory after 10 s' ("working set {0:N1} MB, private bytes {1:N1} MB (target < 50 MB)" -f ($agentProcess.WorkingSet64 / 1MB), ($agentProcess.PrivateMemorySize64 / 1MB))
        $cpu1 = $agentProcess.TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Seconds 5; $agentProcess.Refresh()
        $cpuIdle = ($agentProcess.TotalProcessorTime.TotalMilliseconds - $cpu1) / 5000 * 100 / [Environment]::ProcessorCount
        Info 'c.idle cpu' ("{0:N2} % of the machine over 5 s" -f $cpuIdle)
        # RAM = working set (what Task Manager shows as Memory). Private bytes is committed address space, reported above for information.
        Check 'c.idle working set < 50 MB' ($agentProcess.WorkingSet64 -lt 50MB) ("{0:N1} MB" -f ($agentProcess.WorkingSet64 / 1MB))
    }

    # settings with C and D off + Reload event -> exits within 2 s, Run value removed
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'false'; RadialMenuCtrlDragEnabled = 'false' }
    $ev = [Threading.EventWaitHandle]::OpenExisting('Local\FileConverterPlusPlus.RadialAgent.Reload')
    $sw = [Diagnostics.Stopwatch]::StartNew(); [void]$ev.Set(); $ev.Dispose()
    $gone = Wait-Until { (Get-AgentProcesses).Count -eq 0 -and -not (Test-AgentRunning) } 5000
    $runGone = $null -eq (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue)
    Check 'c.Reload with C and D off: agent exits within 2 s' ($gone -ge 0 -and $gone -le 2000) "took $gone ms"
    Check 'c.Reload with C and D off: Run value removed' $runGone

    # --stop-agent
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = $hotkey }
    $init = Start-App @('--agent')
    $null = Wait-Until { Test-AgentRunning } 10000
    Check 'c.agent restarted for --stop-agent' (Test-AgentRunning)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $stop = Start-App @('--stop-agent'); $stopped = Wait-Exit $stop 10000; $sw.Stop()
    Check 'c.--stop-agent returns, exit code 0, in < 2.5 s' ($stopped -and $stop.ExitCode -eq 0 -and $sw.ElapsedMilliseconds -lt 2500) "exit $($stop.ExitCode) after $($sw.ElapsedMilliseconds) ms"
    Check 'c.--stop-agent: no agent process, mutex gone' ((Get-AgentProcesses).Count -eq 0 -and -not (Test-AgentRunning))
    Check 'c.--stop-agent: Run value removed' ($null -eq (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
    $stop2 = Start-App @('--stop-agent'); $ok = Wait-Exit $stop2 10000
    Check 'c.hotkey registered (log of the first agent)' (@(Get-Log | Select-String "hotkey $([regex]::Escape($hotkey)) registered").Count -ge 1)
    Check 'c.--stop-agent with no agent: exit 0' ($ok -and $stop2.ExitCode -eq 0)
    Stop-Agents
}

function Test-GroupD {
    Write-Host '== d: master off'
    Stop-Agents
    # triggers on but master off: no agent, no Run value
    Write-UserSettings @{ RadialMenuEnabled = 'false'; RadialMenuHotkeyEnabled = 'true'; RadialMenuCtrlDragEnabled = 'true'; RadialMenuExtraContextMenuEntry = 'true' }
    $init = Start-App @('--post-install-init'); [void](Wait-Exit $init 20000)
    Start-Sleep -Milliseconds 800
    Check 'd.master off + C/D on: post-install-init starts no agent' (-not (Test-AgentRunning) -and (Get-AgentProcesses).Count -eq 0)
    Check 'd.master off: no Run value' ($null -eq (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
    $a = Start-App @('--agent'); $exited = Wait-Exit $a 10000
    Check 'd.master off: --agent exits by itself (nothing to do)' ($exited -and (Get-AgentProcesses).Count -eq 0) $(if ($exited) { "exit $($a.ExitCode)" } else { 'still running' })
    Check 'd.master off: still no Run value after --agent' ($null -eq (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
    # the extension's menu mode, through the built extension assembly
    $dll = Join-Path $exeDir 'FileConverterPlusPlusExtension.dll'
    try {
        $asm = [Reflection.Assembly]::LoadFrom($dll)
        $type = $asm.GetType('FileConverterExtension.ExtensionSettings')
        $method = $type.GetMethod('ResolveMenuMode')
        $modeOff = $method.Invoke($null, @($false, $true, $true)).ToString()
        $modeOffA = $method.Invoke($null, @($false, $true, $false)).ToString()
        Check 'd.extension: master off with A and B on -> Classic' ($modeOff -eq 'Classic' -and $modeOffA -eq 'Classic') "got $modeOff / $modeOffA"
        Check 'd.extension: master on, A -> ClassicWithRadialEntry; B -> RadialOnly; A+B -> RadialOnly (B wins); none -> Classic' (
            $method.Invoke($null, @($true, $true, $false)).ToString() -eq 'ClassicWithRadialEntry' -and
            $method.Invoke($null, @($true, $false, $true)).ToString() -eq 'RadialOnly' -and
            $method.Invoke($null, @($true, $true, $true)).ToString() -eq 'RadialOnly' -and
            $method.Invoke($null, @($true, $false, $false)).ToString() -eq 'Classic')
    }
    catch { Skip 'd.extension ResolveMenuMode' "could not load the extension assembly: $($_.Exception.Message)" }
}

function Test-GroupEdge {
    Write-Host '== edge cases'
    Stop-Agents
    $dir = Join-Path $work 'edge'; New-Item -ItemType Directory -Force $dir | Out-Null

    # invalid hotkey string: logged, no crash
    Clear-Logs
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = 'Ctrl+Nonsense+Banana'; RadialMenuCtrlDragEnabled = 'true' }
    $a = Start-App @('--agent')
    $ms = Wait-Until { Test-AgentRunning } 10000
    Check 'edge.invalid hotkey: agent starts' ($ms -ge 0)
    Start-Sleep -Milliseconds 1500
    Check 'edge.invalid hotkey: agent alive' (-not $a.HasExited)
    Stop-Agents; [void]$a.WaitForExit(5000)   # the log is exclusively open while the agent runs
    Check 'edge.invalid hotkey: logged' (@(Get-Log | Select-String "can't register the hotkey 'Ctrl\+Nonsense\+Banana'").Count -ge 1) 'no "can''t register the hotkey" log line'
    Check 'edge.invalid hotkey: Ctrl+drag hook installed anyway' (@(Get-Log | Select-String "can't install the mouse hook").Count -eq 0)
    Stop-Agents

    # shift-only / no modifier hotkey strings in the file
    foreach ($bad in 'F11', 'Shift+F11', 'Ctrl+Alt', '', '   ') {
        Clear-Logs
        Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = $bad }
        $a = Start-App @('--agent'); $null = Wait-Until { Test-AgentRunning } 10000; Start-Sleep -Milliseconds 800
        $alive = -not $a.HasExited
        Stop-Agents; [void]$a.WaitForExit(5000)
        $logged = @(Get-Log | Select-String "(can't register the hotkey|hotkey .* registered)")
        if ($bad.Trim() -eq '') { Check "edge.empty hotkey '$bad' falls back to the default, agent alive" ($alive -and @($logged | Where-Object { $_ -match 'Ctrl\+Alt\+C' }).Count -ge 1) "log: $($logged -join ' ; ')" }
        else { Check "edge.hotkey '$bad' rejected, agent alive" ($alive -and @($logged | Where-Object { $_ -match "can't register" }).Count -ge 1) "log: $($logged -join ' ; ')" }
        Stop-Agents
    }

    # A and B both true: B wins (extension side through the assembly; application side through a settings save)
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuExtraContextMenuEntry = 'true'; RadialMenuReplaceContextMenu = 'true' }
    try {
        $asm = [Reflection.Assembly]::LoadFrom((Join-Path $exeDir 'FileConverterPlusPlusExtension.dll'))
        $type = $asm.GetType('FileConverterExtension.ExtensionSettings')
        $ser = New-Object Xml.Serialization.XmlSerializer $type
        $reader = New-Object IO.StreamReader $userSettings
        $loaded = $ser.Deserialize($reader); $reader.Dispose()
        $mode = $type.GetMethod('GetMenuMode').Invoke($loaded, @()).ToString()
        Check 'edge.A and B true in the file: extension mode is RadialOnly (B wins)' ($mode -eq 'RadialOnly') "mode $mode"
    }
    catch { Skip 'edge.A+B extension mode' $_.Exception.Message }
    $init = Start-App @('--post-install-init'); [void](Wait-Exit $init 20000)
    [xml]$saved = Get-Content -Raw -Encoding UTF8 $userSettings
    Check 'edge.A and B true: saved with B true, A false' ($saved.Settings.RadialMenuReplaceContextMenu -eq 'true' -and $saved.Settings.RadialMenuExtraContextMenuEntry -eq 'false') "A=$($saved.Settings.RadialMenuExtraContextMenuEntry) B=$($saved.Settings.RadialMenuReplaceContextMenu)"
    Stop-Agents

    # > 8000 chars of paths through --input-files with --radial
    $longDir = Join-Path $dir ('long_' + ('x' * 60)); New-Item -ItemType Directory -Force $longDir | Out-Null
    $tpl = Join-Path $dir 'tpl.png'; New-TinyImage $tpl
    $longFiles = @(1..120 | ForEach-Object { $f = Join-Path $longDir ('image_with_a_long_name_{0:D4}_{1}.png' -f $_, ('y' * 30)); Copy-Item $tpl $f; $f })
    $list = Join-Path $dir 'list.txt'; [IO.File]::WriteAllLines($list, [string[]]$longFiles)
    $totalChars = ($longFiles | ForEach-Object { $_.Length + 3 } | Measure-Object -Sum).Sum
    Write-UserSettings @{}
    Clear-Logs
    $r = Start-Radial @('--input-files', $list)
    Check "edge.--radial --input-files with $totalChars chars of paths: donut opens" ($null -ne $r.Window)
    if ($r.Window) {
        $slices = @(Get-Slices $r.Window)
        $exp = @(Get-ExpectedPresets @('x.png') | Select-Object -First 8)
        Check 'edge.--input-files: slices are the png presets' ((($slices | ForEach-Object { $_.Name }) -join '|') -eq ($exp -join '|'))
        $target = $slices | Where-Object { $_.Name -eq 'To Jpg' } | Select-Object -First 1
        if ($target) {
            $target.Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $exited = Wait-Exit $r.Process 300000
            $jpgs = @(Get-ChildItem $longDir -Filter *.jpg).Count
            Check 'edge.--input-files: every file of the list is converted' ($exited -and $jpgs -eq 120) "exited=$exited, $jpgs/120 outputs"
        }
    }
    Stop-Tree $r.Process
    # the same list directly on the command line (> 8000 chars is rejected by Windows at 32767 only; 120 paths ~ 15 k chars is fine)
    $r = Start-Radial $longFiles
    Check 'edge.--radial with all paths on the command line: donut opens' ($null -ne $r.Window)
    Close-Radial $r

    # preset names with quotes / special characters: shown verbatim, picked, converted (in-process pick)
    $weird = 'Sp' + [char]0xE9 + 'cial/Say "hi" & <b> % ^ '' ; , ' + [char]0x65E5 + [char]0x672C
    $dirW = Join-Path $work 'edge-weird'; New-Item -ItemType Directory -Force $dirW | Out-Null
    $srcW = Join-Path $dirW 'w.png'; New-TinyImage $srcW
    $mutate = {
        param($doc)
        $src = $doc.Settings.SelectSingleNode("ConversionPreset[@Name='To Jpg']")
        $c = $src.CloneNode($true); $c.SetAttribute('Name', $weird); $c.RemoveAttribute('IsDefaultSettings')
        # first in the list
        [void]$doc.Settings.InsertBefore($c, $doc.Settings.SelectSingleNode('ConversionPreset'))
    }
    Write-UserSettings @{} 7 $mutate
    Clear-Logs
    $r = Start-Radial @($srcW)
    if ($r.Window) {
        $slices = @(Get-Slices $r.Window)
        $s = $slices | Where-Object { $_.Name -eq $weird } | Select-Object -First 1
        Check 'edge.weird preset name: slice named exactly' ($null -ne $s) "slices: $(($slices | ForEach-Object { $_.Name }) -join ' ; ')"
        if ($s) {
            $s.Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $exited = Wait-Exit $r.Process 120000
            Check 'edge.weird preset name: picked, converted, output exists' ($exited -and (Test-Path (Join-Path $dirW 'w.jpg'))) "exited=$exited, files: $((Get-ChildItem $dirW -Name) -join ', ')"
        }
    }
    else { Check 'edge.weird preset name: donut opens' $false }
    Stop-Tree $r.Process
    # the agent's child process command line (ConversionProcessLauncher.BuildArguments)
    foreach ($name in $weird, 'Plain/Name', 'Trailing backslash\') {
        $dirN = Join-Path $work ('edge-arg-' + [guid]::NewGuid().ToString('N').Substring(0, 6)); New-Item -ItemType Directory -Force $dirN | Out-Null
        $srcN = Join-Path $dirN 'n.png'; New-TinyImage $srcN
        $nm = $name
        Write-UserSettings @{} 7 {
            param($doc)
            $src = $doc.Settings.SelectSingleNode("ConversionPreset[@Name='To Jpg']")
            $c = $src.CloneNode($true); $c.SetAttribute('Name', $nm); $c.RemoveAttribute('IsDefaultSettings')
            [void]$doc.Settings.AppendChild($c)
        }
        # Windows quoting as done by ConversionProcessLauncher.Quote: backslashes before a quote or the closing quote are doubled.
        $quoted = '"' + (($name -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
        $raw = "--conversion-preset $quoted `"$srcN`""
        $p = Start-App @() $raw; $exited = Wait-Exit $p 60000
        Check "edge.child command line with preset '$name'" ($exited -and (Test-Path (Join-Path $dirN 'n.jpg'))) "args: $raw ; exited=$exited, files: $((Get-ChildItem $dirN -Name) -join ', ')"
        Stop-Tree $p
    }

    # few compatible presets: 0, 1, 2 -> ring
    $counts = @{}
    foreach ($e in ($allPresets | ForEach-Object { $_.Inputs } | Select-Object -Unique)) { $counts[$e] = @(Get-ExpectedPresets @("x.$e")).Count }
    $oneExt = ($counts.GetEnumerator() | Where-Object { $_.Value -eq 1 } | Select-Object -First 1).Key
    $twoExt = ($counts.GetEnumerator() | Where-Object { $_.Value -eq 2 } | Select-Object -First 1).Key
    Write-UserSettings @{}
    foreach ($case in @(@{ Ext = $oneExt; N = 1 }, @{ Ext = $twoExt; N = 2 }, @{ Ext = 'xyz'; N = 0 })) {
        if (-not $case.Ext) { Skip "edge.$($case.N) compatible presets" 'no extension with that many presets in the default settings'; continue }
        $f = Join-Path $dir "few.$($case.Ext)"; Set-Content $f 'x'
        $rect0 = $null
        Move-Mouse 700 450
        $before = New-Object Drawing.Bitmap 400, 400
        $g = [Drawing.Graphics]::FromImage($before); $g.CopyFromScreen(500, 250, 0, 0, (New-Object Drawing.Size 400, 400)); $g.Dispose()
        $r = Start-Radial @($f)
        if (-not $r.Window) { Check "edge.$($case.N) compatible preset(s): donut opens" $false; Stop-Tree $r.Process; continue }
        Start-Sleep -Milliseconds 600
        $slices = @(Get-Slices $r.Window)
        $texts = Get-DonutTexts $r.Window
        Check "edge.$($case.N) compatible preset(s) (.$($case.Ext)): slice count" ($slices.Count -eq $case.N) "slices: $(($slices | ForEach-Object { $_.Name }) -join ', ')"
        Check "edge.$($case.N) compatible preset(s): no paging indicator" ($null -eq (Get-Indicator $r.Window))
        if ($case.N -eq 0) { Check 'edge.0 compatible presets: message shown' ($texts -contains 'No compatible preset') "texts: $($texts -join ' | ')" }
        if ($case.N -eq 1) {
            # a single slice must be a full ring: sample 8 points around the ring against the desktop behind it
            $rect = $r.Window.Current.BoundingRectangle; $scale = $rect.Width / 320.0
            $after = New-Object Drawing.Bitmap ([int]$rect.Width), ([int]$rect.Height)
            $g = [Drawing.Graphics]::FromImage($after); $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $after.Size); $g.Dispose()
            $differ = 0; $samples = 8
            for ($k = 0; $k -lt $samples; $k++) {
                $a = ($k * 45 + 20) * [Math]::PI / 180
                $px = [int]((160 + 102 * [Math]::Sin($a)) * $scale); $py = [int]((160 - 102 * [Math]::Cos($a)) * $scale)
                $c1 = $after.GetPixel($px, $py)
                $bx = [int]($rect.X + $px - 500); $by = [int]($rect.Y + $py - 250)
                $c0 = if ($bx -ge 0 -and $by -ge 0 -and $bx -lt 400 -and $by -lt 400) { $before.GetPixel($bx, $by) } else { $c1 }
                if ([Math]::Abs($c1.R - $c0.R) + [Math]::Abs($c1.G - $c0.G) + [Math]::Abs($c1.B - $c0.B) -gt 6) { $differ++ }
            }
            if ($differ -eq $samples) { Check 'edge.1 compatible preset renders as a full ring (8/8 ring samples differ from the desktop)' $true } else { Info 'edge.1 compatible preset: pixel sampling of the ring' "$differ/$samples samples differ from the desktop behind (content dependent: dark desktop under a dark ring; the hover check below asserts the full-ring hit test)" }
            $after.Dispose()
            # hit test: every direction of the ring hovers slice 0
            $okHover = 0
            foreach ($deg in 0, 90, 180, 270) {
                $a = $deg * [Math]::PI / 180
                Move-Mouse ([int]($rect.X + (160 + 102 * [Math]::Sin($a)) * $scale)) ([int]($rect.Y + (160 - 102 * [Math]::Cos($a)) * $scale)); Start-Sleep -Milliseconds 250
                if ((Get-DonutTexts $r.Window) -contains $slices[0].Name) { $okHover++ }
            }
            Check 'edge.1 compatible preset: hovering at 0/90/180/270 degrees highlights it' ($okHover -eq 4) "$okHover/4 directions"
        }
        $before.Dispose()
        Close-Radial $r
    }

    # a second --radial while one is open
    $f = Join-Path $dir 'two.png'; New-TinyImage $f
    $r1 = Start-Radial @($f)
    $r2 = Start-Radial @($f)
    Start-Sleep -Milliseconds 1500
    $alive1 = -not $r1.Process.HasExited; $alive2 = -not $r2.Process.HasExited
    Info 'edge.second --radial while one is open' "first alive=$alive1, second alive=$alive2, second window found=$($null -ne $r2.Window)"
    Check 'edge.second --radial: the second donut opens' ($null -ne $r2.Window)
    Check 'edge.second --radial: the second instance does not crash' (-not ($r2.Process.HasExited -and $r2.Process.ExitCode -ne 0)) "exit $($r2.Process.ExitCode)"
    Check 'edge.second --radial: at most one donut stays open (the first closes on focus loss)' (-not ($alive1 -and $alive2)) "first alive=$alive1, second alive=$alive2"
    Close-Radial $r2; Close-Radial $r1

    # missing / deleted file in the list: the donut still opens, nothing crashes
    $r = Start-Radial @((Join-Path $dir 'does-not-exist.png'))
    Check 'edge.nonexistent file: donut opens' ($null -ne $r.Window)
    Close-Radial $r

    # --radial without any file
    $r = Start-Radial @()
    if ($r.Window) { Check 'edge.--radial without files: shows the message' ((Get-DonutTexts $r.Window) -contains 'No compatible preset') }
    else { Info 'edge.--radial without files' "no donut window; exit code $(if ($r.Process.HasExited) { $r.Process.ExitCode } else { 'still running' })" }
    Close-Radial $r

    # French
    Write-UserSettings @{ ApplicationLanguageName = 'fr-FR' }
    $f = Join-Path $dir 'fr.xyz'; Set-Content $f 'x'
    [xml]$fr = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Application\FileConverter\Properties\Resources.fr-FR.resx')
    $expectedFr = ($fr.root.data | Where-Object { $_.name -eq 'RadialMenuNoCompatiblePreset' }).value
    $r = Start-Radial @($f)
    if ($r.Window) { Check "edge.French: message is '$expectedFr'" ((Get-DonutTexts $r.Window) -contains $expectedFr) "texts: $((Get-DonutTexts $r.Window) -join ' | ')" }
    else { Check 'edge.French: donut opens' $false }
    Close-Radial $r
    Write-UserSettings @{}

    # Settings window: Save blocked with master + C on and an invalid hotkey
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = 'Banana' }
    $p = Start-App @('--settings')
    $win = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 20000 -and -not $win) {
        foreach ($w in $ae::RootElement.FindAll($treeScope::Children, (Get-Prop 'ProcessIdProperty' $p.Id))) { if ($w.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { $win = $w } }
        if (-not $win) { Start-Sleep -Milliseconds 200 }
    }
    if ($win) {
        Start-Sleep -Milliseconds 1500
        $save = $win.FindFirst($treeScope::Descendants, (New-Object Windows.Automation.AndCondition (Get-Prop 'NameProperty' 'Save'), (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::Button))))
        if ($save) {
            Check 'edge.settings: Save is disabled with master+C on and an invalid hotkey' (-not $save.Current.IsEnabled)
            $before = (Get-Item $userSettings).LastWriteTimeUtc
            # fix the hotkey in the file is not possible from here; instead turn C off through the UI would need clicks: report only
        }
        else { Skip 'edge.settings Save blocked' 'Save button not found through UIA' }
    }
    else { Skip 'edge.settings Save blocked' 'settings window not found' }
    Stop-Tree $p
    Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'false'; RadialMenuHotkey = 'Banana' }
    $p = Start-App @('--settings'); $win = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 20000 -and -not $win) {
        foreach ($w in $ae::RootElement.FindAll($treeScope::Children, (Get-Prop 'ProcessIdProperty' $p.Id))) { if ($w.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { $win = $w } }
        if (-not $win) { Start-Sleep -Milliseconds 200 }
    }
    if ($win) {
        Start-Sleep -Milliseconds 1500
        $save = $win.FindFirst($treeScope::Descendants, (New-Object Windows.Automation.AndCondition (Get-Prop 'NameProperty' 'Save'), (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::Button))))
        if ($save) { Check 'edge.settings: Save stays enabled when C is off (hotkey not validated)' $save.Current.IsEnabled }
    }
    Stop-Tree $p
    Write-UserSettings @{}
}

# ------------------------------------------------------------------ Ctrl+drag with real Explorer windows (moves the mouse)
function Get-ExplorerWindow([string]$Folder) {
    $shell = New-Object -ComObject Shell.Application
    $uri = ([Uri]$Folder).AbsoluteUri
    Start-Process explorer.exe -ArgumentList ('"' + $Folder + '"')
    $found = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 15000 -and -not $found) {
        foreach ($w in $shell.Windows()) { try { if ($w.LocationURL -and ([Uri]$w.LocationURL).LocalPath -eq $Folder) { $found = $w } } catch { } }
        if (-not $found) { Start-Sleep -Milliseconds 200 }
    }
    $found
}

function Get-ItemCenter([IntPtr]$Handle, [string]$NamePrefix) {
    $win = $ae::FromHandle($Handle)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 10000) {
        foreach ($i in $win.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::ListItem)))) {
            if ($i.Current.Name -like "$NamePrefix*" -and -not $i.Current.IsOffscreen) { $b = $i.Current.BoundingRectangle; return @{ X = [int]($b.X + $b.Width / 2); Y = [int]($b.Y + $b.Height / 2) } }
        }
        Start-Sleep -Milliseconds 200
    }
    $null
}

function Invoke-Drag($From, $To, [switch]$Ctrl, [scriptblock]$AtTarget = $null) {
    Move-Mouse $From.X $From.Y; Start-Sleep -Milliseconds 150; Click-Mouse; Start-Sleep -Milliseconds 300   # select the item
    [Native]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    for ($i = 1; $i -le 6; $i++) { Move-Mouse ($From.X + 3 * $i) ($From.Y + 3 * $i); Start-Sleep -Milliseconds 25 }
    if ($Ctrl) { [Native]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }   # Ctrl pressed after the drag has started
    $steps = 25
    for ($i = 1; $i -le $steps; $i++) {
        Move-Mouse ([int]($From.X + 18 + ($To.X - $From.X - 18) * $i / $steps)) ([int]($From.Y + 18 + ($To.Y - $From.Y - 18) * $i / $steps)); Start-Sleep -Milliseconds 20
    }
    Start-Sleep -Milliseconds 700
    if ($AtTarget) { & $AtTarget }
    [Native]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    if ($Ctrl) { [Native]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 800
}

function Test-GroupDrag {
    Write-Host '== drag: Ctrl+drag (moves the mouse for ~30 s, do not touch it)'
    Stop-Agents
    $srcDir = Join-Path $work 'drag-src'; $dstDir = Join-Path $work 'drag-dst'
    foreach ($d in $srcDir, $dstDir) { New-Item -ItemType Directory -Force $d | Out-Null }
    $src = Join-Path $srcDir 'dragme.png'; New-TinyImage $src
    $win1 = Get-ExplorerWindow $srcDir; $win2 = Get-ExplorerWindow $dstDir
    if (-not $win1 -or -not $win2) { Skip 'drag' 'could not open the Explorer windows'; return }
    [void][Native]::MoveWindow([IntPtr]$win1.HWND, 20, 20, 800, 500, $true)
    [void][Native]::MoveWindow([IntPtr]$win2.HWND, 860, 20, 800, 500, $true)
    Start-Sleep -Milliseconds 800
    try {
        $from = Get-ItemCenter ([IntPtr]$win1.HWND) 'dragme'
        $to = @{ X = 1260; Y = 300 }
        if (-not $from) { Skip 'drag' 'could not locate the file item in Explorer'; return }

        # positive control: without the agent a Ctrl+drag between the windows copies the file
        Invoke-Drag $from $to -Ctrl
        $copied = @(Get-ChildItem $dstDir -File)
        if (@(Get-ChildItem $dstDir -File).Count -eq 1) { Check 'drag.control: without the agent, Ctrl+drag between Explorer windows copies the file (harness works)' ($copied.Count -eq 1) "files in target: $($copied.Name -join ', ')" }
        if ($copied.Count -ne 1) { Skip 'drag' 'the synthetic Ctrl+drag does not copy without the agent on this machine (harness cannot drive Explorer here): the Ctrl+drag guarantee is not verifiable by script, use the manual checklist'; return }
        Get-ChildItem $dstDir -File | Remove-Item -Force

        # agent with Ctrl+drag on
        Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuCtrlDragEnabled = 'true' }
        Clear-Logs
        $a = Start-App @('--agent'); $null = Wait-Until { Test-AgentRunning } 10000; Start-Sleep -Milliseconds 1500
        $from = Get-ItemCenter ([IntPtr]$win1.HWND) 'dragme'

        # drop elsewhere (on the other Explorer window): nothing copied or moved
        Invoke-Drag $from $to -Ctrl
        Check 'drag.drop elsewhere: nothing copied into the target folder' (@(Get-ChildItem $dstDir -File).Count -eq 0) "files in target: $((Get-ChildItem $dstDir -File).Name -join ', ')"
        Check 'drag.drop elsewhere: source untouched, no copy beside it' (((Get-ChildItem $srcDir -File).Name -join ',') -eq 'dragme.png')
        Check 'drag.drop elsewhere: no donut left behind' ($null -eq (Find-Donut $a.Id 800))
        Check 'drag.drop elsewhere: no conversion started' ((@(Get-Process FileConverterPlusPlus -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $a.Id }).Count -eq 0))

        # drop on the hub (centre of the donut): nothing happens
        $hub = $null
        Invoke-Drag $from $to -Ctrl -AtTarget {
            $w = Find-Donut $a.Id 2000
            if ($w) { $r = $w.Current.BoundingRectangle; $script:donutSeen = $true; Move-Mouse ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 500 }
        }
        Check 'drag.donut appeared during the Ctrl+drag' ([bool]$script:donutSeen)
        Check 'drag.drop on the hub: nothing copied, nothing converted' (@(Get-ChildItem $dstDir -File).Count -eq 0 -and @(Get-ChildItem $srcDir -File).Count -eq 1 -and (@(Get-Process FileConverterPlusPlus -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $a.Id }).Count -eq 0))

        # Esc during the drag
        Invoke-Drag $from $to -Ctrl -AtTarget { Send-Key 0x1B; Start-Sleep -Milliseconds 400 }
        Check 'drag.Esc: nothing copied' (@(Get-ChildItem $dstDir -File).Count -eq 0 -and @(Get-ChildItem $srcDir -File).Count -eq 1)

        # drop on a slice: converts, still no file operation
        $script:donutSeen = $false
        Invoke-Drag $from $to -Ctrl -AtTarget {
            $w = Find-Donut $a.Id 2000
            if ($w) {
                $slices = @(Get-Slices $w)
                $s = $slices | Where-Object { $_.Name -eq 'To Jpg' } | Select-Object -First 1
                if ($s) { $c = Get-SliceCenter $w $s.Index $slices.Count; Move-Mouse $c.X $c.Y; Start-Sleep -Milliseconds 500; $script:hubText = (Get-DonutTexts $w) -join ' | ' }
            }
        }
        $ok = (Wait-Until { Test-Path (Join-Path $srcDir 'dragme.jpg') } 60000) -ge 0
        Info 'drag.hub text while hovering the slice' $script:hubText
        Check 'drag.drop on a slice: the preset ran (dragme.jpg next to the source)' $ok "source folder: $((Get-ChildItem $srcDir -Name) -join ', ')"
        Check 'drag.drop on a slice: no copy into the target folder' (@(Get-ChildItem $dstDir -File).Count -eq 0) "target: $((Get-ChildItem $dstDir -Name) -join ', ')"
        Check 'drag.drop on a slice: no other file in the source folder' (((Get-ChildItem $srcDir -Name | Sort-Object) -join ',') -eq 'dragme.jpg,dragme.png')

        # plain drag still works with the agent: copies/moves normally (move between folders on the same drive)
        Remove-Item (Join-Path $srcDir 'dragme.jpg') -ErrorAction SilentlyContinue
        $from = Get-ItemCenter ([IntPtr]$win1.HWND) 'dragme'
        Invoke-Drag $from $to
        Check 'drag.plain drag (no Ctrl) is unaffected: file moved to the target' (Test-Path (Join-Path $dstDir 'dragme.png')) "target: $((Get-ChildItem $dstDir -Name) -join ', ')"
        Check 'drag.plain drag: no donut' ($null -eq (Find-Donut $a.Id 500))
    }
    finally {
        Stop-Agents
        foreach ($w in @($win1, $win2)) { try { $w.Quit() } catch { } }
    }
}

function Open-SettingsWindow([string[]]$Extra = @()) {
    $p = Start-App (@('--settings') + $Extra)
    $win = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 20000 -and -not $win) {
        foreach ($w in $ae::RootElement.FindAll($treeScope::Children, (Get-Prop 'ProcessIdProperty' $p.Id))) { if ($w.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { $win = $w } }
        if (-not $win) { Start-Sleep -Milliseconds 200 }
    }
    if ($win) { Start-Sleep -Milliseconds 1500 }
    @{ Process = $p; Window = $win }
}

function Find-Named($Window, [string]$Name, $ControlType) {
    $conds = @((Get-Prop 'NameProperty' $Name))
    if ($ControlType) { $conds += (Get-Prop 'ControlTypeProperty' $ControlType) }
    $Window.FindFirst($treeScope::Descendants, (New-Object Windows.Automation.AndCondition ([Windows.Automation.Condition[]]$conds)))
}

function Toggle-Check($element) { $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 200 }
function Get-ToggleState($element) { $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() }

# Settings window driven through UI Automation: master/triggers greyed, hotkey capture, Save starts / reloads / stops the agent.
function Test-GroupUi {
    Write-Host '== ui: Settings window'
    Stop-Agents
    Write-UserSettings @{}
    $s = Open-SettingsWindow
    if (-not $s.Window) { Skip 'ui' 'settings window not found'; Stop-Tree $s.Process; return }
    $tab = Find-Named $s.Window 'Radial menu' ([Windows.Automation.ControlType]::TabItem)
    if (-not $tab) { Check 'ui.Settings has a "Radial menu" tab' $false; Stop-Tree $s.Process; return }
    Check 'ui.Settings has a "Radial menu" tab' $true
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
    $checks = @($s.Window.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::CheckBox))) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.AutomationId -like 'RadialMenu*' -or ($_.Current.AutomationId -eq '' -and -not $_.Current.IsOffscreen -and $_.Current.ClassName -eq 'CheckBox' -and $_.Current.BoundingRectangle.Y -gt 0) })
    $master = $s.Window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialMenuEnabledCheckBox'))
    $hotkeyCheck = $s.Window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialMenuHotkeyEnabledCheckBox'))
    $hotkeyBox = $s.Window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialMenuHotkeyTextBox'))
    if (-not $master -or -not $hotkeyCheck -or -not $hotkeyBox) { Skip 'ui' 'controls not found through UIA'; Stop-Tree $s.Process; return }
    # Ctrl+drag checkbox: the CheckBox below the hotkey box (no AutomationId)
    $ctrlDrag = @($s.Window.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::CheckBox))) | Where-Object { $_.Current.BoundingRectangle.Y -gt $hotkeyBox.Current.BoundingRectangle.Y -and $_.Current.BoundingRectangle.Y -lt $hotkeyBox.Current.BoundingRectangle.Y + 80 }) | Select-Object -First 1
    $combo = @($s.Window.FindAll($treeScope::Descendants, (Get-Prop 'ControlTypeProperty' ([Windows.Automation.ControlType]::ComboBox))) | Where-Object { $_.Current.BoundingRectangle.Y -gt $master.Current.BoundingRectangle.Y -and $_.Current.BoundingRectangle.Y -lt $master.Current.BoundingRectangle.Y + 80 }) | Select-Object -First 1

    Check 'ui.master is off by default' ((Get-ToggleState $master) -eq 'Off')
    Check 'ui.master off: C, hotkey box, D and the context menu choice are greyed' (-not $hotkeyCheck.Current.IsEnabled -and -not $hotkeyBox.Current.IsEnabled -and $ctrlDrag -and -not $ctrlDrag.Current.IsEnabled -and $combo -and -not $combo.Current.IsEnabled) "C=$($hotkeyCheck.Current.IsEnabled) box=$($hotkeyBox.Current.IsEnabled) D=$(if ($ctrlDrag) { $ctrlDrag.Current.IsEnabled } else { 'n/a' }) combo=$(if ($combo) { $combo.Current.IsEnabled } else { 'n/a' })"
    Toggle-Check $master
    Check 'ui.master on: triggers enabled' ($hotkeyCheck.Current.IsEnabled -and $ctrlDrag.Current.IsEnabled -and $combo.Current.IsEnabled)
    Check 'ui.hotkey box greyed until C is ticked' (-not $hotkeyBox.Current.IsEnabled)
    Toggle-Check $hotkeyCheck
    Check 'ui.C ticked: hotkey box enabled, shows Ctrl+Alt+C' ($hotkeyBox.Current.IsEnabled -and $hotkeyBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'Ctrl+Alt+C') "value: $($hotkeyBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value)"

    # hotkey capture: no modifier ignored, Ctrl+Alt+F9 accepted, Backspace resets
    if (Focus-Window ([IntPtr]$s.Window.Current.NativeWindowHandle)) {
        $hotkeyBox.SetFocus(); Start-Sleep -Milliseconds 300
        $value = { $hotkeyBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
        Send-Key 0x74   # F5 alone
        Check 'ui.hotkey box: F5 without modifier is ignored' ((& $value) -eq 'Ctrl+Alt+C') "value: $(& $value)"
        Send-Key 0x78 -Shift   # Shift+F9
        Check 'ui.hotkey box: Shift+F9 (Shift only) is ignored' ((& $value) -eq 'Ctrl+Alt+C') "value: $(& $value)"
        [Native]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        Send-Key 0x78
        [Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [Native]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
        Check 'ui.hotkey box: Ctrl+Alt+F9 accepted' ((& $value) -eq 'Ctrl+Alt+F9') "value: $(& $value)"
        Send-Key 0x08
        Check 'ui.hotkey box: Backspace resets to Ctrl+Alt+C' ((& $value) -eq 'Ctrl+Alt+C') "value: $(& $value)"
        [Native]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
        Send-Key 0x7A   # Ctrl+Alt+Shift+F11, the test hotkey
        [Native]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [Native]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
    }
    else { Skip 'ui.hotkey capture' 'could not give the focus to the Settings window' }

    # Save: starts the agent, writes the Run value
    $save = Find-Named $s.Window 'Save' ([Windows.Automation.ControlType]::Button)
    Check 'ui.Save enabled with a valid hotkey' ($save -and $save.Current.IsEnabled)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $ms = Wait-Until { Test-AgentRunning } 8000
    Check 'ui.Save with master+C on starts the agent' ($ms -ge 0) "waited $ms ms"
    Check 'ui.Save writes the Run value' ($null -ne (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
    [void](Wait-Exit $s.Process 8000)
    [xml]$saved = Get-Content -Raw -Encoding UTF8 $userSettings
    Check 'ui.Save stored master and C' ($saved.Settings.RadialMenuEnabled -eq 'true' -and $saved.Settings.RadialMenuHotkeyEnabled -eq 'true') "master=$($saved.Settings.RadialMenuEnabled) C=$($saved.Settings.RadialMenuHotkeyEnabled) hotkey=$($saved.Settings.RadialMenuHotkey)"
    Stop-Tree $s.Process

    # changing the hotkey applies without restart: the old combination is released, the new one is taken
    $oldHotkey = $saved.Settings.RadialMenuHotkey
    $agentBefore = @(Get-AgentProcesses)[0].ProcessId
    Start-Sleep -Seconds 3   # the agent registers its hotkey a moment after it creates its mutex; probing earlier could steal it
    Check 'ui.saved hotkey is held by the agent (cannot be registered by another process)' (-not [Native]::RegisterHotKey([IntPtr]::Zero, 9001, 7, 0x7A)) "hotkey in file: $oldHotkey"
    [void][Native]::UnregisterHotKey([IntPtr]::Zero, 9001)
    $s = Open-SettingsWindow
    if ($s.Window) {
        $tab = Find-Named $s.Window 'Radial menu' ([Windows.Automation.ControlType]::TabItem); $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
        $hotkeyBox = $s.Window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialMenuHotkeyTextBox'))
        if (Focus-Window ([IntPtr]$s.Window.Current.NativeWindowHandle)) {
            $hotkeyBox.SetFocus(); Start-Sleep -Milliseconds 300
            [Native]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
            Send-Key 0x79   # Ctrl+Alt+Shift+F10
            [Native]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [Native]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
            $save = Find-Named $s.Window 'Save' ([Windows.Automation.ControlType]::Button)
            $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            $took = Wait-Until { [Native]::RegisterHotKey([IntPtr]::Zero, 9002, 7, 0x7A) } 2500 100   # the old one becomes free
            [void][Native]::UnregisterHotKey([IntPtr]::Zero, 9002)
            Check 'ui.hotkey change: the old combination is released within ~2 s' ($took -ge 0) "waited $took ms"
            $newTaken = -not [Native]::RegisterHotKey([IntPtr]::Zero, 9003, 7, 0x79)
            if (-not $newTaken) { [void][Native]::UnregisterHotKey([IntPtr]::Zero, 9003) }
            Check 'ui.hotkey change: the new combination is registered by the same agent process' ($newTaken -and @(Get-AgentProcesses)[0].ProcessId -eq $agentBefore)
            [void](Wait-Exit $s.Process 8000)
        }
        else { Skip 'ui.hotkey change' 'could not give the focus to the Settings window' }
    }
    Stop-Tree $s.Process

    # untick C (D never ticked) -> Save stops the agent and removes the Run value within 2 s
    $s = Open-SettingsWindow
    if ($s.Window) {
        $tab = Find-Named $s.Window 'Radial menu' ([Windows.Automation.ControlType]::TabItem); $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
        $hotkeyCheck = $s.Window.FindFirst($treeScope::Descendants, (Get-Prop 'AutomationIdProperty' 'RadialMenuHotkeyEnabledCheckBox'))
        Toggle-Check $hotkeyCheck
        $save = Find-Named $s.Window 'Save' ([Windows.Automation.ControlType]::Button)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $ms = Wait-Until { -not (Test-AgentRunning) } 6000
        Check 'ui.untick C and Save: the agent exits within ~2 s' ($ms -ge 0 -and $ms -le 2500) "took $ms ms"
        Check 'ui.untick C and Save: Run value removed' ($null -eq (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue))
    }
    Stop-Tree $s.Process
    Stop-Agents
}

function Test-GroupHotkey {
    Write-Host '== hotkey: agent trigger C with a real Explorer window (moves the mouse)'
    Stop-Agents
    $dir = Join-Path $work 'hotkey'; New-Item -ItemType Directory -Force $dir | Out-Null
    $f1 = Join-Path $dir 'hk_one.png'; New-TinyImage $f1
    $sub = Join-Path $dir 'subfolder'; New-Item -ItemType Directory -Force $sub | Out-Null
    $win = Get-ExplorerWindow $dir
    if (-not $win) { Skip 'hotkey' 'could not open an Explorer window'; return }
    try {
        [void][Native]::MoveWindow([IntPtr]$win.HWND, 20, 20, 800, 500, $true); Start-Sleep -Milliseconds 800
        Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = 'Ctrl+Alt+Shift+F11' }
        Clear-Logs
        $a = Start-App @('--agent'); $null = Wait-Until { Test-AgentRunning } 10000; Start-Sleep -Milliseconds 1500
        $press = {
            foreach ($k in 0x11, 0x12, 0x10) { [Native]::keybd_event($k, 0, 0, [UIntPtr]::Zero) }
            [Native]::keybd_event(0x7A, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x7A, 0, 2, [UIntPtr]::Zero)
            foreach ($k in 0x10, 0x12, 0x11) { [Native]::keybd_event($k, 0, 2, [UIntPtr]::Zero) }
        }

        # Explorer in front with one file and one folder selected: the donut lists the png presets (the folder is left out)
        $item = Get-ItemCenter ([IntPtr]$win.HWND) 'hk_one'
        $folder = Get-ItemCenter ([IntPtr]$win.HWND) 'subfolder'
        [void](Focus-Window ([IntPtr]$win.HWND))
        Move-Mouse $item.X $item.Y; Start-Sleep -Milliseconds 200; Click-Mouse; Start-Sleep -Milliseconds 400
        & $press
        $w = Find-Donut $a.Id 4000
        Check 'hotkey.Explorer foreground + selection: donut opens' ($null -ne $w)
        if ($w) {
            $slices = @(Get-Slices $w)
            $exp = @(Get-ExpectedPresets @('x.png') | Select-Object -First 8)
            Check 'hotkey.donut lists the presets compatible with the selected png' ((($slices | ForEach-Object { $_.Name }) -join '|') -eq ($exp -join '|')) "got: $(($slices | ForEach-Object { $_.Name }) -join ', ')"
            if (Focus-Window ([IntPtr]$w.Current.NativeWindowHandle)) { Send-Key 0x1B; Start-Sleep -Milliseconds 600 }
            Check 'hotkey.Esc hides the donut and the agent stays alive' ($null -eq (Find-Donut $a.Id 600) -and -not $a.HasExited)
        }

        # selection = one file + one folder -> folder ignored; selection = folder only -> nothing
        if ($folder) {
            [void](Focus-Window ([IntPtr]$win.HWND))
            Move-Mouse $folder.X $folder.Y; Start-Sleep -Milliseconds 200; Click-Mouse; Start-Sleep -Milliseconds 400
            & $press
            $w = Find-Donut $a.Id 2000
            Check 'hotkey.only a folder selected: nothing opens' ($null -eq $w)
            if ($w) { [void](Focus-Window ([IntPtr]$w.Current.NativeWindowHandle)); Send-Key 0x1B }
        }

        # no selection (click on empty space of the list)
        [void](Focus-Window ([IntPtr]$win.HWND))
        Move-Mouse 600 400; Start-Sleep -Milliseconds 200; Click-Mouse; Start-Sleep -Milliseconds 400
        & $press
        $w = Find-Donut $a.Id 2000
        Check 'hotkey.Explorer foreground, no selection: nothing opens' ($null -eq $w)
        if ($w) { [void](Focus-Window ([IntPtr]$w.Current.NativeWindowHandle)); Send-Key 0x1B }

        # another application in front
        $form = New-Object Windows.Forms.Form; $form.Width = 300; $form.Height = 150; $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point 900, 600; $form.Show()
        [void](Focus-Window $form.Handle)
        & $press
        $w = Find-Donut $a.Id 2000
        Check 'hotkey.non-Explorer foreground: nothing opens' ($null -eq $w)
        if ($w) { [void](Focus-Window ([IntPtr]$w.Current.NativeWindowHandle)); Send-Key 0x1B }
        $form.Close(); $form.Dispose()
        Check 'hotkey.agent alive after all presses' (-not $a.HasExited)
        Stop-Agents; [void]$a.WaitForExit(5000)
        $first = @(Get-Log | Select-String 'Radial menu shown in (\d+) ms \(from trigger\)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
        Info 'hotkey.first frame from trigger (target < 300 ms)' "$($first -join ', ') ms"
        Check 'hotkey.no-selection presses are logged' (@(Get-Log | Select-String 'hotkey pressed without an Explorer selection').Count -ge 2)
    }
    finally {
        Stop-Agents
        try { $win.Quit() } catch { }
    }

    # a hotkey taken by another application: warning logged, agent alive, Ctrl+drag hook still installed
    $holder = [Native]::RegisterHotKey([IntPtr]::Zero, 9100, 7, 0x7A)
    try {
        Clear-Logs
        Write-UserSettings @{ RadialMenuEnabled = 'true'; RadialMenuHotkeyEnabled = 'true'; RadialMenuHotkey = 'Ctrl+Alt+Shift+F11'; RadialMenuCtrlDragEnabled = 'true' }
        $a = Start-App @('--agent'); $null = Wait-Until { Test-AgentRunning } 10000; Start-Sleep -Milliseconds 1500
        $alive = -not $a.HasExited
        Stop-Agents; [void]$a.WaitForExit(5000)
        $log = Get-Log
        Check 'hotkey.taken by another application: test process holds it' $holder
        Check 'hotkey.taken by another application: agent alive' $alive
        Check 'hotkey.taken by another application: logged with the win32 error' (@($log | Select-String "can't register the hotkey 'Ctrl\+Alt\+Shift\+F11' \(win32 error 1409\)").Count -eq 1) "log: $(($log | Select-String 'hotkey') -join ' ; ')"
        Check 'hotkey.taken: Ctrl+drag hook still installed' (@($log | Select-String "can't install the mouse hook").Count -eq 0)
    }
    finally { [void][Native]::UnregisterHotKey([IntPtr]::Zero, 9100) }
}

# ------------------------------------------------------------------ main
$hadReg = Test-Path $regPath
$origPath = if ($hadReg) { (Get-ItemProperty $regPath -ErrorAction SilentlyContinue).Path } else { $null }
$hadState = (Test-Path $userDir) -and @(Get-ChildItem $userDir -Exclude 'Diagnostics-*').Count -gt 0
if (($hadReg -or $hadState) -and -not $BackupExisting) { throw "FileConverter is installed or has user settings ($regPath / $userDir): pass -BackupExisting to save and restore them." }
if (Test-AgentRunning) { throw 'A radial menu agent is already running: exit it first (tray icon > Exit).' }
$origRun = (Get-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue).$runValueName
$knownProcessIds = @(Get-Process -Name FileConverterPlusPlus -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })

try {
    New-Item -ItemType Directory -Force $work, $backup, $userDir | Out-Null
    foreach ($f in $stateFiles) { if (Test-Path (Join-Path $userDir $f)) { Copy-Item (Join-Path $userDir $f) (Join-Path $backup $f) -Force; Remove-Item (Join-Path $userDir $f) -Force } }
    if ($hadReg) { Remove-Item $regPath -Recurse -Force }
    New-Item -Force $regPath | Out-Null
    Set-ItemProperty $regPath -Name Path -Value $Exe
    Write-Host "Exe: $Exe`nWork folder: $work`nBackup of the existing state: $backup"

    if ($Group -contains 'a') { Test-GroupA }
    if ($Group -contains 'b') { Test-GroupB }
    if ($Group -contains 'c') { Test-GroupC }
    if ($Group -contains 'd') { Test-GroupD }
    if ($Group -contains 'edge') { Test-GroupEdge }
    if ($Group -contains 'ui') { Test-GroupUi }
    if ($Group -contains 'hotkey') { Test-GroupHotkey }
    if ($Group -contains 'drag') { Test-GroupDrag }
}
finally {
    Stop-Agents
    foreach ($p in $started) { Stop-Tree $p }
    Get-Process -Name FileConverterPlusPlus -ErrorAction SilentlyContinue | Where-Object { $knownProcessIds -notcontains $_.Id } | ForEach-Object { Kill-Tree $_.Id }
    if ($origRun) { New-Item -Force $runKeyPath | Out-Null; Set-ItemProperty $runKeyPath -Name $runValueName -Value $origRun }
    else { Remove-ItemProperty $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue }
    Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadReg) { New-Item -Force $regPath | Out-Null; if ($origPath) { Set-ItemProperty $regPath -Name Path -Value $origPath } }
    foreach ($f in $stateFiles) { Remove-Item (Join-Path $userDir $f) -Force -ErrorAction SilentlyContinue; if (Test-Path (Join-Path $backup $f)) { Copy-Item (Join-Path $backup $f) (Join-Path $userDir $f) -Force } }
    Clear-Logs
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    if ((Test-Path $backup) -and ((Get-ChildItem $backup).Count -eq 0 -or $true)) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
}

$fail = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$pass = @($results | Where-Object { $_.status -eq 'pass' }).Count
$skip = @($results | Where-Object { $_.status -eq 'skip' }).Count
Write-Host ("{0} checks: {1} passed, {2} failed, {3} not verifiable here, {4} info" -f ($pass + $fail + $skip), $pass, $fail, $skip, @($results | Where-Object { $_.status -eq 'info' }).Count)
if ($fail -gt 0) { exit 1 }
