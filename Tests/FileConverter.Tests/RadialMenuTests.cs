// <copyright file="RadialMenuTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows;
    using System.Windows.Input;

    using FileConverter.RadialMenu;
    using FileConverter.Services;

    using Xunit;

    public class RadialMenuTests
    {
        private static ConversionPreset CreatePreset(string name, OutputType outputType, params string[] inputTypes)
        {
            return new ConversionPreset(name, outputType, inputTypes);
        }

        // Preset compatibility.

        [Fact]
        public void ExtensionsAreLowerCaseWithoutDotAndDistinct()
        {
            HashSet<string> extensions = PresetCompatibility.GetExtensions(new[] { @"C:\a\One.MP3", @"C:\a\two.mp3", @"C:\a\three.Wav", @"C:\a\noextension", @"C:\a\trailingdot.", string.Empty, null });

            Assert.Equal(new[] { "mp3", "wav" }, extensions.OrderBy(extension => extension));
        }

        [Fact]
        public void CompatiblePresetsKeepOrderAndRequireEveryExtension()
        {
            ConversionPreset toMp3 = CreatePreset("To Mp3", OutputType.Mp3, "wav", "flac", "ogg");
            ConversionPreset toOgg = CreatePreset("To Ogg", OutputType.Ogg, "wav", "mp3");
            ConversionPreset toPng = CreatePreset("To Png", OutputType.Png, "jpg", "webp");
            ConversionPreset[] presets = { toMp3, toPng, toOgg };

            Assert.Equal(new[] { toMp3, toOgg }, PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\a.wav" }));
            Assert.Equal(new[] { toMp3, toOgg }, PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\a.WAV", @"C:\b.wav" }));
            Assert.Equal(new[] { toMp3 }, PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\a.wav", @"C:\b.flac" }));
            Assert.Equal(new[] { toPng }, PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\a.jpg", @"C:\b.webp" }));
        }

        [Fact]
        public void MixedSelectionWithoutCommonPresetGivesNothing()
        {
            ConversionPreset toMp3 = CreatePreset("To Mp3", OutputType.Mp3, "wav");
            ConversionPreset toPng = CreatePreset("To Png", OutputType.Png, "jpg");

            Assert.Empty(PresetCompatibility.GetPresetsCompatibleWithAll(new[] { toMp3, toPng }, new[] { @"C:\a.wav", @"C:\b.jpg" }));
        }

        [Fact]
        public void FilesWithoutExtensionAreIgnoredAndNoExtensionAtAllGivesNothing()
        {
            ConversionPreset toMp3 = CreatePreset("To Mp3", OutputType.Mp3, "wav");
            ConversionPreset[] presets = { toMp3 };

            Assert.Equal(new[] { toMp3 }, PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\a.wav", @"C:\README" }));
            Assert.Empty(PresetCompatibility.GetPresetsCompatibleWithAll(presets, new[] { @"C:\README" }));
            Assert.Empty(PresetCompatibility.GetPresetsCompatibleWithAll(presets, new string[0]));
            Assert.Empty(PresetCompatibility.GetPresetsCompatibleWithAll(new ConversionPreset[0], new[] { @"C:\a.wav" }));
        }

        // Pager.

        [Theory]
        [InlineData(0, 1, false)]
        [InlineData(1, 1, false)]
        [InlineData(8, 1, false)]
        [InlineData(9, 2, true)]
        [InlineData(16, 2, true)]
        [InlineData(17, 3, true)]
        [InlineData(30, 4, true)]
        public void PagerPageCount(int itemCount, int expectedPageCount, bool expectedShowsPaging)
        {
            RadialPager<int> pager = new RadialPager<int>(Enumerable.Range(0, itemCount).ToList());

            Assert.Equal(expectedPageCount, pager.PageCount);
            Assert.Equal(expectedShowsPaging, pager.ShowsPaging);
            Assert.Equal(0, pager.PageIndex);
        }

        [Fact]
        public void PagerPagesNextAndPreviousWrap()
        {
            RadialPager<int> pager = new RadialPager<int>(Enumerable.Range(0, 19).ToList());

            Assert.Equal("1/3", pager.Indicator);
            Assert.Equal(Enumerable.Range(0, 8), pager.CurrentPage);

            pager.Next();
            Assert.Equal("2/3", pager.Indicator);
            Assert.Equal(Enumerable.Range(8, 8), pager.CurrentPage);

            pager.Next();
            Assert.Equal(new[] { 16, 17, 18 }, pager.CurrentPage);

            pager.Next();
            Assert.Equal(0, pager.PageIndex);

            pager.Previous();
            Assert.Equal(2, pager.PageIndex);
            Assert.Equal("3/3", pager.Indicator);
        }

        [Fact]
        public void PagerWithoutItemsHasOneEmptyPage()
        {
            RadialPager<int> pager = new RadialPager<int>(new List<int>());

            Assert.Equal(1, pager.PageCount);
            Assert.Empty(pager.CurrentPage);
            pager.Next();
            Assert.Equal(0, pager.PageIndex);
            pager.Previous();
            Assert.Equal(0, pager.PageIndex);
        }

        [Fact]
        public void PagerRejectsInvalidArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new RadialPager<int>(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RadialPager<int>(new List<int>(), 0));
        }

        // Geometry.

        [Theory]
        [InlineData(0, 8, 337.5, 45)]
        [InlineData(1, 8, 22.5, 45)]
        [InlineData(2, 8, 67.5, 45)]
        [InlineData(0, 4, 315, 90)]
        [InlineData(3, 4, 225, 90)]
        [InlineData(0, 1, 180, 360)]
        public void SliceAnglesAreCenteredOnTheSliceIndex(int index, int sliceCount, double expectedStart, double expectedSweep)
        {
            (double start, double sweep) = RadialGeometry.GetSliceAngles(index, sliceCount);

            Assert.Equal(expectedStart, start, 6);
            Assert.Equal(expectedSweep, sweep, 6);
        }

        [Theory]
        [InlineData(0, -100, 8, 0)] // 12 o'clock
        [InlineData(100, 0, 8, 2)] // 3 o'clock
        [InlineData(0, 100, 8, 4)] // 6 o'clock
        [InlineData(-100, 0, 8, 6)] // 9 o'clock
        [InlineData(1, -100, 8, 0)]
        [InlineData(-1, -100, 8, 0)]
        [InlineData(100, -100, 8, 1)]
        [InlineData(0, -100, 1, 0)]
        [InlineData(100, 0, 3, 1)]
        [InlineData(0, 54, 8, -1)] // hub
        [InlineData(0, 0, 8, -1)]
        [InlineData(0, -151, 8, -1)] // outside
        [InlineData(0, -100, 0, -1)]
        public void HitTestFindsTheSlice(double x, double y, int sliceCount, int expected)
        {
            Assert.Equal(expected, RadialGeometry.HitTestSlice(x, y, sliceCount, RadialGeometry.InnerRadius, RadialGeometry.OuterRadius));
        }

        [Fact]
        public void HitTestAgreesWithSliceAngles()
        {
            const int sliceCount = 7;
            for (int index = 0; index < sliceCount; index++)
            {
                (double start, double sweep) = RadialGeometry.GetSliceAngles(index, sliceCount);
                double middle = (start + (sweep / 2)) * Math.PI / 180d;
                double x = 100 * Math.Sin(middle);
                double y = -100 * Math.Cos(middle);

                Assert.Equal(index, RadialGeometry.HitTestSlice(x, y, sliceCount, RadialGeometry.InnerRadius, RadialGeometry.OuterRadius));
            }
        }

        [Fact]
        public void ClampKeepsTheWindowCenteredWhenItFits()
        {
            Rect result = RadialGeometry.ClampToWorkArea(new Point(960, 540), 400, new Rect(0, 0, 1920, 1040));

            Assert.Equal(new Rect(760, 340, 400, 400), result);
        }

        [Theory]
        [InlineData(5, 5, 0, 0)]
        [InlineData(1915, 5, 1520, 0)]
        [InlineData(5, 1035, 0, 640)]
        [InlineData(1915, 1035, 1520, 640)]
        public void ClampMovesTheWindowInsideTheWorkArea(double cursorX, double cursorY, double expectedLeft, double expectedTop)
        {
            Rect result = RadialGeometry.ClampToWorkArea(new Point(cursorX, cursorY), 400, new Rect(0, 0, 1920, 1040));

            Assert.Equal(new Rect(expectedLeft, expectedTop, 400, 400), result);
        }

        [Fact]
        public void ClampWorksOnAMonitorWithNegativeOrigin()
        {
            // Secondary monitor on the left of and above the primary one.
            Rect workArea = new Rect(-1920, -200, 1920, 1040);

            Assert.Equal(new Rect(-1920, -200, 400, 400), RadialGeometry.ClampToWorkArea(new Point(-1900, -190), 400, workArea));
            Assert.Equal(new Rect(-400, 440, 400, 400), RadialGeometry.ClampToWorkArea(new Point(-10, 830), 400, workArea));
            Assert.Equal(new Rect(-1160, 120, 400, 400), RadialGeometry.ClampToWorkArea(new Point(-960, 320), 400, workArea));
        }

        [Fact]
        public void ClampAlignsAWindowLargerThanTheWorkAreaOnItsTopLeft()
        {
            Rect result = RadialGeometry.ClampToWorkArea(new Point(100, 100), 400, new Rect(-50, -20, 300, 300));

            Assert.Equal(new Rect(-50, -20, 400, 400), result);
        }

        // Hotkey.

        [Theory]
        [InlineData("Ctrl+Alt+C", ModifierKeys.Control | ModifierKeys.Alt, Key.C, "Ctrl+Alt+C")]
        [InlineData("ctrl+alt+c", ModifierKeys.Control | ModifierKeys.Alt, Key.C, "Ctrl+Alt+C")]
        [InlineData("Control + Alt + C", ModifierKeys.Control | ModifierKeys.Alt, Key.C, "Ctrl+Alt+C")]
        [InlineData("C+Alt+Ctrl", ModifierKeys.Control | ModifierKeys.Alt, Key.C, "Ctrl+Alt+C")]
        [InlineData("Win+Shift+Alt+Ctrl+F5", ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows, Key.F5, "Ctrl+Alt+Shift+Win+F5")]
        [InlineData("Windows+Q", ModifierKeys.Windows, Key.Q, "Win+Q")]
        [InlineData("Ctrl+5", ModifierKeys.Control, Key.D5, "Ctrl+5")]
        [InlineData("Alt+Space", ModifierKeys.Alt, Key.Space, "Alt+Space")]
        [InlineData("Ctrl+Shift+Oem3", ModifierKeys.Control | ModifierKeys.Shift, Key.Oem3, "Ctrl+Shift+Oem3")]
        public void HotkeyParses(string text, ModifierKeys modifiers, Key key, string canonical)
        {
            Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
            Assert.Equal(modifiers, hotkey.Modifiers);
            Assert.Equal(key, hotkey.Key);
            Assert.Equal(canonical, hotkey.ToString());

            // The canonical form parses back to the same hotkey.
            Assert.True(Hotkey.TryParse(hotkey.ToString(), out Hotkey roundTrip));
            Assert.Equal(hotkey.Modifiers, roundTrip.Modifiers);
            Assert.Equal(hotkey.Key, roundTrip.Key);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("C")] // no modifier
        [InlineData("Shift+C")] // Shift alone
        [InlineData("Ctrl+Alt")] // no key
        [InlineData("Ctrl+Alt+C+D")] // two keys
        [InlineData("Ctrl+Ctrl+C")] // duplicated modifier
        [InlineData("Ctrl+")]
        [InlineData("+Ctrl+C")]
        [InlineData("Ctrl+Alt+NotAKey")]
        [InlineData("Ctrl+Alt+65")] // numeric enum value
        [InlineData("Ctrl+Alt+None")]
        [InlineData("Ctrl+Alt+LeftCtrl")]
        public void HotkeyRejectsInvalidText(string text)
        {
            Assert.False(Hotkey.TryParse(text, out Hotkey hotkey));
            Assert.Equal(default(ModifierKeys), hotkey.Modifiers);
            Assert.Equal(Key.None, hotkey.Key);
        }

        [Fact]
        public void HotkeyDefaultIsCtrlAltC()
        {
            Assert.Equal("Ctrl+Alt+C", Hotkey.Default.ToString());
            Assert.Equal(Settings.DefaultRadialMenuHotkey, Hotkey.Default.ToString());
            Assert.True(Hotkey.TryParse(Settings.DefaultRadialMenuHotkey, out Hotkey parsed));
            Assert.Equal(Hotkey.Default.Modifiers, parsed.Modifiers);
            Assert.Equal(Hotkey.Default.Key, parsed.Key);
        }

        [Fact]
        public void HotkeyNativeValues()
        {
            Assert.Equal(0x43u, Hotkey.Default.VirtualKey);
            Assert.Equal(1u | 2u, Hotkey.Default.NativeModifiers);

            Assert.True(Hotkey.TryParse("Ctrl+Alt+Shift+Win+F5", out Hotkey hotkey));
            Assert.Equal(1u | 2u | 4u | 8u, hotkey.NativeModifiers);
            Assert.Equal(0x74u, hotkey.VirtualKey);
        }

        [Fact]
        public void HotkeyFromKeyEvent()
        {
            Assert.True(Hotkey.TryFromKeyEvent(ModifierKeys.Control | ModifierKeys.Shift, Key.K, out Hotkey hotkey));
            Assert.Equal("Ctrl+Shift+K", hotkey.ToString());

            Assert.False(Hotkey.TryFromKeyEvent(ModifierKeys.None, Key.K, out _));
            Assert.False(Hotkey.TryFromKeyEvent(ModifierKeys.Shift, Key.K, out _));
            Assert.False(Hotkey.TryFromKeyEvent(ModifierKeys.Control, Key.LeftCtrl, out _));
            Assert.False(Hotkey.TryFromKeyEvent(ModifierKeys.Control, Key.None, out _));
            Assert.False(Hotkey.TryFromKeyEvent(ModifierKeys.Control | ModifierKeys.Alt, Key.System, out _));
        }

        // Agent controller.

        [Theory]
        [InlineData(true, false, RadialAgentController.AgentAction.Start)]
        [InlineData(true, true, RadialAgentController.AgentAction.Reload)]
        [InlineData(false, true, RadialAgentController.AgentAction.Stop)]
        [InlineData(false, false, RadialAgentController.AgentAction.None)]
        public void AgentActionTruthTable(bool shouldRun, bool isRunning, RadialAgentController.AgentAction expected)
        {
            Assert.Equal(expected, RadialAgentController.ComputeAction(shouldRun, isRunning));
        }

        [Fact]
        public void AgentRunCommandQuotesThePath()
        {
            Assert.Equal("\"C:\\Program Files\\FileConverter\\FileConverterPlusPlus.exe\" --agent", RadialAgentNames.BuildRunCommand(@"C:\Program Files\FileConverter\FileConverterPlusPlus.exe"));
        }

        // Settings.

        [Fact]
        public void RadialSettingsDefaultToOff()
        {
            Settings settings = new Settings();

            Assert.False(settings.RadialMenuEnabled);
            Assert.False(settings.RadialMenuExtraContextMenuEntry);
            Assert.False(settings.RadialMenuReplaceContextMenu);
            Assert.False(settings.RadialMenuHotkeyEnabled);
            Assert.False(settings.RadialMenuCtrlDragEnabled);
            Assert.Equal("Ctrl+Alt+C", settings.RadialMenuHotkey);
            Assert.Equal(Helpers.RadialContextMenuMode.None, settings.RadialMenuContextMenuMode);
            Assert.False(settings.ShouldRunRadialAgent);
            Assert.Equal(7, Settings.Version);
        }

        [Fact]
        public void ContextMenuModeFacadeReadsAndWritesBothFlags()
        {
            Settings settings = new Settings();
            List<string> changed = new List<string>();
            settings.PropertyChanged += (sender, args) => changed.Add(args.PropertyName);

            settings.RadialMenuContextMenuMode = Helpers.RadialContextMenuMode.ReplaceSubmenu;
            Assert.True(settings.RadialMenuReplaceContextMenu);
            Assert.False(settings.RadialMenuExtraContextMenuEntry);
            Assert.Contains(nameof(Settings.RadialMenuContextMenuMode), changed);
            Assert.Contains(nameof(Settings.RadialMenuExtraContextMenuEntry), changed);
            Assert.Contains(nameof(Settings.RadialMenuReplaceContextMenu), changed);

            settings.RadialMenuContextMenuMode = Helpers.RadialContextMenuMode.ExtraEntry;
            Assert.False(settings.RadialMenuReplaceContextMenu);
            Assert.True(settings.RadialMenuExtraContextMenuEntry);
            Assert.Equal(Helpers.RadialContextMenuMode.ExtraEntry, settings.RadialMenuContextMenuMode);

            settings.RadialMenuContextMenuMode = Helpers.RadialContextMenuMode.None;
            Assert.False(settings.RadialMenuReplaceContextMenu);
            Assert.False(settings.RadialMenuExtraContextMenuEntry);
        }

        [Fact]
        public void ReplaceWinsOverExtraEntryWhenBothAreSet()
        {
            Settings settings = new Settings { RadialMenuExtraContextMenuEntry = true, RadialMenuReplaceContextMenu = true };

            settings.OnDeserializationComplete();

            Assert.False(settings.RadialMenuExtraContextMenuEntry);
            Assert.True(settings.RadialMenuReplaceContextMenu);
            Assert.Equal(Helpers.RadialContextMenuMode.ReplaceSubmenu, settings.RadialMenuContextMenuMode);
        }

        [Theory]
        [InlineData(null, "Ctrl+Alt+C")]
        [InlineData("", "Ctrl+Alt+C")]
        [InlineData("   ", "Ctrl+Alt+C")]
        [InlineData("Ctrl+Shift+K", "Ctrl+Shift+K")]
        [InlineData("garbage", "garbage")]
        public void EmptyHotkeyFallsBackToDefaultAndInvalidOneIsKept(string stored, string expected)
        {
            Settings settings = new Settings { RadialMenuHotkey = stored };

            settings.OnDeserializationComplete();

            Assert.Equal(expected, settings.RadialMenuHotkey);
        }

        [Theory]
        // master, hotkey, ctrl+drag -> agent needed
        [InlineData(false, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, false, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, true, true)]
        public void AgentIsNeededOnlyForTheHotkeyAndCtrlDragTriggers(bool master, bool hotkey, bool ctrlDrag, bool expected)
        {
            Settings settings = new Settings { RadialMenuEnabled = master, RadialMenuHotkeyEnabled = hotkey, RadialMenuCtrlDragEnabled = ctrlDrag };

            Assert.Equal(expected, settings.ShouldRunRadialAgent);
        }

        [Fact]
        public void MigrationFromVersion6KeepsRadialDefaultsAndPresets()
        {
            Settings settings = new Settings { SerializationVersion = 6 };
            settings.ConversionPresets.Add(CreatePreset("Folder/To Mp3", OutputType.Mp3, "wav", "flac"));
            settings.ConversionPresets.Add(CreatePreset("To Png", OutputType.Png, "jpg"));

            SettingsService.MigrateSettingsToCurrentVersion(settings);

            Assert.False(settings.RadialMenuEnabled);
            Assert.Equal(Helpers.RadialContextMenuMode.None, settings.RadialMenuContextMenuMode);
            Assert.False(settings.RadialMenuHotkeyEnabled);
            Assert.False(settings.RadialMenuCtrlDragEnabled);
            Assert.Equal("Ctrl+Alt+C", settings.RadialMenuHotkey);
            Assert.Equal(new[] { "Folder/To Mp3", "To Png" }, settings.ConversionPresets.Select(preset => preset.FullName));
            Assert.Equal(new[] { "wav", "flac" }, settings.ConversionPresets[0].InputTypes);
        }

        [Fact]
        public void MigrationFromVersion6ResolvesExtraEntryAndReplaceToReplace()
        {
            Settings settings = new Settings { SerializationVersion = 6, RadialMenuExtraContextMenuEntry = true, RadialMenuReplaceContextMenu = true };

            settings.OnDeserializationComplete();
            SettingsService.MigrateSettingsToCurrentVersion(settings);

            Assert.Equal(Helpers.RadialContextMenuMode.ReplaceSubmenu, settings.RadialMenuContextMenuMode);
            Assert.False(settings.RadialMenuExtraContextMenuEntry);
        }

        [Fact]
        public void Version6FileWithoutRadialElementsLoadsWithDefaults()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Settings SerializationVersion=""6"">
  <MaximumNumberOfSimultaneousConversions>4</MaximumNumberOfSimultaneousConversions>
  <CheckUpgradeAtStartup>false</CheckUpgradeAtStartup>
  <ConversionPreset Name=""Folder/To Mp3"" OutputType=""Mp3"">
    <InputTypes>wav</InputTypes>
    <InputTypes>flac</InputTypes>
    <InputPostConversionAction>None</InputPostConversionAction>
  </ConversionPreset>
</Settings>";

            string path = Path.Combine(Path.GetTempPath(), "FileConverter.Tests-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(path, xml);

                XmlHelpers.LoadFromFile<Settings>("Settings", path, out Settings settings);
                SettingsService.MigrateSettingsToCurrentVersion(settings);

                Assert.Equal(6, settings.SerializationVersion);
                Assert.Equal(4, settings.MaximumNumberOfSimultaneousConversions);
                Assert.False(settings.CheckUpgradeAtStartup);
                Assert.False(settings.RadialMenuEnabled);
                Assert.False(settings.RadialMenuExtraContextMenuEntry);
                Assert.False(settings.RadialMenuReplaceContextMenu);
                Assert.False(settings.RadialMenuHotkeyEnabled);
                Assert.False(settings.RadialMenuCtrlDragEnabled);
                Assert.Equal("Ctrl+Alt+C", settings.RadialMenuHotkey);
                Assert.False(settings.ShouldRunRadialAgent);
                ConversionPreset preset = Assert.Single(settings.ConversionPresets);
                Assert.Equal("Folder/To Mp3", preset.FullName);
                Assert.Equal(new[] { "wav", "flac" }, preset.InputTypes);

                // Saving writes the new elements, and loading them back gives the same values.
                settings.SerializationVersion = Settings.Version;
                settings.RadialMenuEnabled = true;
                settings.RadialMenuContextMenuMode = Helpers.RadialContextMenuMode.ExtraEntry;
                settings.RadialMenuHotkeyEnabled = true;
                settings.RadialMenuHotkey = "Ctrl+Shift+K";
                settings.RadialMenuCtrlDragEnabled = true;
                XmlHelpers.SaveToFile("Settings", path, settings);

                string saved = File.ReadAllText(path);
                Assert.Contains("<RadialMenuEnabled>true</RadialMenuEnabled>", saved);
                Assert.DoesNotContain("RadialMenuContextMenuMode", saved);
                Assert.DoesNotContain("ShouldRunRadialAgent", saved);

                XmlHelpers.LoadFromFile<Settings>("Settings", path, out Settings reloaded);
                Assert.Equal(Settings.Version, reloaded.SerializationVersion);
                Assert.True(reloaded.RadialMenuEnabled);
                Assert.Equal(Helpers.RadialContextMenuMode.ExtraEntry, reloaded.RadialMenuContextMenuMode);
                Assert.True(reloaded.RadialMenuHotkeyEnabled);
                Assert.Equal("Ctrl+Shift+K", reloaded.RadialMenuHotkey);
                Assert.True(reloaded.RadialMenuCtrlDragEnabled);
                Assert.True(reloaded.ShouldRunRadialAgent);
                Assert.Single(reloaded.ConversionPresets);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
