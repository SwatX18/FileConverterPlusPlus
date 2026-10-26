// <copyright file="Settings.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter
{
    using System.Linq;
    using System.Xml.Serialization;
    using System.Collections.ObjectModel;
    using System.Globalization;

    using CommunityToolkit.Mvvm.ComponentModel;

    [XmlRoot]
    [XmlType]
    public class Settings : ObservableObject, IXmlSerializable
    {
        public const int Version = 7;

        public const string DefaultRadialMenuHotkey = "Ctrl+Alt+C";

        private bool exitApplicationWhenConversionsFinished = false;
        private float durationBetweenEndOfConversionsAndApplicationExit = 3f;
        private ObservableCollection<ConversionPreset> conversionPresets = new ObservableCollection<ConversionPreset>();
        private bool checkUpgradeAtStartup = true;
        private CultureInfo applicationLanguage;
        private int maximumNumberOfSimultaneousConversions;
        private bool copyFilesInClipboardAfterConversion = false;
        private Helpers.HardwareAccelerationMode hardwareAccelerationMode = Helpers.HardwareAccelerationMode.Auto;
        private int maximumNumberOfSimultaneousGpuConversions = Hardware.GpuSessionLimiter.DefaultMaximumSessions;
        private int maximumNumberOfSimultaneousCpuVideoConversions;
        private bool copyStreamsWhenPossible = true;
        private Helpers.ImageEngine imageEngine = Helpers.ImageEngine.Auto;
        private bool radialMenuEnabled = false;
        private bool radialMenuExtraContextMenuEntry = false;
        private bool radialMenuReplaceContextMenu = false;
        private bool radialMenuHotkeyEnabled = false;
        private string radialMenuHotkey = DefaultRadialMenuHotkey;
        private bool radialMenuCtrlDragEnabled = false;

        public ConversionPreset GetPresetFromName(string presetName)
        {
            return this.conversionPresets.FirstOrDefault(match => match.FullName == presetName);
        }

        public void Clean()
        {
            for (int index = 0; index < this.ConversionPresets.Count; index++)
            {
                this.ConversionPresets[index].Clean();
            }
        }
        
        public Settings Merge(Settings settings)
        {
            if (settings == null || settings.conversionPresets == null)
            {
                return this;
            }
            
            for (int index = 0; index < settings.conversionPresets.Count; index++)
            {
                ConversionPreset conversionPreset = settings.conversionPresets[index];
                if (this.conversionPresets.Any(match => match.FullName == conversionPreset.FullName))
                {
                    continue;
                }

                this.conversionPresets.Add(conversionPreset);
            }

            return this;
        }

        [XmlAttribute]
        public int SerializationVersion
        {
            get;
            set;
        } = Version;

        [XmlIgnore]
        public CultureInfo ApplicationLanguage
        {
            get
            {
                return this.applicationLanguage;
            }

            set
            {
                if (this.applicationLanguage != null && this.applicationLanguage.Equals(value))
                {
                    return;
                }

                this.applicationLanguage = value;
                if (this.applicationLanguage != null)
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = this.applicationLanguage;
                    System.Threading.Thread.CurrentThread.CurrentUICulture = this.applicationLanguage;
                }

                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public string ApplicationLanguageName
        {
            get
            {
                if (this.ApplicationLanguage == null)
                {
                    return string.Empty;
                }

                return this.ApplicationLanguage.Name;
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    this.ApplicationLanguage = null;
                    return;
                }

                this.ApplicationLanguage = CultureInfo.GetCultureInfo(value);
            }
        }

        [XmlIgnore]
        public ObservableCollection<ConversionPreset> ConversionPresets
        {
            get
            {
                return this.conversionPresets;
            }

            set
            {
                this.conversionPresets = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public bool ExitApplicationWhenConversionsFinished
        {
            get
            {
                return this.exitApplicationWhenConversionsFinished;
            }

            set
            {
                this.exitApplicationWhenConversionsFinished = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public float DurationBetweenEndOfConversionsAndApplicationExit
        {
            get
            {
                return this.durationBetweenEndOfConversionsAndApplicationExit;
            }

            set
            {
                this.durationBetweenEndOfConversionsAndApplicationExit = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public int MaximumNumberOfSimultaneousConversions
        {
            get
            {
                return this.maximumNumberOfSimultaneousConversions;
            }

            set
            {
                this.maximumNumberOfSimultaneousConversions = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Maximum number of simultaneous hardware (GPU) encodes, on top of <see cref="MaximumNumberOfSimultaneousConversions"/>.
        /// Consumer GPUs limit the number of concurrent encode sessions.
        /// </summary>
        [XmlElement]
        public int MaximumNumberOfSimultaneousGpuConversions
        {
            get
            {
                return this.maximumNumberOfSimultaneousGpuConversions;
            }

            set
            {
                this.maximumNumberOfSimultaneousGpuConversions = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Maximum number of simultaneous software video encodes (x264, x265...), within
        /// <see cref="MaximumNumberOfSimultaneousConversions"/>. 0 means automatic (see <see cref="Services.ConversionLimits"/>).
        /// A missing element (settings from older versions) means automatic.
        /// </summary>
        [XmlElement]
        public int MaximumNumberOfSimultaneousCpuVideoConversions
        {
            get
            {
                return this.maximumNumberOfSimultaneousCpuVideoConversions;
            }

            set
            {
                this.maximumNumberOfSimultaneousCpuVideoConversions = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Copy audio / video streams without re-encoding when the output container accepts them and the preset asks for
        /// no change (see <see cref="Media.RemuxPlanner"/>).
        /// </summary>
        [XmlElement]
        public bool CopyStreamsWhenPossible
        {
            get
            {
                return this.copyStreamsWhenPossible;
            }

            set
            {
                this.copyStreamsWhenPossible = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Image conversion engine: Auto (libvips, then ImageMagick) or ImageMagick only.
        /// </summary>
        [XmlElement]
        public Helpers.ImageEngine ImageEngine
        {
            get
            {
                return this.imageEngine;
            }

            set
            {
                this.imageEngine = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement("ConversionPreset")]
        public ConversionPreset[] SerializableConversionPresets
        {
            get
            {
                return this.ConversionPresets.ToArray();
            }

            set
            {
                for (int index = 0; index < value.Length; index++)
                {
                    this.ConversionPresets.Add(value[index]);
                }
            }
        }

        [XmlElement]
        public bool CheckUpgradeAtStartup
        {
            get
            {
                return this.checkUpgradeAtStartup;
            }

            set
            {
                this.checkUpgradeAtStartup = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public bool CopyFilesInClipboardAfterConversion
        {
            get
            {
                return this.copyFilesInClipboardAfterConversion;
            }

            set
            {
                this.copyFilesInClipboardAfterConversion = value;
                this.OnPropertyChanged();
            }
        }

        [XmlElement]
        public Helpers.HardwareAccelerationMode HardwareAccelerationMode
        {
            get
            {
                return this.hardwareAccelerationMode;
            }

            set
            {
                this.hardwareAccelerationMode = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Master switch of the radial menu. When off, every radial menu trigger is disabled.
        /// </summary>
        [XmlElement]
        public bool RadialMenuEnabled
        {
            get
            {
                return this.radialMenuEnabled;
            }

            set
            {
                this.radialMenuEnabled = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.ShouldRunRadialAgent));
            }
        }

        /// <summary>
        /// Trigger A: extra "radial menu" entry in the Explorer context menu.
        /// </summary>
        [XmlElement]
        public bool RadialMenuExtraContextMenuEntry
        {
            get
            {
                return this.radialMenuExtraContextMenuEntry;
            }

            set
            {
                this.radialMenuExtraContextMenuEntry = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.RadialMenuContextMenuMode));
            }
        }

        /// <summary>
        /// Trigger B: the context menu entry opens the radial menu instead of the preset submenu.
        /// </summary>
        [XmlElement]
        public bool RadialMenuReplaceContextMenu
        {
            get
            {
                return this.radialMenuReplaceContextMenu;
            }

            set
            {
                this.radialMenuReplaceContextMenu = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.RadialMenuContextMenuMode));
            }
        }

        /// <summary>
        /// Trigger C: global hotkey (see <see cref="RadialMenuHotkey"/>), handled by the tray agent.
        /// </summary>
        [XmlElement]
        public bool RadialMenuHotkeyEnabled
        {
            get
            {
                return this.radialMenuHotkeyEnabled;
            }

            set
            {
                this.radialMenuHotkeyEnabled = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.ShouldRunRadialAgent));
            }
        }

        /// <summary>
        /// Hotkey of trigger C, in the canonical form of <see cref="RadialMenu.Hotkey"/> (e.g. "Ctrl+Alt+C").
        /// </summary>
        [XmlElement]
        public string RadialMenuHotkey
        {
            get
            {
                return this.radialMenuHotkey;
            }

            set
            {
                this.radialMenuHotkey = value;
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// Trigger D: Ctrl+drag of files from Explorer, handled by the tray agent.
        /// </summary>
        [XmlElement]
        public bool RadialMenuCtrlDragEnabled
        {
            get
            {
                return this.radialMenuCtrlDragEnabled;
            }

            set
            {
                this.radialMenuCtrlDragEnabled = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.ShouldRunRadialAgent));
            }
        }

        /// <summary>
        /// Exclusive choice between triggers A and B, as a single value for the settings UI.
        /// </summary>
        [XmlIgnore]
        public Helpers.RadialContextMenuMode RadialMenuContextMenuMode
        {
            get
            {
                if (this.radialMenuReplaceContextMenu)
                {
                    return Helpers.RadialContextMenuMode.ReplaceSubmenu;
                }

                return this.radialMenuExtraContextMenuEntry ? Helpers.RadialContextMenuMode.ExtraEntry : Helpers.RadialContextMenuMode.None;
            }

            set
            {
                this.radialMenuExtraContextMenuEntry = value == Helpers.RadialContextMenuMode.ExtraEntry;
                this.radialMenuReplaceContextMenu = value == Helpers.RadialContextMenuMode.ReplaceSubmenu;
                this.OnPropertyChanged(nameof(this.RadialMenuExtraContextMenuEntry));
                this.OnPropertyChanged(nameof(this.RadialMenuReplaceContextMenu));
                this.OnPropertyChanged();
            }
        }

        /// <summary>
        /// The tray agent is only needed by the triggers C and D.
        /// </summary>
        [XmlIgnore]
        public bool ShouldRunRadialAgent
        {
            get
            {
                return this.radialMenuEnabled && (this.radialMenuHotkeyEnabled || this.radialMenuCtrlDragEnabled);
            }
        }

        public void OnDeserializationComplete()
        {
            // A hand-edited file can enable both exclusive context menu triggers: the replacement wins.
            if (this.radialMenuExtraContextMenuEntry && this.radialMenuReplaceContextMenu)
            {
                this.RadialMenuExtraContextMenuEntry = false;
            }

            // An invalid but non-empty hotkey is kept as is: the agent warns and leaves the trigger inactive.
            if (string.IsNullOrWhiteSpace(this.radialMenuHotkey))
            {
                this.RadialMenuHotkey = DefaultRadialMenuHotkey;
            }

            this.DurationBetweenEndOfConversionsAndApplicationExit = System.Math.Max(0, System.Math.Min(10, this.DurationBetweenEndOfConversionsAndApplicationExit));

            for (int index = 0; index < this.ConversionPresets.Count; index++)
            {
                this.ConversionPresets[index].OnDeserializationComplete();
            }

            // Initialize application if it was not deserialized from the settings.
            if (this.ApplicationLanguage == null)
            {
                CultureInfo bestCandidate = null;
                CultureInfo currentUICulture = System.Threading.Thread.CurrentThread.CurrentUICulture;
                foreach (CultureInfo culture in Helpers.GetSupportedCultures())
                {
                    if (culture.Equals(currentUICulture))
                    {
                        bestCandidate = culture;
                        break;
                    }
                    else if (culture.Equals(currentUICulture.Parent))
                    {
                        bestCandidate = culture;
                    }
                }

                if (bestCandidate != null)
                {
                    this.ApplicationLanguage = bestCandidate;
                }
                else
                {
                    Diagnostics.Debug.Log($"Can't find supported culture info for culture {currentUICulture}. Fallback to default culture.");
                    this.ApplicationLanguage = CultureInfo.GetCultureInfo("en");
                }
            }
        }
    }
}
