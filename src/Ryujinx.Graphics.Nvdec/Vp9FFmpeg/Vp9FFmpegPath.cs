using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.Image;
using Ryujinx.Graphics.Nvdec.Types.Vp9;
using Ryujinx.Graphics.Nvdec.Vp9;
using Ryujinx.Graphics.Video;
using Ryujinx.Memory;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Ryujinx.Graphics.Nvdec.MemoryExtensions;

namespace Ryujinx.Graphics.Nvdec.Vp9FFmpeg
{
    internal sealed class Vp9FFmpegPath : IDisposable
    {
        private FFmpeg.Vp9.Decoder _decoder;
        private Vp9HeaderComposer _composer;
        private ISurface _decodeSurface;
        private ISurface _lastSurface;
        private bool _hasLastFrame;
        private bool _active;
        private bool _unavailable;

        public bool TryDecode(
            ResourceManager rm,
            ref NvdecRegisters state,
            in PictureInfo pictureInfo)
        {
            bool isKeyFrame =
                pictureInfo.Flags.HasFlag(FrameFlags.IsKeyFrame);

            if (!_active)
            {
                if (_unavailable ||
                    !isKeyFrame ||
                    !Start())
                {
                    return false;
                }
            }

            if (pictureInfo.BitDepth != 0 &&
                pictureInfo.BitDepth != 8)
            {
                Stop(
                    $"profundidade de cor nao suportada ({pictureInfo.BitDepth})");

                return false;
            }

            try
            {
                return DecodeCore(
                    rm,
                    ref state,
                    in pictureInfo);
            }
            catch (Exception ex)
            {
                Stop($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private bool DecodeCore(
            ResourceManager rm,
            ref NvdecRegisters state,
            in PictureInfo pictureInfo)
        {
            EntropyProbs entropy =
                rm.MemoryManager.DeviceRead<EntropyProbs>(
                    state.Vp9SetProbTabBufOffset);

            Vp9FrameContainer incoming = new()
            {
                Picture = pictureInfo,
                Bitstream = rm.MemoryManager.DeviceGetSpan(
                    state.SetInBufBaseOffset,
                    (int)pictureInfo.BitstreamSize).ToArray(),
                ShowFrame = true,
            };

            entropy.Convert(ref incoming.Entropy);

            for (int i = 0; i < 4; i++)
            {
                incoming.Offsets[i] =
                    state.SetPictureLumaOffset[i];
            }

            byte[] packet =
                _composer.ComposeFrame(
                    in incoming,
                    out _,
                    out _);

            int result =
                _decoder.Decode(
                    _decodeSurface,
                    packet,
                    out bool gotFrame);

            if (result < 0)
            {
                Stop($"FFmpeg retornou {result}");
                return false;
            }

            ISurface output = null;

            if (gotFrame)
            {
                (_decodeSurface, _lastSurface) =
                    (_lastSurface, _decodeSurface);

                _hasLastFrame = true;
                output = _lastSurface;
            }
            else if (_hasLastFrame)
            {
                output = _lastSurface;
            }

            if (output != null &&
                output.Width == pictureInfo.CurrentFrameSize.Width &&
                output.Height == pictureInfo.CurrentFrameSize.Height)
            {
                SurfaceWriter.Write(
                    rm.MemoryManager,
                    output,
                    state.SetPictureLumaOffset[3],
                    state.SetPictureChromaOffset[3]);
            }

            int miCols =
                BitUtils.DivRoundUp(
                    pictureInfo.CurrentFrameSize.Width,
                    8);

            int miRows =
                BitUtils.DivRoundUp(
                    pictureInfo.CurrentFrameSize.Height,
                    8);

            using (WritableRegion mvsRegion =
                rm.MemoryManager.GetWritableRegion(
                    ExtendOffset(
                        state.Vp9SetColMvWriteBufOffset),
                    miRows * miCols * 16))
            {
                mvsRegion.Memory.Span.Clear();
            }

            using (WritableRegion countsRegion =
                rm.MemoryManager.GetWritableRegion(
                    ExtendOffset(
                        state.Vp9SetCtxCounterBufOffset),
                    Unsafe.SizeOf<BackwardUpdates>()))
            {
                MemoryMarshal.Cast<byte, BackwardUpdates>(
                    countsRegion.Memory.Span)[0] = default;
            }

            return true;
        }

        private bool Start()
        {
            try
            {
                _decoder ??= new FFmpeg.Vp9.Decoder();

                if (!_decoder.IsReady)
                {
                    _unavailable = true;

                    Logger.Warning?.Print(
                        LogClass.Nvdec,
                        "FFmpeg VP9 indisponivel; usando o decodificador em C#.");

                    return false;
                }

                _composer ??= new Vp9HeaderComposer();
                _composer.Reset();

                _decodeSurface ??=
                    _decoder.CreateSurface(0, 0);

                _lastSurface ??=
                    _decoder.CreateSurface(0, 0);

                _hasLastFrame = false;
                _active = true;

                return true;
            }
            catch (Exception ex)
            {
                _unavailable = true;

                Logger.Warning?.Print(
                    LogClass.Nvdec,
                    $"Falha ao iniciar o FFmpeg VP9 ({ex.GetType().Name}: {ex.Message}); usando o decodificador em C#.");

                return false;
            }
        }

        private void Stop(string reason)
        {
            _active = false;
            _hasLastFrame = false;
            _composer?.Reset();

            _decodeSurface?.Dispose();
            _decodeSurface = null;

            _lastSurface?.Dispose();
            _lastSurface = null;

            _decoder?.Dispose();
            _decoder = null;

            Logger.Warning?.Print(
                LogClass.Nvdec,
                $"Voltando ao decodificador em C# ate o proximo quadro-chave: {reason}");
        }

        public void Dispose()
        {
            _decodeSurface?.Dispose();
            _decodeSurface = null;

            _lastSurface?.Dispose();
            _lastSurface = null;

            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
