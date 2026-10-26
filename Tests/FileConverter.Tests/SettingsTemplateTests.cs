// <copyright file="SettingsTemplateTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Xml.Linq;

    using Xunit;

    /// <summary>
    /// The settings pane of each output type (ConversionPresetTemplates.xaml, OutputTypeSelectorStyle): every output type
    /// that has settings is mapped to a template which binds the settings of that output type.
    /// </summary>
    public class SettingsTemplateTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // Shown by the advanced mode wrapper, not by the per output type templates.
        private static readonly string[] WrapperSettings =
        {
            ConversionPreset.ConversionSettingKeys.EnableFFMPEGCustomCommand,
            ConversionPreset.ConversionSettingKeys.FFMPEGCustomCommand,
        };

        private static readonly Lazy<Mapping> Parsed = new Lazy<Mapping>(Load);

        public static TheoryData<OutputType> AllOutputTypes
        {
            get
            {
                TheoryData<OutputType> data = new TheoryData<OutputType>();
                foreach (OutputType outputType in Enum.GetValues(typeof(OutputType)))
                {
                    if (outputType != OutputType.None)
                    {
                        data.Add(outputType);
                    }
                }

                return data;
            }
        }

        [Theory]
        [MemberData(nameof(AllOutputTypes))]
        public void OutputTypeWithSettingsHasATemplateBindingThem(OutputType outputType)
        {
            ISet<string> presetSettings = GetPresetSettings(outputType);
            Dictionary<string, string> templateByOutputType = Parsed.Value.TemplateByOutputType;
            Dictionary<string, ISet<string>> settingsByTemplate = Parsed.Value.SettingsByTemplate;

            bool hasOwnSettings = presetSettings.Except(WrapperSettings).Any();
            bool hasTemplate = templateByOutputType.TryGetValue(outputType.ToString(), out string templateName);
            if (!hasOwnSettings)
            {
                Assert.False(hasTemplate, $"{outputType} has no settings but is mapped to {templateName}.");
                return;
            }

            Assert.True(hasTemplate, $"{outputType} has settings but no template in OutputTypeSelectorStyle (it shows an empty pane).");

            ISet<string> bound = settingsByTemplate[templateName];
            Assert.NotEmpty(bound);
            Assert.True(
                bound.IsSubsetOf(presetSettings),
                $"{outputType} uses {templateName}, which binds {string.Join(", ", bound.Except(presetSettings))}: not settings of {outputType}.");
        }

        [Fact]
        public void OutputFormatListHasEveryOutputType()
        {
            foreach (OutputType outputType in Enum.GetValues(typeof(OutputType)))
            {
                if (outputType != OutputType.None)
                {
                    Assert.Contains(outputType, ViewModels.SettingsViewModel.SettingsOutputTypes);
                }
            }

            Assert.Equal(ViewModels.SettingsViewModel.SettingsOutputTypes.Count, ViewModels.SettingsViewModel.SettingsOutputTypes.Distinct().Count());
        }

        [Fact]
        public void EveryTriggerIsAnOutputType()
        {
            Dictionary<string, string> templateByOutputType = Parsed.Value.TemplateByOutputType;
            foreach (string name in templateByOutputType.Keys)
            {
                Assert.True(Enum.IsDefined(typeof(OutputType), name), $"'{name}' is not an OutputType.");
            }
        }

        [Fact]
        public void ImageOutputsWithAQualityShowTheQualitySlider()
        {
            Dictionary<string, string> templateByOutputType = Parsed.Value.TemplateByOutputType;
            Dictionary<string, ISet<string>> settingsByTemplate = Parsed.Value.SettingsByTemplate;
            foreach (OutputType outputType in new[] { OutputType.Avif, OutputType.Jpg, OutputType.Webp })
            {
                string templateName = templateByOutputType[outputType.ToString()];
                Assert.Contains(ConversionPreset.ConversionSettingKeys.ImageQuality, settingsByTemplate[templateName]);
            }
        }

        private static ISet<string> GetPresetSettings(OutputType outputType)
        {
            ConversionPreset preset = new ConversionPreset("test", outputType);
            return new HashSet<string>(preset.Settings.Select(setting => setting.Key));
        }

        private static Mapping Load()
        {
            XDocument document = XDocument.Load(FindTemplatesFile());

            Dictionary<string, ISet<string>> settingsByTemplate = new Dictionary<string, ISet<string>>();
            foreach (XElement template in document.Descendants(Presentation + "DataTemplate"))
            {
                string key = (string)template.Attribute(Xaml + "Key");
                if (key == null)
                {
                    continue;
                }

                settingsByTemplate[key] = new HashSet<string>(
                    template.DescendantsAndSelf()
                            .SelectMany(element => element.Attributes())
                            .SelectMany(attribute => Regex.Matches(attribute.Value, @"ConverterParameter=(\w+)\|").Cast<Match>())
                            .Select(match => match.Groups[1].Value));
            }

            XElement style = document.Descendants(Presentation + "Style").Single(element => (string)element.Attribute(Xaml + "Key") == "OutputTypeSelectorStyle");

            Dictionary<string, string> templateByOutputType = new Dictionary<string, string>();
            foreach (XElement trigger in style.Descendants(Presentation + "DataTrigger"))
            {
                string value = (string)trigger.Attribute("Value");
                string setter = (string)trigger.Element(Presentation + "Setter").Attribute("Value");
                string templateName = Regex.Match(setter, @"StaticResource\s+(\w+)").Groups[1].Value;

                Assert.False(templateByOutputType.ContainsKey(value), $"Two triggers for {value}.");
                Assert.True(settingsByTemplate.ContainsKey(templateName), $"{value} uses the unknown template '{templateName}'.");
                templateByOutputType[value] = templateName;
            }

            return new Mapping { TemplateByOutputType = templateByOutputType, SettingsByTemplate = settingsByTemplate };
        }

        private static string FindTemplatesFile()
        {
            string relativePath = Path.Combine("Application", "FileConverter", "Views", "Resources", "ConversionPresetTemplates.xaml");
            for (DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException(relativePath);
        }

        private sealed class Mapping
        {
            /// <summary>Gets OutputType name -> template key (OutputTypeSelectorStyle).</summary>
            public Dictionary<string, string> TemplateByOutputType { get; set; }

            /// <summary>Gets template key -> the settings its bindings use.</summary>
            public Dictionary<string, ISet<string>> SettingsByTemplate { get; set; }
        }
    }
}
