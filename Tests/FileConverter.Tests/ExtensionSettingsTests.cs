// <copyright file="ExtensionSettingsTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.IO;

    using FileConverterExtension;

    using Xunit;

    /// <summary>
    /// The settings subset read by the explorer extension: menu mode resolution and tolerant deserialization.
    /// </summary>
    public sealed class ExtensionSettingsTests : IDisposable
    {
        private const string PresetsXml = @"
    <ConversionPreset Name=""Video/To Mp4""><InputTypes>mkv</InputTypes><InputTypes>avi</InputTypes></ConversionPreset>
    <ConversionPreset Name=""To Png""><InputTypes>jpg</InputTypes></ConversionPreset>";

        private readonly string folder = Path.Combine(Path.GetTempPath(), "FileConverter.Tests-" + Guid.NewGuid().ToString("N"));

        public ExtensionSettingsTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.folder, true);
            }
            catch (IOException)
            {
            }
        }

        [Theory]
        [InlineData(false, false, false, ExtensionMenuMode.Classic)]
        [InlineData(false, true, false, ExtensionMenuMode.Classic)]
        [InlineData(false, false, true, ExtensionMenuMode.Classic)]
        [InlineData(false, true, true, ExtensionMenuMode.Classic)]
        [InlineData(true, false, false, ExtensionMenuMode.Classic)]
        [InlineData(true, true, false, ExtensionMenuMode.ClassicWithRadialEntry)]
        [InlineData(true, false, true, ExtensionMenuMode.RadialOnly)]
        [InlineData(true, true, true, ExtensionMenuMode.RadialOnly)]
        public void ResolveMenuMode_TruthTable(bool enabled, bool extraEntry, bool replace, ExtensionMenuMode expected)
        {
            Assert.Equal(expected, ExtensionSettings.ResolveMenuMode(enabled, extraEntry, replace));
        }

        [Fact]
        public void Deserialize_V6File_HasPresetsAndRadialOff()
        {
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Settings><Version>6</Version><ApplicationLanguageName>fr-FR</ApplicationLanguageName>" + PresetsXml + "</Settings>";

            ExtensionSettings settings = this.Load(xml);

            Assert.False(settings.RadialMenuEnabled);
            Assert.False(settings.RadialMenuExtraContextMenuEntry);
            Assert.False(settings.RadialMenuReplaceContextMenu);
            Assert.Equal("fr-FR", settings.ApplicationLanguageName);
            Assert.Equal(ExtensionMenuMode.Classic, settings.GetMenuMode());
            AssertPresets(settings);
        }

        [Fact]
        public void Deserialize_FileWithoutPresets_DoesNotThrow()
        {
            ExtensionSettings settings = this.Load("<Settings><Version>7</Version><RadialMenuEnabled>true</RadialMenuEnabled></Settings>");

            // The extension treats a missing list as empty (it runs inside explorer.exe).
            Assert.Empty(settings.ConversionPresets ?? new PresetReference[0]);
            Assert.Equal(ExtensionMenuMode.Classic, settings.GetMenuMode());
        }

        [Fact]
        public void Deserialize_FileWithoutLanguage_HasNullLanguage()
        {
            ExtensionSettings settings = this.Load("<Settings>" + PresetsXml + "</Settings>");

            Assert.Null(settings.ApplicationLanguageName);
            AssertPresets(settings);
        }

        [Fact]
        public void Deserialize_V7File_ReadsRadialFlags()
        {
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Settings><Version>7</Version><ApplicationLanguageName>en-US</ApplicationLanguageName>"
                + "<RadialMenuEnabled>true</RadialMenuEnabled><RadialMenuExtraContextMenuEntry>true</RadialMenuExtraContextMenuEntry>"
                + "<RadialMenuReplaceContextMenu>false</RadialMenuReplaceContextMenu><RadialMenuHotkeyEnabled>true</RadialMenuHotkeyEnabled>"
                + "<RadialMenuHotkey>Ctrl+Alt+C</RadialMenuHotkey>" + PresetsXml + "</Settings>";

            ExtensionSettings settings = this.Load(xml);

            Assert.True(settings.RadialMenuEnabled);
            Assert.True(settings.RadialMenuExtraContextMenuEntry);
            Assert.False(settings.RadialMenuReplaceContextMenu);
            Assert.Equal("en-US", settings.ApplicationLanguageName);
            Assert.Equal(ExtensionMenuMode.ClassicWithRadialEntry, settings.GetMenuMode());
            AssertPresets(settings);
        }

        private static void AssertPresets(ExtensionSettings settings)
        {
            Assert.NotNull(settings.ConversionPresets);
            Assert.Equal(2, settings.ConversionPresets.Length);
            Assert.Equal("Video/To Mp4", settings.ConversionPresets[0].FullName);
            Assert.Equal("To Mp4", settings.ConversionPresets[0].Name);
            Assert.Equal(new[] { "Video" }, settings.ConversionPresets[0].Folders);
            Assert.Equal(new[] { "mkv", "avi" }, settings.ConversionPresets[0].InputTypes);
            Assert.Equal("To Png", settings.ConversionPresets[1].Name);
        }

        private ExtensionSettings Load(string xml)
        {
            string path = Path.Combine(this.folder, Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, xml);

            XmlHelpers.LoadFromFile("Settings", path, out ExtensionSettings settings);
            return settings;
        }
    }
}
