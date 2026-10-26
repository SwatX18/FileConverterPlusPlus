// <copyright file="RadialAgentController.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;

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

        public static bool IsAgentRunning()
        {
            throw new NotImplementedException();
        }

        public static void SetAutostart(bool enabled, string exePath)
        {
            throw new NotImplementedException();
        }

        public static void ApplySettings(Settings settings, string exePath)
        {
            throw new NotImplementedException();
        }

        public static bool StopAgent(TimeSpan timeout)
        {
            throw new NotImplementedException();
        }
    }
}
