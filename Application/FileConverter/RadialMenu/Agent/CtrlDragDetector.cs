// <copyright file="CtrlDragDetector.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows.Threading;

    using Debug = FileConverter.Diagnostics.Debug;

    /// <summary>
    /// Detects a left button drag started on an Explorer window while Ctrl is held, with a low-level mouse hook (no injection).
    /// The hook lives on its own thread with its own message loop, it never swallows an event.
    /// Events are raised on the dispatcher given at construction.
    /// </summary>
    internal sealed class CtrlDragDetector : IDisposable
    {
        private const string ExplorerWindowClass = "CabinetWClass";

        private readonly Dispatcher dispatcher;
        private readonly AgentNativeMethods.LowLevelMouseProc callback;

        private Thread thread;
        private uint threadId;
        private IntPtr hook;
        private bool isPressed;
        private bool isPressedOnExplorer;
        private bool hasStarted;
        private NativeMethods.POINT pressedPoint;

        public CtrlDragDetector(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.callback = this.HookProc;
        }

        /// <summary>Raised once per button press when a Ctrl+drag starts. Argument: Stopwatch timestamp of the detection.</summary>
        public event EventHandler<long> CtrlDragStarted;

        public event EventHandler MouseReleased;

        public bool IsRunning => this.thread != null;

        public void Start()
        {
            if (this.thread != null)
            {
                return;
            }

            using (ManualResetEventSlim installed = new ManualResetEventSlim(false))
            {
                this.thread = new Thread(() => this.Run(installed)) { IsBackground = true, Name = "RadialCtrlDragHook" };
                this.thread.Start();
                installed.Wait();
            }

            if (this.hook == IntPtr.Zero)
            {
                this.thread.Join();
                this.thread = null;
            }
        }

        public void Stop()
        {
            Thread stoppedThread = this.thread;
            if (stoppedThread == null)
            {
                return;
            }

            this.thread = null;
            AgentNativeMethods.PostThreadMessage(this.threadId, AgentNativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            stoppedThread.Join(1000);
        }

        public void Dispose()
        {
            this.Stop();
        }

        private void Run(ManualResetEventSlim installed)
        {
            this.threadId = AgentNativeMethods.GetCurrentThreadId();
            this.hook = AgentNativeMethods.SetWindowsHookEx(AgentNativeMethods.WH_MOUSE_LL, this.callback, IntPtr.Zero, 0);
            if (this.hook == IntPtr.Zero)
            {
                Debug.Log($"Radial menu agent: can't install the mouse hook (error {Marshal.GetLastWin32Error()}), Ctrl+drag is inactive.");
            }

            installed.Set();
            if (this.hook == IntPtr.Zero)
            {
                return;
            }

            while (AgentNativeMethods.GetMessage(out AgentNativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                AgentNativeMethods.TranslateMessage(ref message);
                AgentNativeMethods.DispatchMessage(ref message);
            }

            AgentNativeMethods.UnhookWindowsHookEx(this.hook);
            this.hook = IntPtr.Zero;
        }

        private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                try
                {
                    this.Handle(wParam.ToInt32(), Marshal.PtrToStructure<AgentNativeMethods.MSLLHOOKSTRUCT>(lParam).Point);
                }
                catch (Exception)
                {
                    // Never let an exception escape from the hook.
                }
            }

            return AgentNativeMethods.CallNextHookEx(this.hook, code, wParam, lParam);
        }

        private void Handle(int message, NativeMethods.POINT point)
        {
            switch (message)
            {
                case AgentNativeMethods.WM_LBUTTONDOWN:
                    this.isPressed = true;
                    this.hasStarted = false;
                    this.pressedPoint = point;
                    IntPtr root = AgentNativeMethods.GetAncestor(AgentNativeMethods.WindowFromPoint(point), AgentNativeMethods.GA_ROOT);
                    this.isPressedOnExplorer = root != IntPtr.Zero && AgentNativeMethods.GetWindowClassName(root) == ExplorerWindowClass;
                    break;

                case AgentNativeMethods.WM_MOUSEMOVE:
                    if (this.isPressed && this.isPressedOnExplorer && !this.hasStarted)
                    {
                        bool movedEnough = Math.Abs(point.X - this.pressedPoint.X) > AgentNativeMethods.GetSystemMetrics(AgentNativeMethods.SM_CXDRAG) ||
                                           Math.Abs(point.Y - this.pressedPoint.Y) > AgentNativeMethods.GetSystemMetrics(AgentNativeMethods.SM_CYDRAG);
                        if (movedEnough && (AgentNativeMethods.GetAsyncKeyState(AgentNativeMethods.VK_CONTROL) & 0x8000) != 0)
                        {
                            this.hasStarted = true;
                            long timestamp = Stopwatch.GetTimestamp();
                            this.dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => this.CtrlDragStarted?.Invoke(this, timestamp)));
                        }
                    }

                    break;

                case AgentNativeMethods.WM_LBUTTONUP:
                    bool wasStarted = this.hasStarted;
                    this.isPressed = false;
                    this.hasStarted = false;
                    if (wasStarted)
                    {
                        this.dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => this.MouseReleased?.Invoke(this, EventArgs.Empty)));
                    }

                    break;
            }
        }
    }
}
