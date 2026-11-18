// <copyright file="ShellExtensionTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading.Tasks;

    using FileConverterExtension;
    using ExtensionClass = global::FileConverterExtension.FileConverterExtension;

    using Xunit;

    /// <summary>
    /// The shell extension's own logic: command line / input list building and the choice of the compatible presets for a
    /// selection that mixes root files, nested files and several extensions. The COM side (what Explorer hands over) is
    /// covered by Tests/ShellProbe, see Tests/README.md.
    /// </summary>
    public sealed class ShellExtensionTests : IDisposable
    {
        private const string Exe = @"C:\Program Files\FileConverter++\FileConverterPlusPlus.exe";

        private readonly string folder = Path.Combine(Path.GetTempPath(), "FileConverter.Tests-" + Guid.NewGuid().ToString("N"));

        public ShellExtensionTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, recursive: true);
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        /// <summary>Parses a command line the way Windows (and so the .NET runtime) does. Index 0 is the executable.</summary>
        private static string[] Parse(string arguments)
        {
            IntPtr argv = CommandLineToArgvW("app.exe " + arguments, out int argc);
            Assert.NotEqual(IntPtr.Zero, argv);
            try
            {
                string[] result = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                }

                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        public static IEnumerable<object[]> AwkwardPaths()
        {
            yield return new object[] { @"C:\Movies\plain.avi" };
            yield return new object[] { @"C:\Movies\with space\a b.avi" };
            yield return new object[] { @"C:\Movies\Film (2010) [1080p] & more!.avi" };
            yield return new object[] { @"C:\Movies\100% ^caret $dollar #hash @at ~tilde =eq ,comma ;semi 'apos'.avi" };
            yield return new object[] { "C:\\Movies\\юникод 日本語 ñ é 😀.avi" };
            yield return new object[] { @"\\nas\share\Movies\sub folder\x.avi" };
            yield return new object[] { @"C:\Movies\folder with trailing backslash\" };
            yield return new object[] { @"C:\Movies\two trailing\\" };
            yield return new object[] { @"C:\Movies\back\\slash before quote.avi" };
        }

        [Theory]
        [MemberData(nameof(AwkwardPaths))]
        public void QuotedArgumentIsParsedBackUnchanged(string path)
        {
            string[] parsed = Parse("--conversion-preset " + ConversionCommandLine.QuoteArgument("To mp4") + " " + ConversionCommandLine.QuoteArgument(path));

            Assert.Equal(new[] { "app.exe", "--conversion-preset", "To mp4", path }, parsed);
        }

        [Fact]
        public void ArgumentsWithQuotesAndEmptyArgumentsRoundTrip()
        {
            string[] values = { "He said \"hi\"", "ends with quote\"", string.Empty, "\\\"", "a\\\\\"b" };
            string line = string.Join(" ", values.Select(ConversionCommandLine.QuoteArgument));

            Assert.Equal(new[] { "app.exe" }.Concat(values), Parse(line));
        }

        [Fact]
        public void SmallSelectionGoesOnTheCommandLineWithoutListFile()
        {
            string[] files =
            {
                @"C:\Movies\root one.avi",
                @"C:\Movies\sub1\nested_a.avi",
                @"C:\Movies\sub2\deeper\x\y\nested (deep).avi",
                @"D:\Other drive\épisode 01.avi",
            };

            ConversionCommandLine.Invocation invocation = ConversionCommandLine.Build(Exe, "Video/To mkv (remux)", files, this.folder);

            Assert.Null(invocation.InputListPath);
            Assert.Empty(Directory.GetFiles(this.folder));
            Assert.Equal(new[] { "app.exe", "--conversion-preset", "Video/To mkv (remux)" }.Concat(files), Parse(invocation.Arguments));
        }

        [Fact]
        public void LargeSelectionGoesThroughAListFileWithoutLosingOrDuplicatingFiles()
        {
            List<string> files = new List<string>();
            for (int i = 0; i < 600; i++)
            {
                // Different parents, spaces, parentheses, non-ASCII, ~190 characters each: ~115 KB in total.
                files.Add($@"C:\Movies\{new string('x', 60)} ({i % 7})\sub {i}\épisode «{i:D4}» 日本語 {new string('y', 60)}.avi");
            }

            ConversionCommandLine.Invocation invocation = ConversionCommandLine.Build(Exe, "Video/To mp4", files, this.folder);

            Assert.NotNull(invocation.InputListPath);
            Assert.True(invocation.Arguments.Length < ConversionCommandLine.MaximumCommandLineLength);
            string[] parsed = Parse(invocation.Arguments);
            Assert.Equal(new[] { "app.exe", "--conversion-preset", "Video/To mp4", "--input-files", invocation.InputListPath }, parsed);

            string[] lines = File.ReadAllLines(invocation.InputListPath);
            Assert.Equal(files, lines);
            Assert.Equal(files.Count, lines.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            ConversionCommandLine.TryDeleteInputList(invocation.InputListPath);
            Assert.False(File.Exists(invocation.InputListPath));
        }

        [Fact]
        public void ListFileIsUtf8WithoutBomSoUnicodeNamesSurvive()
        {
            string path = ConversionCommandLine.WriteInputList(this.folder, new[] { "C:\\Фильмы\\кино 日本語.avi" });
            byte[] bytes = File.ReadAllBytes(path);

            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            // The application reads it with a default StreamReader (UTF-8).
            using (StreamReader reader = new StreamReader(path))
            {
                Assert.Equal("C:\\Фильмы\\кино 日本語.avi", reader.ReadLine());
            }
        }

        [Fact]
        public void ConcurrentInvocationsNeverShareOrOverwriteAListFile()
        {
            const int Count = 64;
            string[] paths = new string[Count];
            Parallel.For(0, Count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                paths[i] = ConversionCommandLine.WriteInputList(this.folder, new[] { $@"C:\Movies\file {i}.avi" });
            });

            Assert.Equal(Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal(new[] { $@"C:\Movies\file {i}.avi" }, File.ReadAllLines(paths[i]));
            }
        }

        [Fact]
        public void OnlyStaleListsAreRemovedByTheCleanup()
        {
            string fresh = ConversionCommandLine.WriteInputList(this.folder, new[] { "a" });
            string stale = ConversionCommandLine.WriteInputList(this.folder, new[] { "b" });
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
            string unrelated = Path.Combine(this.folder, "keep-me.txt");
            File.WriteAllText(unrelated, "x");
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(30));

            ConversionCommandLine.DeleteStaleInputLists(this.folder);

            Assert.True(File.Exists(fresh));
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(unrelated));
        }

        [Fact]
        public void ApplicationOnlyDeletesInputListsOfTheExtension()
        {
            string temp = Path.GetTempPath();
            Assert.True(FileConverter.Application.IsExtensionInputList(Path.Combine(temp, "file-converter-input-list-0123abcd.txt")));
            Assert.True(FileConverter.Application.IsExtensionInputList(Path.Combine(temp, "file-converter-input-list.txt")));
            Assert.False(FileConverter.Application.IsExtensionInputList(Path.Combine(temp, "my-files.txt")));
            Assert.False(FileConverter.Application.IsExtensionInputList(@"C:\Users\someone\Documents\file-converter-input-list-1.txt"));
            Assert.False(FileConverter.Application.IsExtensionInputList(Path.Combine(temp, "sub", "file-converter-input-list-1.txt")));
        }

        [Fact]
        public void NormalizeDropsEmptyEntriesAndDuplicatesAndKeepsOrder()
        {
            List<string> result = ShellSelection.Normalize(new[]
            {
                @"C:\Movies\b.avi", null, "  ", @"C:\Movies\sub\a.avi", @"c:\movies\B.AVI", @"C:\Movies\sub\a.avi", @"D:\c.avi",
            });

            Assert.Equal(new[] { @"C:\Movies\b.avi", @"C:\Movies\sub\a.avi", @"D:\c.avi" }, result);
        }

        [Fact]
        public void NormalizeExpandsShortAliasesOfExistingFiles()
        {
            string longDir = Path.Combine(this.folder, "A rather long directory name");
            Directory.CreateDirectory(longDir);
            string file = Path.Combine(longDir, "A rather long file name.avi");
            File.WriteAllText(file, "x");

            // Ask Windows for the 8.3 alias; volumes with 8.3 names disabled have none, then there is nothing to expand.
            StringBuilder shortPath = new StringBuilder(512);
            if (GetShortPathName(file, shortPath, shortPath.Capacity) == 0 || shortPath.ToString().IndexOf('~') < 0)
            {
                return;
            }

            Assert.Equal(new[] { file }, ShellSelection.Normalize(new[] { shortPath.ToString() }));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetShortPathName(string path, System.Text.StringBuilder shortPath, int cch);

        [Theory]
        [InlineData(@"C:\Movies\a.avi", true)]
        [InlineData(@"C:\Movies/a.avi", true)]
        [InlineData(@"\\nas\share\a.avi", true)]
        [InlineData(@"\\?\C:\very\long\a.avi", true)]
        [InlineData(@"\\?\", false)]
        [InlineData(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
        [InlineData(@"search-ms:displayname=Search&crumb=location:C%3A%5CMovies", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ParsingNamesOfVirtualItemsAreNotTakenForPaths(string parsingName, bool expected)
        {
            Assert.Equal(expected, ShellSelection.LooksLikeFileSystemPath(parsingName));
        }

        // ---- preset eligibility for a selection (root files, nested files, mixed extensions) ----

        private static PresetReference Preset(string name, params string[] inputTypes)
        {
            PresetReference preset = (PresetReference)Activator.CreateInstance(typeof(PresetReference), nonPublic: true);
            preset.FullName = name;
            preset.InputTypes = inputTypes;
            return preset;
        }

        private static readonly PresetReference[] Presets =
        {
            Preset("Video/To mp4", "avi", "mkv", "mov", "mp4"),
            Preset("Video/To mkv (remux)", "avi", "mp4", "mov"),
            Preset("Video/To gif", "mp4", "mkv"),
            Preset("Audio/To mp3", "wav", "flac", "mp4", "mkv", "avi"),
            Preset("Image/To png", "jpg", "bmp"),
        };

        private static (bool CanShow, Dictionary<string, bool> Enabled) Evaluate(params string[] selection)
        {
            ExtensionClass extension = new ExtensionClass();
            Type type = typeof(ExtensionClass);
            const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetField("selectedPaths", Flags).SetValue(extension, selection.ToList());
            type.GetField("presetReferences", Flags).SetValue(extension, Presets);

            bool canShow = (bool)type.GetMethod("CanShowMenu", Flags).Invoke(extension, null);
            type.GetMethod("RefreshPresetList", Flags).Invoke(extension, null);

            var enabled = new Dictionary<string, bool>();
            foreach (object entry in (System.Collections.IEnumerable)type.GetField("menuEntries", Flags).GetValue(extension))
            {
                Type entryType = entry.GetType();
                PresetReference reference = (PresetReference)entryType.GetField("PresetReference").GetValue(entry);
                enabled[reference.FullName] = (bool)entryType.GetField("Enabled").GetValue(entry);
            }

            return (canShow, enabled);
        }

        [Fact]
        public void OneAviAnywhereShowsTheMenu()
        {
            // The path depth, the drive and the parent folder do not matter, only the extension does.
            Assert.True(Evaluate(@"C:\Movies\a.avi").CanShow);
            Assert.True(Evaluate(@"C:\Movies\sub1\a.avi").CanShow);
            Assert.True(Evaluate(@"C:\Movies\1\2\3\4\5\6\7\8\9\10\11\12\a.AVI").CanShow);
            Assert.True(Evaluate(@"\\nas\Movies\x\y\z\a.avi").CanShow);
        }

        [Fact]
        public void FilesFromDifferentSubdirectoriesShowTheMenuWithTheSamePresets()
        {
            var (canShow, enabled) = Evaluate(@"C:\Movies\a.avi", @"C:\Movies\sub1\b.avi", @"C:\Movies\sub2\deeper\c.avi", @"D:\elsewhere\d.avi");

            Assert.True(canShow);
            Assert.True(enabled["Video/To mp4"]);
            Assert.True(enabled["Video/To mkv (remux)"]);
            Assert.True(enabled["Audio/To mp3"]);
            Assert.False(enabled.ContainsKey("Video/To gif"));
        }

        [Fact]
        public void MixedExtensionsEnableOnlyThePresetsCompatibleWithAllOfThem()
        {
            var (canShow, enabled) = Evaluate(@"C:\Movies\a.avi", @"C:\Movies\sub\b.mkv");

            Assert.True(canShow);
            Assert.True(enabled["Video/To mp4"]);        // avi + mkv
            Assert.True(enabled["Audio/To mp3"]);        // avi + mkv
            Assert.False(enabled["Video/To mkv (remux)"]); // avi only: shown but disabled
            Assert.False(enabled["Video/To gif"]);       // mkv only: shown but disabled
        }

        [Fact]
        public void NothingConvertibleHidesTheMenu()
        {
            Assert.False(Evaluate(@"C:\Movies\notes.txt").CanShow);
            Assert.False(Evaluate(@"C:\Movies\no_extension").CanShow);
            Assert.False(Evaluate().CanShow);
        }
    }
}
