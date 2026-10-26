// <copyright file="RadialAgentController.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;
    using System.Diagnostics;
    using System.Security.AccessControl;
    using System.Threading;

    using Microsoft.Win32;

    using Debug = FileConverter.Diagnostics.Debug;

    /// <summary>
    /// Starts, reloads and stops the radial menu tray agent, and manages its autostart, from the settings process.
    /// </summary>
    public static class RadialAgentController
    {
        public enum AgentAction
        {
            None,
            Start,
            Reload,
            Stop
        }

        public static AgentAction ComputeAction(bool shouldRun, bool isRunning)
        {
            if (shouldRun)
            {
                return isRunning ? AgentAction.Reload : AgentAction.Start;
            }

            return isRunning ? AgentAction.Stop : AgentAction.None;
        }

        /// <summary>
        /// The agent owns a named mutex for its whole life: it is running as long as the mutex can be opened.
        /// </summary>
        public static bool IsAgentRunning()
        {
            if (Mutex.TryOpenExisting(RadialAgentNames.Mutex, MutexRights.Synchronize, out Mutex mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }

        public static void SetAutostart(bool enabled, string exePath)
        {
            SetAutostart(enabled, exePath, RadialAgentNames.RunKeyPath);
        }

        /// <summary>
        /// Applies the settings: autostart value, then start / reload / stop of the agent.
        /// </summary>
        public static void ApplySettings(Settings settings, string exePath)
        {
            ApplySettings(settings, exePath, allowStart: true);
        }

        /// <summary>
        /// Same as <see cref="ApplySettings(Settings, string)"/>, but the agent is only started if <paramref name="allowStart"/> is true.
        /// The post installation initialization must not start it: the installer can run elevated and the agent has to run at the
        /// integrity level of the user session (it starts at next logon, or when the settings are saved).
        /// </summary>
        public static void ApplySettings(Settings settings, string exePath, bool allowStart)
        {
            bool shouldRun = settings.ShouldRunRadialAgent;
            SetAutostart(shouldRun, exePath);

            AgentAction action = ComputeAction(shouldRun, IsAgentRunning());
            Debug.Log($"Radial menu agent: {action}.");
            switch (action)
            {
                case AgentAction.Start:
                    if (!allowStart)
                    {
                        break;
                    }

                    Process.Start(exePath, "--agent")?.Dispose();
                    break;

                case AgentAction.Reload:
                    SignalAgent(RadialAgentNames.ReloadEvent);
                    break;

                case AgentAction.Stop:
                    SignalAgent(RadialAgentNames.ExitEvent);
                    break;
            }
        }

        /// <summary>
        /// Asks the agent to exit and waits until it is gone.
        /// </summary>
        /// <returns>True if the agent is not running anymore (or was not running).</returns>
        public static bool StopAgent(TimeSpan timeout)
        {
            if (!IsAgentRunning())
            {
                return true;
            }

            SignalAgent(RadialAgentNames.ExitEvent);

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (IsAgentRunning())
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    return false;
                }

                Thread.Sleep(50);
            }

            return true;
        }

        /// <summary>
        /// Writes or removes the autostart value (under HKEY_CURRENT_USER\<paramref name="runKeyPath"/>).
        /// </summary>
        internal static void SetAutostart(bool enabled, string exePath, string runKeyPath)
        {
            if (enabled)
            {
                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath))
                {
                    key.SetValue(RadialAgentNames.RunValueName, RadialAgentNames.BuildRunCommand(exePath), RegistryValueKind.String);
                }
            }
            else
            {
                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                {
                    key?.DeleteValue(RadialAgentNames.RunValueName, false);
                }
            }
        }

        private static void SignalAgent(string eventName)
        {
            // The agent can exit between the check and the signal: nothing to do then.
            if (EventWaitHandle.TryOpenExisting(eventName, EventWaitHandleRights.Modify, out EventWaitHandle handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
    }
}
