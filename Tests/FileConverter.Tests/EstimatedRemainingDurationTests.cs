// <copyright file="EstimatedRemainingDurationTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Globalization;

    using FileConverter.ConversionJobs;
    using FileConverter.ValueConverters;

    using Xunit;

    /// <summary>
    /// The "estimated remaining time" text of the conversion window appears once a job has run for 10 s (upstream issue
    /// #803): the converter runs on the UI thread and must never throw, whatever progress ffmpeg reports.
    /// </summary>
    public sealed class EstimatedRemainingDurationTests
    {
        private static string Convert(params object[] values)
        {
            return (string)new ConversionJobToEstimatedRemainingDuration().Convert(values, typeof(string), null, CultureInfo.InvariantCulture);
        }

        private static string Estimate(ConversionState state, double elapsedSeconds, float progress)
        {
            return Convert(state, DateTime.Now - TimeSpan.FromSeconds(elapsedSeconds), progress);
        }

        [Fact]
        public void ShowsNothingBeforeTenSeconds()
        {
            Assert.Equal(string.Empty, Estimate(ConversionState.InProgress, 5, 0.5f));
        }

        [Fact]
        public void EstimatesRemainingTime()
        {
            // 30 s for 25 %: about 90 s left.
            string text = Estimate(ConversionState.InProgress, 30, 0.25f);
            Assert.StartsWith("~0:01:", text);
        }

        [Theory]
        [InlineData(ConversionState.Unknown)]
        [InlineData(ConversionState.Ready)]
        [InlineData(ConversionState.Done)]
        [InlineData(ConversionState.Failed)]
        public void ShowsNothingOutsideOfInProgress(ConversionState state)
        {
            Assert.Equal(string.Empty, Estimate(state, 30, 0.5f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-0.5f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void ShowsNothingForUnusableProgress(float progress)
        {
            Assert.Equal(string.Empty, Estimate(ConversionState.InProgress, 30, progress));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(float.MaxValue)]
        public void NeverShowsNegativeTimeWhenProgressReachesOrPassesOne(float progress)
        {
            string text = Estimate(ConversionState.InProgress, 30, progress);
            Assert.Equal("~0:00:00", text);
        }

        [Fact]
        public void EstimatesJustUnderTheOverflowThreshold()
        {
            // 30 s / 1e-10 = 3e11 s still fits in a TimeSpan.
            string text = Estimate(ConversionState.InProgress, 30, 1e-10f);
            Assert.StartsWith("~", text);
        }

        [Theory]
        [InlineData(float.Epsilon)]
        [InlineData(1e-30f)]
        public void ShowsNothingWhenTheEstimateOverflows(float progress)
        {
            // 30 s / 1e-30 does not fit in a TimeSpan.
            Assert.Equal(string.Empty, Estimate(ConversionState.InProgress, 30, progress));
        }

        [Fact]
        public void HandlesVeryOldStartTime()
        {
            string text = Convert(ConversionState.InProgress, DateTime.MinValue, 0.5f);
            Assert.StartsWith("~", text);
        }

        [Fact]
        public void IgnoresMissingOrUnsetBindingValues()
        {
            DateTime start = DateTime.Now - TimeSpan.FromSeconds(30);
            Assert.Equal(string.Empty, Convert());
            Assert.Equal(string.Empty, Convert(ConversionState.InProgress, start));
            Assert.Equal(string.Empty, Convert(new object(), new object(), new object()));
            Assert.Equal(string.Empty, Convert(ConversionState.InProgress, start, null));
            Assert.Equal(string.Empty, Convert(ConversionState.InProgress, start, 0.5));
            Assert.Equal(string.Empty, (string)new ConversionJobToEstimatedRemainingDuration().Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        }
    }
}
