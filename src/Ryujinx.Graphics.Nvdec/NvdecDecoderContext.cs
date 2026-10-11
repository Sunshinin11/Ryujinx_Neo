using System;

namespace Ryujinx.Graphics.Nvdec
{
    class NvdecDecoderContext : IDisposable
    {
        private FFmpeg.H264.Decoder _h264Decoder;
        private FFmpeg.Vp8.Decoder _vp8Decoder;
        private Vp9FFmpeg.Vp9FFmpegPath _vp9FFmpeg;

        public Vp9FFmpeg.Vp9FFmpegPath Vp9FFmpegDecoder =>
            _vp9FFmpeg ??= new Vp9FFmpeg.Vp9FFmpegPath();

        public FFmpeg.H264.Decoder GetH264Decoder()
        {
            return _h264Decoder ??= new FFmpeg.H264.Decoder();
        }

        public FFmpeg.Vp8.Decoder GetVp8Decoder()
        {
            return _vp8Decoder ??= new FFmpeg.Vp8.Decoder();
        }

        public void Dispose()
        {
            _h264Decoder?.Dispose();
            _h264Decoder = null;

            _vp8Decoder?.Dispose();
            _vp8Decoder = null;

            _vp9FFmpeg?.Dispose();
            _vp9FFmpeg = null;
        }
    }
}
