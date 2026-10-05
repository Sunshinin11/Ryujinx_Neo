using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp9
{
    /// <summary>
    /// NEOV2-VP9EDEN1: VP9 por software do FFmpeg (NEON + threads por tile), como no Eden.
    /// O chamador recompoe os cabecalhos VP9 que o NVDEC nao recebe.
    /// </summary>
    public sealed class Decoder : IDecoder
    {
        private readonly FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_VP9, sliceThreads: true);

        public bool IsHardwareAccelerated => false;

        public bool IsReady => _context.IsReady;

        public ISurface CreateSurface(int width, int height) => new Surface(width, height);

        /// <summary>Retorna o codigo do FFmpeg (&lt; 0 = erro). Quadro oculto: gotFrame = false.</summary>
        public int Decode(ISurface output, ReadOnlySpan<byte> frame, out bool gotFrame)
        {
            return _context.DecodeFrameNoFlush((Surface)output, frame, out gotFrame);
        }

        public void Dispose() => _context.Dispose();
    }
}
