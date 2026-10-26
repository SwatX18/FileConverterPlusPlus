// <copyright file="RadialAgentNames.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    /// <summary>
    /// Names shared by the settings process and the radial menu tray agent.
    /// </summary>
    public static class RadialAgentNames
    {
        public const string Mutex = @"Local\FileConverterPlusPlus.RadialAgent";

        /// <summary>
        /// Auto-reset event created by the agent: the agent reloads the settings when it is set.
        /// </summary>
        public const string ReloadEvent = @"Local\FileConverterPlusPlus.RadialAgent.Reload";

        /// <summary>
        /// Auto-reset event created by the agent: the agent exits when it is set.
        /// </summary>
        public const string ExitEvent = @"Local\FileConverterPlusPlus.RadialAgent.Exit";

        /// <summary>
        /// Path of the per-user autostart key (HKEY_CURRENT_USER).
        /// </summary>
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public const string RunValueName = "FileConverterPlusPlus.RadialAgent";

        public static string BuildRunCommand(string exePath) => $"\"{exePath}\" --agent";
    }
}
