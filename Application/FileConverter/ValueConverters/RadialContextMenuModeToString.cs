// <copyright file="RadialContextMenuModeToString.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ValueConverters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    public class RadialContextMenuModeToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case Helpers.RadialContextMenuMode.None:
                    return Properties.Resources.RadialContextMenuMode_None;
                case Helpers.RadialContextMenuMode.ExtraEntry:
                    return Properties.Resources.RadialContextMenuMode_ExtraEntry;
                case Helpers.RadialContextMenuMode.ReplaceSubmenu:
                    return Properties.Resources.RadialContextMenuMode_ReplaceSubmenu;
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
