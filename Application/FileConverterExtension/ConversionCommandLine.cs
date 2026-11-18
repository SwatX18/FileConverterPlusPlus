// <copyright file="ConversionCommandLine.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverterExtension
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Builds the command line the extension uses to start FileConverterPlusPlus.exe: Windows command-line quoting
    /// (the rules CommandLineToArgvW / the .NET runtime parse back), and a private input-list file when the selection
    /// does not fit on the command line.
    /// </summary>
    public static class ConversionCommandLine
    {
        /// <summary>
        /// Conservative limit for the whole command line (the documented one is 32767 characters for CreateProcess, and 8191
        /// for some shell launchers, see https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/command-line-string-limitation).
        /// </summary>
        public const int MaximumCommandLineLength = 8000;

        public const string InputListPrefix = "file-converter-input-list-";
        public const string InputListExtension = ".txt";

        public sealed class Invocation
        {
            /// <summary>Gets the arguments (without the executable).</summary>
            public string Arguments { get; internal set; }

            /// <summary>Gets the input list file written for this invocation, or null when everything fits on the command line.</summary>
            public string InputListPath { get; internal set; }
        }

        /// <summary>
        /// Quotes one argument so that the Windows C runtime / .NET parse it back unchanged: it only adds quotes when needed,
        /// doubles the backslashes that precede a quote, and escapes embedded quotes.
        /// </summary>
        public static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                argument = string.Empty;
            }

            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            var builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    backslashes = 0;
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(c);
                    backslashes = 0;
                }
            }

            // Trailing backslashes would escape the closing quote.
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Builds the arguments for a conversion. Files are passed directly when the whole command line fits
        /// in <see cref="MaximumCommandLineLength"/>, otherwise through a new, uniquely named list file in
        /// <paramref name="tempFolder"/> (one path per line, UTF-8), so nothing is ever truncated or dropped.
        /// </summary>
        public static Invocation Build(string executablePath, string presetName, IReadOnlyList<string> filePaths, string tempFolder)
        {
            string presetArgument = "--conversion-preset " + QuoteArgument(presetName);

            var direct = new StringBuilder(presetArgument);
            foreach (string filePath in filePaths)
            {
                direct.Append(' ').Append(QuoteArgument(filePath));
            }

            int executableLength = QuoteArgument(executablePath ?? string.Empty).Length + 1;
            if (executableLength + direct.Length <= MaximumCommandLineLength)
            {
                return new Invocation { Arguments = direct.ToString() };
            }

            DeleteStaleInputLists(tempFolder);
            string listPath = WriteInputList(tempFolder, filePaths);
            return new Invocation
            {
                Arguments = presetArgument + " --input-files " + QuoteArgument(listPath),
                InputListPath = listPath,
            };
        }

        /// <summary>
        /// Writes the paths to a new file. The name contains a GUID and the file is created with CreateNew, so concurrent
        /// invocations (several right-clicks, several Explorer processes) can't pick or overwrite the same file.
        /// </summary>
        public static string WriteInputList(string tempFolder, IEnumerable<string> filePaths)
        {
            Directory.CreateDirectory(tempFolder);
            string path = Path.Combine(tempFolder, InputListPrefix + Guid.NewGuid().ToString("N") + InputListExtension);

            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (string filePath in filePaths)
                {
                    writer.WriteLine(filePath);
                }
            }

            return path;
        }

        /// <summary>Deletes an input list created by <see cref="WriteInputList"/>. Never throws.</summary>
        public static void TryDeleteInputList(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                ExtensionLog.Warn($"Can't delete the input list '{path}': {exception.Message}");
            }
        }

        /// <summary>Removes lists left over by crashed or killed sessions (older than one day). Never throws.</summary>
        public static void DeleteStaleInputLists(string tempFolder)
        {
            try
            {
                foreach (string file in Directory.GetFiles(tempFolder, InputListPrefix + "*" + InputListExtension))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                    {
                        TryDeleteInputList(file);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
