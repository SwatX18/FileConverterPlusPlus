// <copyright file="RadialAgent.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Security.AccessControl;
    using System.Threading;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;

    using CommunityToolkit.Mvvm.DependencyInjection;

    using FileConverter.Services;
    using FileConverter.Views;

    using Microsoft.Extensions.DependencyInjection;

    using Debug = FileConverter.Diagnostics.Debug;
    using Resources = FileConverter.Properties.Resources;

    /// <summary>
    /// Per-user tray agent (--agent): owns the global hotkey (trigger C) and the Ctrl+drag detection (trigger D) and shows the
    /// radial menu. A pick starts a new FileConverter++ process, the agent never converts. Lives on the WPF UI thread.
    /// </summary>
    internal sealed class RadialAgent : IDisposable
    {
        private static readonly TimeSpan DragTeardownGrace = TimeSpan.FromMilliseconds(150);
        private const double StuckShieldTimeoutMilliseconds = 1000;

        private readonly Dispatcher dispatcher;
        private readonly string exePath;
        private readonly List<DropShieldForm> shields = new List<DropShieldForm>();

        private Mutex mutex;
        private EventWaitHandle reloadEvent;
        private EventWaitHandle exitEvent;
        private RegisteredWaitHandle reloadWait;
        private RegisteredWaitHandle exitWait;
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private Icon icon;
        private RadialMenuWindow donut;
        private HotkeyListener hotkeyListener;
        private CtrlDragDetector dragDetector;
        private DispatcherTimer teardownTimer;
        private DispatcherTimer stuckShieldTimer;
        private long buttonUpSince;
        private long dragTimestamp;
        private bool isDropMenuRequested;
        private bool isDisposed;

        public RadialAgent()
        {
            this.dispatcher = System.Windows.Application.Current.Dispatcher;
            using (Process process = Process.GetCurrentProcess())
            {
                this.exePath = process.MainModule.FileName;
            }
        }

        /// <summary>Starts the agent. Returns false if the process has nothing to do and should exit.</summary>
        public bool Start()
        {
            this.mutex = new Mutex(true, RadialAgentNames.Mutex, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Another agent is running: let it re-read the settings.
                Debug.Log("Radial menu agent: already running, ask it to reload the settings.");
                if (EventWaitHandle.TryOpenExisting(RadialAgentNames.ReloadEvent, EventWaitHandleRights.Modify, out EventWaitHandle existingReloadEvent))
                {
                    using (existingReloadEvent)
                    {
                        existingReloadEvent.Set();
                    }
                }

                this.mutex.Dispose();
                this.mutex = null;
                return false;
            }

            this.reloadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RadialAgentNames.ReloadEvent);
            this.exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RadialAgentNames.ExitEvent);
            this.reloadWait = ThreadPool.RegisterWaitForSingleObject(this.reloadEvent, (state, timedOut) => this.dispatcher.BeginInvoke((Action)this.Reload), null, Timeout.Infinite, false);
            this.exitWait = ThreadPool.RegisterWaitForSingleObject(this.exitEvent, (state, timedOut) => this.dispatcher.BeginInvoke((Action)this.RequestExit), null, Timeout.Infinite, false);

            ISettingsService settingsService = Ioc.Default.GetRequiredService<ISettingsService>();
            if (settingsService.Settings == null)
            {
                Debug.Log("Radial menu agent: can't load the settings.");
                return false;
            }

            if (!settingsService.Settings.ShouldRunRadialAgent)
            {
                Debug.Log("Radial menu agent: no trigger enabled, exit.");
                RadialAgentController.SetAutostart(false, this.exePath);
                return false;
            }

            ConversionProcessLauncher.DeleteStaleListFiles(TimeSpan.FromDays(1));

            this.CreateTrayIcon();

            // The menu is created hidden once, showing it later is cheap.
            this.donut = new RadialMenuWindow { HideInsteadOfClose = true };
            new WindowInteropHelper(this.donut).EnsureHandle();
            this.donut.PresetChosen += this.Donut_PresetChosen;
            this.donut.Dismissed += this.Donut_Dismissed;

            this.teardownTimer = new DispatcherTimer(DispatcherPriority.Send, this.dispatcher) { Interval = DragTeardownGrace };
            this.teardownTimer.Tick += (sender, args) => this.EndDrag();

            // Safety net: shields are removed if the button has been up for a while (a missed button-up event).
            this.stuckShieldTimer = new DispatcherTimer(DispatcherPriority.Normal, this.dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            this.stuckShieldTimer.Tick += (sender, args) => this.CheckStuckShields();

            this.ApplyTriggers(settingsService.Settings);
            Debug.Log("Radial menu agent started.");

            this.dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)TrimWorkingSet);
            return true;
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;

            this.reloadWait?.Unregister(null);
            this.exitWait?.Unregister(null);
            this.dragDetector?.Dispose();
            this.hotkeyListener?.Dispose();
            this.teardownTimer?.Stop();
            this.stuckShieldTimer?.Stop();
            this.DisposeShields();

            if (this.donut != null)
            {
                this.donut.PresetChosen -= this.Donut_PresetChosen;
                this.donut.Dismissed -= this.Donut_Dismissed;
                this.donut.HideInsteadOfClose = false;
                this.donut.Close();
            }

            if (this.notifyIcon != null)
            {
                this.notifyIcon.Visible = false;
                this.notifyIcon.Dispose();
            }

            this.icon?.Dispose();
            this.reloadEvent?.Dispose();
            this.exitEvent?.Dispose();

            if (this.mutex != null)
            {
                try
                {
                    this.mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                this.mutex.Dispose();
            }

            Debug.Log("Radial menu agent stopped.");
        }

        private static void TrimWorkingSet()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                AgentNativeMethods.SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
            }
        }

        private void CreateTrayIcon()
        {
            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(Resources.RadialMenuTraySettings, null, (sender, args) => this.OpenSettings());
            menu.Items.Add(Resources.RadialMenuTrayExit, null, (sender, args) => this.RequestExit());

            this.icon = Icon.ExtractAssociatedIcon(this.exePath);
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = this.icon,
                Text = Resources.RadialMenuTrayTooltip,
                ContextMenuStrip = menu,
                Visible = true,
            };
        }

        private void OpenSettings()
        {
            try
            {
                Process.Start(this.exePath, "--settings")?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.Log($"Radial menu agent: can't open the settings: {exception.Message}");
            }
        }

        private void RequestExit()
        {
            if (!this.isDisposed)
            {
                System.Windows.Application.Current.Shutdown(0);
            }
        }

        private void Reload()
        {
            if (this.isDisposed)
            {
                return;
            }

            Debug.Log("Radial menu agent: reload the settings.");
            ISettingsService settingsService = Ioc.Default.GetRequiredService<ISettingsService>();
            settingsService.RevertSettings();

            Settings settings = settingsService.Settings;
            if (settings == null || !settings.ShouldRunRadialAgent)
            {
                Debug.Log("Radial menu agent: no trigger enabled anymore, exit.");
                RadialAgentController.SetAutostart(false, this.exePath);
                this.RequestExit();
                return;
            }

            this.ApplyTriggers(settings);
        }

        private void ApplyTriggers(Settings settings)
        {
            // Trigger C: global hotkey.
            if (settings.RadialMenuHotkeyEnabled)
            {
                if (this.hotkeyListener == null)
                {
                    this.hotkeyListener = new HotkeyListener();
                    this.hotkeyListener.Pressed += this.Hotkey_Pressed;
                }

                if (!Hotkey.TryParse(settings.RadialMenuHotkey, out Hotkey hotkey))
                {
                    this.hotkeyListener.Unregister();
                    this.ReportHotkeyFailure(settings.RadialMenuHotkey, "invalid hotkey");
                }
                else if (!this.hotkeyListener.TryRegister(hotkey, out int error))
                {
                    this.ReportHotkeyFailure(hotkey.ToString(), $"win32 error {error}");
                }
                else
                {
                    Debug.Log($"Radial menu agent: hotkey {hotkey} registered.");
                }
            }
            else
            {
                this.hotkeyListener?.Unregister();
            }

            // Trigger D: Ctrl+drag.
            if (settings.RadialMenuCtrlDragEnabled)
            {
                if (this.dragDetector == null)
                {
                    this.dragDetector = new CtrlDragDetector(this.dispatcher);
                    this.dragDetector.CtrlDragStarted += this.Drag_Started;
                    this.dragDetector.MouseReleased += this.Drag_Released;
                }

                this.dragDetector.Start();
            }
            else
            {
                // Shields would stay up without the button-up event.
                this.EndDrag();
                this.dragDetector?.Stop();
            }
        }

        private void ReportHotkeyFailure(string hotkeyText, string reason)
        {
            Debug.Log($"Radial menu agent: can't register the hotkey '{hotkeyText}' ({reason}).");
            this.notifyIcon?.ShowBalloonTip(
                5000,
                Resources.RadialMenuTrayTooltip,
                string.Format(Resources.RadialMenuHotkeyRegistrationFailed, hotkeyText),
                System.Windows.Forms.ToolTipIcon.Warning);
        }

        private void Hotkey_Pressed(object sender, EventArgs e)
        {
            long timestamp = Stopwatch.GetTimestamp();

            if (!ExplorerSelection.TryGetSelectedFiles(AgentNativeMethods.GetForegroundWindow(), out IReadOnlyList<string> files))
            {
                Debug.Log("Radial menu agent: hotkey pressed without an Explorer selection.");
                return;
            }

            this.ShowMenu(files, RadialMenuInputMode.Click, timestamp);
        }

        private void Drag_Started(object sender, long timestamp)
        {
            if (this.isDisposed)
            {
                return;
            }

            this.EndDrag();
            this.dragTimestamp = timestamp;
            this.isDropMenuRequested = false;

            this.buttonUpSince = 0;
            this.stuckShieldTimer.Start();

            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                try
                {
                    DropShieldForm shield = new DropShieldForm(screen.Bounds, this.Shield_DragEnter, () => this.dispatcher.BeginInvoke((Action)this.EndDrag));
                    this.shields.Add(shield);
                    shield.Show();
                }
                catch (Exception exception)
                {
                    Debug.Log($"Radial menu agent: can't create a drop shield: {exception.Message}");
                }
            }
        }

        private void Drag_Released(object sender, EventArgs e)
        {
            // Let a pending drop reach the menu before it is torn down.
            if (this.shields.Count > 0)
            {
                this.teardownTimer.Stop();
                this.teardownTimer.Start();
            }
        }

        private void Shield_DragEnter(System.Windows.Forms.IDataObject data)
        {
            if (this.isDropMenuRequested || !data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop))
            {
                return;
            }

            string[] dropped = data.GetData(System.Windows.Forms.DataFormats.FileDrop) as string[];
            string[] files = dropped == null ? new string[0] : dropped.Where(File.Exists).ToArray();
            if (files.Length == 0)
            {
                return;
            }

            this.isDropMenuRequested = true;
            this.ShowMenu(files, RadialMenuInputMode.Drop, this.dragTimestamp);
        }

        private void EndDrag()
        {
            this.teardownTimer.Stop();
            this.stuckShieldTimer.Stop();
            this.DisposeShields();

            if (this.isDropMenuRequested)
            {
                this.isDropMenuRequested = false;
                this.donut.Dismiss();
            }
        }

        private void CheckStuckShields()
        {
            if (this.shields.Count == 0)
            {
                this.stuckShieldTimer.Stop();
                return;
            }

            if ((AgentNativeMethods.GetAsyncKeyState(AgentNativeMethods.GetPrimaryButtonVirtualKey()) & 0x8000) != 0)
            {
                this.buttonUpSince = 0;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (this.buttonUpSince == 0)
            {
                this.buttonUpSince = now;
            }
            else if ((now - this.buttonUpSince) * 1000d / Stopwatch.Frequency > StuckShieldTimeoutMilliseconds)
            {
                Debug.Log("Radial menu agent: the mouse button is up but the drop shields are still shown, remove them.");
                this.EndDrag();
            }
        }

        private void DisposeShields()
        {
            foreach (DropShieldForm shield in this.shields)
            {
                shield.Hide();
                shield.Dispose();
            }

            this.shields.Clear();
        }

        private void ShowMenu(IReadOnlyList<string> files, RadialMenuInputMode mode, long triggerTimestamp)
        {
            Settings settings = Ioc.Default.GetRequiredService<ISettingsService>().Settings;
            if (settings == null)
            {
                return;
            }

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            this.donut.ShowMenu(files, settings.ConversionPresets, new Point(cursor.X, cursor.Y), mode);

            // Log when the first frame is rendered.
            EventHandler onRendering = null;
            onRendering = (sender, args) =>
            {
                CompositionTarget.Rendering -= onRendering;
                double elapsed = (Stopwatch.GetTimestamp() - triggerTimestamp) * 1000d / Stopwatch.Frequency;
                Debug.Log($"Radial menu shown in {(int)elapsed} ms (from trigger).");
            };

            CompositionTarget.Rendering += onRendering;
        }

        private void Donut_PresetChosen(object sender, RadialPresetChosenEventArgs e)
        {
            try
            {
                ConversionProcessLauncher.Launch(this.exePath, e.Preset.FullName, e.FilePaths);
            }
            catch (Exception exception)
            {
                Debug.Log($"Radial menu agent: can't start the conversion: {exception.Message}");
            }

            // Not inside the drop callback.
            this.dispatcher.BeginInvoke((Action)this.donut.Dismiss);
        }

        private void Donut_Dismissed(object sender, EventArgs e)
        {
            TrimWorkingSet();
        }
    }
}
