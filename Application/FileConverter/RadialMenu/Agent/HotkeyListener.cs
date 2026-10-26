// <copyright file="HotkeyListener.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Runtime.InteropServices;
    using System.Windows.Interop;

    /// <summary>
    /// Registers one global hotkey (RegisterHotKey) on a message-only window. Must be used from the UI thread.
    /// </summary>
    internal sealed class HotkeyListener : IDisposable
    {
        private const int HotkeyId = 0x4643;

        private HwndSource source;
        private bool isRegistered;

        public event EventHandler Pressed;

        /// <summary>Registers the hotkey, replacing the previous one.</summary>
        /// <param name="error">Win32 error code on failure.</param>
        public bool TryRegister(Hotkey hotkey, out int error)
        {
            this.Unregister();
            error = 0;

            if (this.source == null)
            {
                HwndSourceParameters parameters = new HwndSourceParameters("FileConverterRadialHotkey")
                {
                    ParentWindow = AgentNativeMethods.HWND_MESSAGE,
                    Width = 0,
                    Height = 0,
                };

                this.source = new HwndSource(parameters);
                this.source.AddHook(this.WndProc);
            }

            if (!AgentNativeMethods.RegisterHotKey(this.source.Handle, HotkeyId, hotkey.NativeModifiers | AgentNativeMethods.MOD_NOREPEAT, hotkey.VirtualKey))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            this.isRegistered = true;
            return true;
        }

        public void Unregister()
        {
            if (this.isRegistered && this.source != null)
            {
                AgentNativeMethods.UnregisterHotKey(this.source.Handle, HotkeyId);
            }

            this.isRegistered = false;
        }

        public void Dispose()
        {
            this.Unregister();
            if (this.source != null)
            {
                this.source.RemoveHook(this.WndProc);
                this.source.Dispose();
                this.source = null;
            }
        }

        private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == AgentNativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                this.Pressed?.Invoke(this, EventArgs.Empty);
            }

            return IntPtr.Zero;
        }
    }
}
