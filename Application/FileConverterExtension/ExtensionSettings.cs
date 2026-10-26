// <copyright file="ExtensionSettings.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverterExtension
{
    using System.Xml.Serialization;

    /// <summary>
    /// How the context menu entries are presented in the explorer.
    /// </summary>
    public enum ExtensionMenuMode
    {
        /// <summary>The classic "FileConverter++" cascade only.</summary>
        Classic,

        /// <summary>The classic cascade plus an extra entry opening the radial menu.</summary>
        ClassicWithRadialEntry,

        /// <summary>A single non-cascading entry opening the radial menu.</summary>
        RadialOnly,
    }

    /// <summary>
    /// The subset of the application settings (Settings.user.xml) read by the explorer extension.
    /// Elements absent from the file (older settings versions) are left to their default value.
    /// </summary>
    [XmlRoot("Settings")]
    public class ExtensionSettings
    {
        [XmlElement("ConversionPreset")]
        public PresetReference[] ConversionPresets;

        [XmlElement]
        public bool RadialMenuEnabled;

        [XmlElement]
        public bool RadialMenuExtraContextMenuEntry;

        [XmlElement]
        public bool RadialMenuReplaceContextMenu;

        [XmlElement]
        public string ApplicationLanguageName;

        public static ExtensionMenuMode ResolveMenuMode(bool enabled, bool extraEntry, bool replace)
        {
            if (!enabled)
            {
                return ExtensionMenuMode.Classic;
            }

            if (replace)
            {
                return ExtensionMenuMode.RadialOnly;
            }

            if (extraEntry)
            {
                return ExtensionMenuMode.ClassicWithRadialEntry;
            }

            return ExtensionMenuMode.Classic;
        }

        public ExtensionMenuMode GetMenuMode()
        {
            return ResolveMenuMode(this.RadialMenuEnabled, this.RadialMenuExtraContextMenuEntry, this.RadialMenuReplaceContextMenu);
        }
    }
}
