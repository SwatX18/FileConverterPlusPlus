// <copyright file="ExtensionLog.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverterExtension
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Diagnostics for the Explorer extension, which runs inside Explorer and has no UI of its own.
    /// Anomalies (<see cref="Warn"/>) always go to the log file; routine traces (<see cref="Info"/>) only when the registry
    /// value HKCU\Software\FileConverter\ExtensionLog is "true". Everything is also sent to OutputDebugString
    /// (visible with DebugView). Never throws.
    /// </summary>
    public static class ExtensionLog
    {
        private const long MaximumFileLength = 1024 * 1024;

        private static readonly object Sync = new object();

        /// <summary>Gets the log file path (%LOCALAPPDATA%\FileConverter\ShellExtension.log).</summary>
        public static string FilePath
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileConverter");
                return Path.Combine(folder, "ShellExtension.log");
            }
        }

        /// <summary>Gets a value indicating whether routine traces are written to the file.</summary>
        public static bool Verbose
        {
            get
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\FileConverter"))
                    {
                        string value = key?.GetValue("ExtensionLog") as string;
                        return bool.TryParse(value, out bool enabled) && enabled;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message, Verbose);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, true);
        }

        private static void Write(string level, string message, bool toFile)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Process.GetCurrentProcess().Id}] {level} {message}";

            try
            {
                Debug.WriteLine("FileConverter++ extension: " + line);
            }
            catch
            {
            }

            if (!toFile)
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));

                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > MaximumFileLength)
                    {
                        string old = path + ".old";
                        if (File.Exists(old))
                        {
                            File.Delete(old);
                        }

                        File.Move(path, old);
                    }

                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Several Explorer processes may write at the same time; a lost line is acceptable.
            }
        }
    }
}
