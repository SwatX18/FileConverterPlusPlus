// <copyright file="ConversionJobToEstimatedRemainingDuration.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.ValueConverters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    using ConversionJobs;

    public class ConversionJobToEstimatedRemainingDuration : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Binding values can be DependencyProperty.UnsetValue (no data context yet): an exception thrown from a
            // converter is not caught by WPF and ends up on the dispatcher.
            if (values == null || values.Length != 3 || !(values[0] is ConversionState) || !(values[1] is DateTime) || !(values[2] is float))
            {
                return string.Empty;
            }

            ConversionState state = (ConversionState)values[0];
            DateTime startTime = (DateTime)values[1];
            float progress = (float)values[2];
            if (!(progress > 0f) || float.IsInfinity(progress))
            {
                // Also catches NaN (every comparison is false).
                return string.Empty;
            }

            if (state == ConversionState.Unknown ||
                state == ConversionState.Ready ||
                state == ConversionState.Failed ||
                state == ConversionState.Done)
            {
                return string.Empty;
            }

            TimeSpan elapsedTime = DateTime.Now - startTime;
            if (elapsedTime.TotalSeconds < 10f)
            {
                return string.Empty;
            }

            // Progress is a ratio of durations reported by ffmpeg and can go past 1 (negative remaining time); a tiny
            // progress would overflow TimeSpan.
            double remainingTimeInSeconds = Math.Max(0.0, (1 - (double)progress) * elapsedTime.TotalSeconds / progress);
            if (remainingTimeInSeconds >= TimeSpan.MaxValue.TotalSeconds)
            {
                return string.Empty;
            }

            TimeSpan remainingTime = TimeSpan.FromSeconds(Math.Floor(remainingTimeInSeconds));
            return "~" + remainingTime.ToString("g");
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
