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

        private Thread thread;
        private HookSession session;
        private bool isPressed;
        private bool isPressedOnExplorer;
        private bool hasStarted;
        private NativeMethods.POINT pressedPoint;

        public CtrlDragDetector(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
        }

        /// <summary>Raised once per button press when a Ctrl+drag starts. Argument: Stopwatch timestamp of the detection.</summary>
        public event EventHandler<long> CtrlDragStarted;

        /// <summary>Raised on every left button release, the listener decides if it matters.</summary>
        public event EventHandler MouseReleased;

        public bool IsRunning => this.thread != null;

        public void Start()
        {
            if (this.thread != null)
            {
                return;
            }

            // Each hook thread owns its handle: a thread that outlives a Stop can never unhook a newer hook.
            HookSession newSession = new HookSession();
            Thread newThread = new Thread(() => this.Run(newSession)) { IsBackground = true, Name = "RadialCtrlDragHook", Priority = ThreadPriority.Highest };
            newThread.Start();
            newSession.Installed.Wait();

            if (newSession.Hook == IntPtr.Zero)
            {
                newThread.Join();
                newSession.Installed.Dispose();
                return;
            }

            Debug.Log($"Radial menu agent: Ctrl+drag mouse hook installed (thread {newSession.ThreadId}).");
            this.thread = newThread;
            this.session = newSession;
        }

        public void Stop()
        {
            Thread stoppedThread = this.thread;
            if (stoppedThread == null)
            {
                return;
            }

            this.thread = null;
            AgentNativeMethods.PostThreadMessage(this.session.ThreadId, AgentNativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            HookSession stoppedSession = this.session;
            this.session = null;
            if (stoppedThread.Join(1000))
            {
                stoppedSession.Installed.Dispose();
            }
        }

        public void Dispose()
        {
            this.Stop();
        }

        private void Run(HookSession hookSession)
        {
            hookSession.ThreadId = AgentNativeMethods.GetCurrentThreadId();

            // Kept alive by this frame for as long as the hook is installed.
            AgentNativeMethods.LowLevelMouseProc callback = (code, wParam, lParam) => this.HookProc(hookSession, code, wParam, lParam);
            hookSession.Hook = AgentNativeMethods.SetWindowsHookEx(AgentNativeMethods.WH_MOUSE_LL, callback, IntPtr.Zero, 0);
            if (hookSession.Hook == IntPtr.Zero)
            {
                Debug.Log($"Radial menu agent: can't install the mouse hook (error {Marshal.GetLastWin32Error()}), Ctrl+drag is inactive.");
            }

            hookSession.Installed.Set();
            if (hookSession.Hook == IntPtr.Zero)
            {
                return;
            }

            while (AgentNativeMethods.GetMessage(out AgentNativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                AgentNativeMethods.TranslateMessage(ref message);
                AgentNativeMethods.DispatchMessage(ref message);
            }

            AgentNativeMethods.UnhookWindowsHookEx(hookSession.Hook);
            GC.KeepAlive(callback);
        }

        private IntPtr HookProc(HookSession hookSession, int code, IntPtr wParam, IntPtr lParam)
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

            return AgentNativeMethods.CallNextHookEx(hookSession.Hook, code, wParam, lParam);
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
                    this.isPressed = false;
                    this.hasStarted = false;

                    // Always reported: if the button-up of a drag was missed, any later release removes the stuck drop shields.
                    this.dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => this.MouseReleased?.Invoke(this, EventArgs.Empty)));
                    break;
            }
        }

        private sealed class HookSession
        {
            public readonly ManualResetEventSlim Installed = new ManualResetEventSlim(false);

            public uint ThreadId;
            public IntPtr Hook;
        }
    }
}
