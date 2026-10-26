// <copyright file="ConversionProcessLauncher.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    using Debug = FileConverter.Diagnostics.Debug;

    /// <summary>
    /// Starts a conversion in a new FileConverter++ process (the agent never converts in-process). Same command line as the
    /// shell extension: files are passed as arguments, or through a list file when the command line gets too long.
    /// </summary>
    public static class ConversionProcessLauncher
    {
        public const int MaximumProcessArgumentsLength = 8000;

        /// <param name="createListFile">Writes the list of files and returns its path. Only called if the command line would be too long.</param>
        /// <param name="listFilePath">Path returned by <paramref name="createListFile"/>, or null if no list file is used.</param>
        public static string BuildArguments(string presetFullName, IReadOnlyList<string> files, Func<string> createListFile, out string listFilePath)
        {
            listFilePath = null;

            string presetArgument = $"--conversion-preset {Quote(presetFullName)}";

            StringBuilder stringBuilder = new StringBuilder(presetArgument);
            foreach (string filePath in files)
            {
                stringBuilder.Append(' ');
                stringBuilder.Append(Quote(filePath));

                if (stringBuilder.Length >= MaximumProcessArgumentsLength)
                {
                    // Alternative way of passing arguments to not overflow the command line.
                    listFilePath = createListFile();
                    return $"{presetArgument} --input-files {Quote(listFilePath)}";
                }
            }

            return stringBuilder.ToString();
        }

        /// <summary>Quotes one argument following the Windows command line rules (CommandLineToArgvW).</summary>
        public static string Quote(string argument)
        {
            StringBuilder builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    // Backslashes before a quote are doubled, then the quote is escaped.
                    builder.Append('\\', (backslashes * 2) + 1);
                }
                else
                {
                    builder.Append('\\', backslashes);
                }

                backslashes = 0;
                builder.Append(character);
            }

            // Backslashes before the closing quote are doubled.
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        public static void Launch(string exePath, string presetFullName, IReadOnlyList<string> files)
        {
            string listFilePath = null;
            string arguments = BuildArguments(presetFullName, files, () => CreateListFile(files), out listFilePath);

            Debug.Log($"Radial menu agent: start conversion process with preset '{presetFullName}' for {files.Count} file(s).");

            ProcessStartInfo processStartInfo = new ProcessStartInfo(exePath)
            {
                CreateNoWindow = false,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                Arguments = arguments,
            };

            Process process = Process.Start(processStartInfo);
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) =>
            {
                if (listFilePath != null)
                {
                    try
                    {
                        File.Delete(listFilePath);
                    }
                    catch (Exception)
                    {
                    }
                }

                process.Dispose();
            };
        }

        private static string CreateListFile(IReadOnlyList<string> files)
        {
            // Store the list of files to convert in a file in the Temp folder.
            string fileListPath = Path.Combine(Path.GetTempPath(), "file-converter-input-list.txt");
            int index = 1;
            while (File.Exists(fileListPath))
            {
                fileListPath = Path.Combine(Path.GetTempPath(), $"file-converter-input-list-{index}.txt");
                index++;
            }

            File.WriteAllLines(fileListPath, files);
            return fileListPath;
        }
    }
}
