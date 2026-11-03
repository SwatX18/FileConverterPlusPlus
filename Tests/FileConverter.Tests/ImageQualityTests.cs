// <copyright file="ImageQualityTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;

    using FileConverter.ConversionJobs;
    using FileConverter.Imaging;

    using Xunit;

    /// <summary>
    /// The quality setting of the lossy image outputs changes the written file.
    /// </summary>
    public sealed class ImageQualityTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "FileConverter.Tests-" + Guid.NewGuid().ToString("N"));

        public ImageQualityTests()
        {
            TestServices.Initialize();
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, recursive: true);
        }

        [Theory]
        [InlineData(OutputType.Avif)]
        [InlineData(OutputType.Jpg)]
        [InlineData(OutputType.Webp)]
        public void HigherQualityGivesALargerFile(OutputType outputType)
        {
            // Converted by libvips (see ConversionJob_Vips).
            Assert.True(VipsEngine.IsAvailable, $"libvips is not loaded: {VipsEngine.UnavailableReason}");
            string input = this.CreateNoisyPng();

            long low = this.Convert(input, outputType, "10");
            long high = this.Convert(input, outputType, "90");

            Assert.True(low > 0, "The low quality output is empty.");
            Assert.True(high > low, $"Quality 90 gave {high} bytes, quality 10 gave {low} bytes.");
        }

        private long Convert(string input, OutputType outputType, string quality)
        {
            ConversionPreset preset = new ConversionPreset("test", outputType);
            preset.Settings = new ConversionSettings { { ConversionPreset.ConversionSettingKeys.ImageQuality, quality } };

            string output = Path.Combine(this.folder, $"out-{quality}.{outputType.ToString().ToLowerInvariant()}");
            ConversionJob job = ConversionJobFactory.Create(preset, input);
            job.PrepareConversion(output);
            job.StartConversion();

            Assert.Equal(ConversionState.Done, job.State);
            return new FileInfo(output).Length;
        }

        private string CreateNoisyPng()
        {
            string path = Path.Combine(this.folder, "noise.png");
            Random random = new Random(1);
            using (Bitmap bitmap = new Bitmap(128, 128, PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                    }
                }

                bitmap.Save(path, ImageFormat.Png);
            }

            return path;
        }
    }
}
