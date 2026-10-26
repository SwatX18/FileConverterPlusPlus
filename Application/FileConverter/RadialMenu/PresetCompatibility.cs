// <copyright file="PresetCompatibility.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>
    /// Selects the presets that can convert a whole selection of files (same rule as the Explorer context menu).
    /// </summary>
    public static class PresetCompatibility
    {
        /// <summary>
        /// Distinct extensions of the files, lower case and without the dot. Files without extension are ignored.
        /// </summary>
        public static HashSet<string> GetExtensions(IEnumerable<string> filePaths)
        {
            HashSet<string> extensions = new HashSet<string>(StringComparer.Ordinal);
            if (filePaths == null)
            {
                return extensions;
            }

            foreach (string filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    continue;
                }

                string extension;
                try
                {
                    extension = Path.GetExtension(filePath);
                }
                catch (ArgumentException)
                {
                    // Path with invalid characters.
                    continue;
                }

                extension = extension?.TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrEmpty(extension))
                {
                    extensions.Add(extension);
                }
            }

            return extensions;
        }

        /// <summary>
        /// Presets (in their original order) whose input types contain the extension of every file. Empty when no file has an extension.
        /// </summary>
        public static List<ConversionPreset> GetPresetsCompatibleWithAll(IEnumerable<ConversionPreset> presets, IEnumerable<string> filePaths)
        {
            List<ConversionPreset> compatiblePresets = new List<ConversionPreset>();
            if (presets == null)
            {
                return compatiblePresets;
            }

            HashSet<string> extensions = GetExtensions(filePaths);
            if (extensions.Count == 0)
            {
                return compatiblePresets;
            }

            foreach (ConversionPreset preset in presets)
            {
                if (preset?.InputTypes == null)
                {
                    continue;
                }

                HashSet<string> inputTypes = new HashSet<string>(
                    preset.InputTypes.Where(inputType => inputType != null).Select(inputType => inputType.ToLowerInvariant()),
                    StringComparer.Ordinal);
                if (extensions.All(inputTypes.Contains))
                {
                    compatiblePresets.Add(preset);
                }
            }

            return compatiblePresets;
        }
    }
}
