// <copyright file="ConversionProcessLauncherTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System.Collections.Generic;
    using System.Linq;

    using FileConverter.RadialMenu.Agent;

    using Xunit;

    public class ConversionProcessLauncherTests
    {
        [Fact]
        public void FewFilesArePassedAsQuotedArguments()
        {
            bool listCreated = false;
            string arguments = ConversionProcessLauncher.BuildArguments(
                @"Audio\To Mp3",
                new[] { @"C:\My Files\a b.wav", @"C:\c.wav" },
                () =>
                {
                    listCreated = true;
                    return null;
                },
                out string listFilePath);

            Assert.Equal("--conversion-preset \"Audio\\To Mp3\" \"C:\\My Files\\a b.wav\" \"C:\\c.wav\"", arguments);
            Assert.Null(listFilePath);
            Assert.False(listCreated);
        }

        [Fact]
        public void TooManyFilesSwitchToAListFile()
        {
            List<string> files = Enumerable.Range(0, 500).Select(index => $@"C:\Some\Folder\With\A\Long\Path\file-number-{index}.wav").ToList();

            string arguments = ConversionProcessLauncher.BuildArguments(@"Audio\To Mp3", files, () => @"C:\Temp\file-converter-input-list.txt", out string listFilePath);

            Assert.Equal(@"C:\Temp\file-converter-input-list.txt", listFilePath);
            Assert.Equal("--conversion-preset \"Audio\\To Mp3\" --input-files \"C:\\Temp\\file-converter-input-list.txt\"", arguments);
            Assert.True(arguments.Length < ConversionProcessLauncher.MaximumProcessArgumentsLength);
        }

        [Fact]
        public void ArgumentsJustUnderTheLimitStayInline()
        {
            // Each file adds 'C:\xxxx...' + 3 characters (space and quotes).
            string preset = "--conversion-preset \"P\"";
            int fileLength = 100;
            int count = (ConversionProcessLauncher.MaximumProcessArgumentsLength - preset.Length - 1) / (fileLength + 3);
            List<string> files = Enumerable.Range(0, count).Select(index => @"C:\" + new string('x', fileLength - 3)).ToList();

            string arguments = ConversionProcessLauncher.BuildArguments("P", files, () => "list.txt", out string listFilePath);

            Assert.Null(listFilePath);
            Assert.True(arguments.Length < ConversionProcessLauncher.MaximumProcessArgumentsLength);
            Assert.Contains("\"" + files[count - 1] + "\"", arguments);
        }

        [Fact]
        public void TrailingBackslashIsDoubledBeforeTheClosingQuote()
        {
            string arguments = ConversionProcessLauncher.BuildArguments("Trailing backslash\\", new[] { @"C:\a.wav" }, () => null, out _);

            Assert.Equal("--conversion-preset \"Trailing backslash\\\\\" \"C:\\a.wav\"", arguments);
        }

        [Fact]
        public void BackslashesBeforeAQuoteAreDoubledAndTheQuoteEscaped()
        {
            Assert.Equal("\"a\\\\\\\"b\"", ConversionProcessLauncher.Quote("a\\\"b"));
            Assert.Equal("\"a\\b\"", ConversionProcessLauncher.Quote("a\\b"));
            Assert.Equal("\"\"", ConversionProcessLauncher.Quote(string.Empty));
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("with space")]
        [InlineData("trailing\\")]
        [InlineData("trailing\\\\")]
        [InlineData("a\\\"b")]
        [InlineData("a\"b\\")]
        [InlineData("C:\\Some Folder\\")]
        public void QuotedArgumentsRoundTripThroughTheWindowsParser(string value)
        {
            string commandLine = "prog.exe " + ConversionProcessLauncher.Quote(value) + " " + ConversionProcessLauncher.Quote("second arg\\");

            string[] parsed = ParseCommandLine(commandLine);

            Assert.Equal(new[] { "prog.exe", value, "second arg\\" }, parsed);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern System.IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr LocalFree(System.IntPtr memory);

        private static string[] ParseCommandLine(string commandLine)
        {
            System.IntPtr list = CommandLineToArgvW(commandLine, out int count);
            try
            {
                string[] result = new string[count];
                for (int index = 0; index < count; index++)
                {
                    result[index] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(list, index * System.IntPtr.Size));
                }

                return result;
            }
            finally
            {
                LocalFree(list);
            }
        }

        [Fact]
        public void QuotesInPresetNameAreEscaped()
        {
            string arguments = ConversionProcessLauncher.BuildArguments("My \"best\" preset", new[] { @"C:\a.wav" }, () => null, out _);

            Assert.StartsWith("--conversion-preset \"My \\\"best\\\" preset\" ", arguments);
        }
    }
}
