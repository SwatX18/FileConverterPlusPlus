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
        public void QuotesInPresetNameAreEscaped()
        {
            string arguments = ConversionProcessLauncher.BuildArguments("My \"best\" preset", new[] { @"C:\a.wav" }, () => null, out _);

            Assert.StartsWith("--conversion-preset \"My \\\"best\\\" preset\" ", arguments);
        }
    }
}
