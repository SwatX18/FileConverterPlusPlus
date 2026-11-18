// <copyright file="OutputValidationTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using FileConverter.ConversionJobs;
    using FileConverter.Media;

    using Xunit;

    /// <summary>
    /// A DVD menu / navigation VOB (VIDEO_TS.VOB, VTS_xx_0.VOB) makes ffmpeg exit with code 0 and write a tiny, stream-poor MP4
    /// (observed: 0.07 to 0.43 s of video, no audio). Such an output must not be reported as a successful conversion.
    /// </summary>
    public class OutputValidationTests
    {
        private const string Mp4 = "mov,mp4,m4a,3gp,3g2,mj2";

        private static MediaInfo Probe(string streams, string duration)
        {
            string durationJson = duration == null ? string.Empty : $", \"duration\": \"{duration}\"";
            return MediaProbe.Parse($"{{ \"streams\": [ {streams} ], \"format\": {{ \"format_name\": \"{Mp4}\"{durationJson} }} }}");
        }

        private const string Video = "{ \"index\": 0, \"codec_type\": \"video\", \"codec_name\": \"h264\", \"pix_fmt\": \"yuv420p\", \"width\": 720, \"height\": 576, \"duration\": \"6.000000\" }";
        private const string Audio = "{ \"index\": 1, \"codec_type\": \"audio\", \"codec_name\": \"aac\", \"sample_rate\": \"48000\", \"channels\": 2, \"duration\": \"6.025011\" }";

        [Fact]
        public void ARealTitleWithVideoAndAudioIsValid()
        {
            Assert.Null(ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video + "," + Audio, "6.025011"), expectVideo: true, expectAudio: true));
        }

        [Fact]
        public void MenuVobFragmentWithoutAudioIsRejected()
        {
            // What the DVD menu VOBs of a real disc gave: video only, 0.43 s.
            string error = ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video, "0.433767"), expectVideo: true, expectAudio: true);

            Assert.NotNull(error);
            Assert.Contains("no audio stream", error);
        }

        [Fact]
        public void VideoOnlyOutputIsValidWhenAudioWasNotRequested()
        {
            Assert.Null(ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video, "6.0"), expectVideo: true, expectAudio: false));
        }

        [Fact]
        public void OutputWithoutAnyStreamIsRejected()
        {
            string error = ConversionJob_FFMPEG.ValidateProbedOutput(Probe(string.Empty, "0.000000"), expectVideo: true, expectAudio: true);

            Assert.NotNull(error);
            Assert.Contains("no video stream", error);
        }

        [Fact]
        public void AudioExtractionWithoutAudioStreamIsRejected()
        {
            string error = ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video, "6.0"), expectVideo: false, expectAudio: true);

            Assert.NotNull(error);
            Assert.Contains("no audio stream", error);
        }

        [Fact]
        public void OutputWithoutDurationIsRejected()
        {
            Assert.NotNull(ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video + "," + Audio, null), expectVideo: true, expectAudio: true));
            Assert.NotNull(ConversionJob_FFMPEG.ValidateProbedOutput(Probe(Video + "," + Audio, "0.000000"), expectVideo: true, expectAudio: true));
        }

        [Fact]
        public void UnreadableOutputIsRejected()
        {
            Assert.NotNull(ConversionJob_FFMPEG.ValidateProbedOutput(null, expectVideo: true, expectAudio: true));
        }
    }
}
