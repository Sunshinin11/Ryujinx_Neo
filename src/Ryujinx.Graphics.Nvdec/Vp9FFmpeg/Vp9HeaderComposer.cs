// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2020 yuzu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Nvdec.Types.Vp9;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.Vp9FFmpeg
{
    internal struct Vp9FrameContainer
    {
        public PictureInfo Picture;
        public Vp9EntropyProbs Entropy;
        public Array4<uint> Offsets;
        public byte[] Bitstream;
        public bool ShowFrame;

        public readonly bool IsKeyFrame =>
            Picture.Flags.HasFlag(FrameFlags.IsKeyFrame);
    }

    internal sealed class Vp9HeaderComposer
    {
        private const int DiffUpdateProbability = 252;
        private const uint FrameSyncCode = 0x498342;

        private static readonly Vp9EntropyProbs _defaultProbs =
            Ryujinx.Graphics.Nvdec.Vp9.Vp9DefaultProbabilities.Create();

        private readonly Vp9EntropyProbs[] _frameCtxs =
            new Vp9EntropyProbs[4];

        private Vp9EntropyProbs _prevFrameProbs;
        private bool _swapRefIndices;

        private Vp9FrameContainer _next;
        private bool _hasNext;

        private bool _currentIntraOnly;

        public Vp9HeaderComposer()
        {
            Reset();
        }

        public void Reset()
        {
            _next = default;
            _hasNext = false;
            _swapRefIndices = false;
            _prevFrameProbs = _defaultProbs;

            for (int i = 0; i < _frameCtxs.Length; i++)
            {
                _frameCtxs[i] = _defaultProbs;
            }
        }

        public byte[] ComposeFrame(
            in Vp9FrameContainer incoming,
            out bool showFrame,
            out bool isKeyFrame)
        {
            Vp9FrameContainer current =
                GetCurrentFrame(incoming);

            showFrame = current.ShowFrame;
            isKeyFrame = current.IsKeyFrame;

            VpxBitStreamWriter uncompWriter =
                ComposeUncompressedHeader(ref current);

            byte[] compressedHeader =
                ComposeCompressedHeader(ref current);

            uncompWriter.WriteU(
                (uint)compressedHeader.Length,
                16);

            uncompWriter.Flush();

            byte[] uncompressedHeader =
                uncompWriter.ToArray();

            byte[] frame =
                new byte[
                    uncompressedHeader.Length +
                    compressedHeader.Length +
                    current.Bitstream.Length];

            uncompressedHeader.CopyTo(frame, 0);

            compressedHeader.CopyTo(
                frame,
                uncompressedHeader.Length);

            current.Bitstream.CopyTo(
                frame,
                uncompressedHeader.Length +
                compressedHeader.Length);

            return frame;
        }

        private Vp9FrameContainer GetCurrentFrame(
            in Vp9FrameContainer incoming)
        {
            Vp9FrameContainer current;

            if (_hasNext)
            {
                current = _next;

                current.ShowFrame =
                    incoming.Picture.Flags.HasFlag(
                        FrameFlags.LastShowFrame);

                _next = incoming;
            }
            else
            {
                current = incoming;
                _next = incoming;
                _hasNext = true;
            }

            return current;
        }

        private VpxBitStreamWriter ComposeUncompressedHeader(
            ref Vp9FrameContainer current)
        {
            ref PictureInfo pic = ref current.Picture;

            bool isKeyFrame = current.IsKeyFrame;
            bool errorResilient =
                pic.Flags.HasFlag(
                    FrameFlags.ErrorResilientMode);

            bool intraOnly =
                pic.Flags.HasFlag(
                    FrameFlags.IntraOnly);

            bool showFrame = current.ShowFrame;

            VpxBitStreamWriter w = new();

            w.WriteU(2, 2);
            w.WriteU(0, 2);
            w.WriteBit(false);
            w.WriteBit(!isKeyFrame);
            w.WriteBit(showFrame);
            w.WriteBit(errorResilient);

            if (isKeyFrame)
            {
                w.WriteU(FrameSyncCode, 24);
                w.WriteU(0, 3);
                w.WriteU(0, 1);

                WriteFrameSize(
                    w,
                    pic.CurrentFrameSize);

                w.WriteBit(false);

                _swapRefIndices = false;
                ResetFrameContexts();

                intraOnly = true;
            }
            else
            {
                if (!showFrame)
                {
                    w.WriteBit(intraOnly);
                }
                else
                {
                    intraOnly = false;
                }

                if (!errorResilient)
                {
                    w.WriteU(0, 2);
                }

                ref Array4<uint> currOffsets =
                    ref current.Offsets;

                ref Array4<uint> nextOffsets =
                    ref _next.Offsets;

                bool refFramesDifferent =
                    currOffsets[1] != currOffsets[2];

                bool nextReferencesSwap =
                    nextOffsets[1] == currOffsets[2] ||
                    nextOffsets[2] == currOffsets[1];

                bool needsRefSwap =
                    refFramesDifferent &&
                    nextReferencesSwap;

                if (needsRefSwap)
                {
                    _swapRefIndices =
                        !_swapRefIndices;
                }

                uint refreshFrameFlags = 0;

                for (int index = 0; index < 3; index++)
                {
                    if (currOffsets[3] ==
                        nextOffsets[index])
                    {
                        refreshFrameFlags |=
                            1u << index;
                    }
                }

                if (_swapRefIndices)
                {
                    uint golden =
                        (refreshFrameFlags >> 1) & 1;

                    uint alt =
                        (refreshFrameFlags >> 2) & 1;

                    refreshFrameFlags =
                        (refreshFrameFlags & ~6u) |
                        (alt << 1) |
                        (golden << 2);
                }

                if (intraOnly)
                {
                    w.WriteU(FrameSyncCode, 24);
                    w.WriteU(refreshFrameFlags, 8);

                    WriteFrameSize(
                        w,
                        pic.CurrentFrameSize);

                    w.WriteBit(false);
                }
                else
                {
                    bool swapIndices =
                        needsRefSwap ^
                        _swapRefIndices;

                    uint goldenSlot =
                        swapIndices ? 2u : 1u;

                    uint altSlot =
                        swapIndices ? 1u : 2u;

                    w.WriteU(refreshFrameFlags, 8);

                    for (int index = 1;
                         index < 4;
                         index++)
                    {
                        uint slot =
                            index == 1
                                ? 0u
                                : (index == 2
                                    ? goldenSlot
                                    : altSlot);

                        w.WriteU(slot, 3);

                        w.WriteU(
                            (uint)(
                                pic.RefFrameSignBias[index] &
                                1),
                            1);
                    }

                    w.WriteBit(false);
                    w.WriteBit(false);
                    w.WriteBit(false);

                    WriteFrameSize(
                        w,
                        pic.CurrentFrameSize);

                    w.WriteBit(false);

                    w.WriteBit(
                        pic.AllowHighPrecisionMv != 0);

                    w.WriteBit(
                        pic.InterpFilter == 4);

                    if (pic.InterpFilter != 4)
                    {
                        w.WriteU(
                            pic.InterpFilter,
                            2);
                    }
                }
            }

            if (!errorResilient)
            {
                w.WriteBit(true);
                w.WriteBit(true);
            }

            int frameCtxIdx =
                showFrame ? 0 : 1;

            w.WriteU(
                (uint)frameCtxIdx,
                2);

            if (errorResilient)
            {
                ResetFrameContexts();
                _prevFrameProbs = _defaultProbs;
            }
            else
            {
                _prevFrameProbs =
                    _frameCtxs[frameCtxIdx];

                _frameCtxs[frameCtxIdx] =
                    current.Entropy;
            }

            w.WriteU(pic.FirstLevel, 6);
            w.WriteU(pic.SharpnessLevel, 3);

            bool modeRefDeltaEnabled =
                pic.Lf.ModeRefDeltaEnabled != 0;

            w.WriteBit(modeRefDeltaEnabled);

            if (modeRefDeltaEnabled)
            {
                w.WriteBit(true);

                for (int index = 0;
                     index < 4;
                     index++)
                {
                    w.WriteBit(true);

                    w.WriteS(
                        pic.Lf.RefDeltas[index],
                        6);
                }

                for (int index = 0;
                     index < 2;
                     index++)
                {
                    w.WriteBit(true);

                    w.WriteS(
                        pic.Lf.ModeDeltas[index],
                        6);
                }
            }

            w.WriteU(pic.BaseQIndex, 8);

            w.WriteDeltaQ(
                (sbyte)pic.YDcDeltaQ);

            w.WriteDeltaQ(
                (sbyte)pic.UvDcDeltaQ);

            w.WriteDeltaQ(
                (sbyte)pic.UvAcDeltaQ);

            WriteSegmentation(
                w,
                ref current);

            int width =
                pic.CurrentFrameSize.Width;

            int minTileColsLog2 =
                CalcMinLog2TileCols(width);

            int maxTileColsLog2 =
                CalcMaxLog2TileCols(width);

            int tileColsLog2Diff =
                pic.Log2TileCols -
                minTileColsLog2;

            uint tileColsLog2IncMask =
                (1u << tileColsLog2Diff) - 1;

            if (pic.Log2TileCols <
                maxTileColsLog2)
            {
                w.WriteU(
                    tileColsLog2IncMask << 1,
                    tileColsLog2Diff + 1);
            }
            else
            {
                w.WriteU(
                    tileColsLog2IncMask,
                    tileColsLog2Diff);
            }

            bool tileRowsLog2IsNonZero =
                pic.Log2TileRows != 0;

            w.WriteBit(
                tileRowsLog2IsNonZero);

            if (tileRowsLog2IsNonZero)
            {
                w.WriteBit(
                    pic.Log2TileRows > 1);
            }

            _currentIntraOnly = intraOnly;

            return w;
        }

        private void ResetFrameContexts()
        {
            _prevFrameProbs =
                _defaultProbs;

            for (int i = 0;
                 i < _frameCtxs.Length;
                 i++)
            {
                _frameCtxs[i] =
                    _defaultProbs;
            }
        }

        private static void WriteFrameSize(
            VpxBitStreamWriter w,
            FrameSize size)
        {
            w.WriteU(
                (uint)(size.Width - 1),
                16);

            w.WriteU(
                (uint)(size.Height - 1),
                16);
        }

        private static void WriteSegmentation(
            VpxBitStreamWriter w,
            ref Vp9FrameContainer current)
        {
            ref Segmentation seg =
                ref current.Picture.Seg;

            bool enabled =
                seg.Enabled != 0;

            w.WriteBit(enabled);

            if (!enabled)
            {
                return;
            }

            bool updateMap =
                seg.UpdateMap != 0;

            w.WriteBit(updateMap);

            if (updateMap)
            {
                for (int i = 0; i < 7; i++)
                {
                    WriteOptionalProbability(
                        w,
                        current.Entropy.SegTreeProb[i]);
                }

                bool temporalUpdate =
                    seg.TemporalUpdate != 0;

                w.WriteBit(temporalUpdate);

                if (temporalUpdate)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        WriteOptionalProbability(
                            w,
                            current.Entropy.SegPredProb[i]);
                    }
                }
            }

            w.WriteBit(true);
            w.WriteBit(seg.AbsDelta != 0);

            ReadOnlySpan<int> featureBits =
                [8, 6, 2, 0];

            for (int i = 0; i < 8; i++)
            {
                uint mask =
                    seg.FeatureMask[i];

                for (int j = 0; j < 4; j++)
                {
                    bool featureEnabled =
                        ((mask >> (j * 8)) &
                         0xff) != 0;

                    w.WriteBit(featureEnabled);

                    if (!featureEnabled ||
                        featureBits[j] == 0)
                    {
                        continue;
                    }

                    int data =
                        seg.FeatureData[i][j];

                    if (j == 2)
                    {
                        w.WriteU(
                            (uint)data,
                            featureBits[j]);
                    }
                    else
                    {
                        w.WriteS(
                            data,
                            featureBits[j]);
                    }
                }
            }
        }

        private static void WriteOptionalProbability(
            VpxBitStreamWriter w,
            byte probability)
        {
            bool coded =
                probability != 255;

            w.WriteBit(coded);

            if (coded)
            {
                w.WriteU(
                    probability,
                    8);
            }
        }

        private byte[] ComposeCompressedHeader(
            ref Vp9FrameContainer current)
        {
            ref PictureInfo pic =
                ref current.Picture;

            ref Vp9EntropyProbs probs =
                ref current.Entropy;

            ref Vp9EntropyProbs prev =
                ref _prevFrameProbs;

            VpxRangeEncoder writer = new();

            int transformMode =
                pic.Lossless != 0
                    ? 0
                    : pic.TxMode;

            if (pic.Lossless == 0)
            {
                if (transformMode >= 3)
                {
                    writer.Write(3, 2);
                    writer.Write(
                        transformMode == 4);
                }
                else
                {
                    writer.Write(
                        transformMode,
                        2);
                }
            }

            if (transformMode == 4)
            {
                for (int i = 0; i < 2; i++)
                {
                    WriteProbabilityUpdate(
                        writer,
                        probs.Tx8x8Prob[i][0],
                        prev.Tx8x8Prob[i][0]);
                }

                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.Tx16x16Prob[i][j],
                            prev.Tx16x16Prob[i][j]);
                    }
                }

                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.Tx32x32Prob[i][j],
                            prev.Tx32x32Prob[i][j]);
                    }
                }
            }

            WriteCoefProbabilityUpdate(
                writer,
                transformMode,
                ref probs,
                ref prev);

            for (int i = 0; i < 3; i++)
            {
                WriteProbabilityUpdate(
                    writer,
                    probs.SkipProb[i],
                    prev.SkipProb[i]);
            }

            if (!_currentIntraOnly)
            {
                for (int i = 0; i < 7; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.InterModeProb[i][j],
                            prev.InterModeProb[i][j]);
                    }
                }

                if (pic.InterpFilter == 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            WriteProbabilityUpdate(
                                writer,
                                probs.SwitchableInterpProb[i][j],
                                prev.SwitchableInterpProb[i][j]);
                        }
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    WriteProbabilityUpdate(
                        writer,
                        probs.IntraInterProb[i],
                        prev.IntraInterProb[i]);
                }

                if ((pic.RefFrameSignBias[1] & 1) !=
                        (pic.RefFrameSignBias[2] & 1) ||
                    (pic.RefFrameSignBias[1] & 1) !=
                        (pic.RefFrameSignBias[3] & 1))
                {
                    if (pic.ReferenceMode >= 1)
                    {
                        writer.Write(1, 1);

                        writer.Write(
                            pic.ReferenceMode == 2);
                    }
                    else
                    {
                        writer.Write(0, 1);
                    }
                }

                if (pic.ReferenceMode == 2)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.CompInterProb[i],
                            prev.CompInterProb[i]);
                    }
                }

                if (pic.ReferenceMode != 1)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            WriteProbabilityUpdate(
                                writer,
                                probs.SingleRefProb[i][j],
                                prev.SingleRefProb[i][j]);
                        }
                    }
                }

                if (pic.ReferenceMode != 0)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.CompRefProb[i],
                            prev.CompRefProb[i]);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 9; j++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.YModeProb[i][j],
                            prev.YModeProb[i][j]);
                    }
                }

                for (int i = 0; i < 16; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        WriteProbabilityUpdate(
                            writer,
                            probs.PartitionProb[i][j],
                            prev.PartitionProb[i][j]);
                    }
                }

                for (int i = 0; i < 3; i++)
                {
                    WriteMvProbabilityUpdate(
                        writer,
                        probs.Joints[i],
                        prev.Joints[i]);
                }

                for (int i = 0; i < 2; i++)
                {
                    WriteMvProbabilityUpdate(
                        writer,
                        probs.Sign[i],
                        prev.Sign[i]);

                    for (int j = 0; j < 10; j++)
                    {
                        WriteMvProbabilityUpdate(
                            writer,
                            probs.Classes[i][j],
                            prev.Classes[i][j]);
                    }

                    WriteMvProbabilityUpdate(
                        writer,
                        probs.Class0[i][0],
                        prev.Class0[i][0]);

                    for (int j = 0; j < 10; j++)
                    {
                        WriteMvProbabilityUpdate(
                            writer,
                            probs.Bits[i][j],
                            prev.Bits[i][j]);
                    }
                }

                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            WriteMvProbabilityUpdate(
                                writer,
                                probs.Class0Fp[i][j][k],
                                prev.Class0Fp[i][j][k]);
                        }
                    }

                    for (int j = 0; j < 3; j++)
                    {
                        WriteMvProbabilityUpdate(
                            writer,
                            probs.Fp[i][j],
                            prev.Fp[i][j]);
                    }
                }

                if (pic.AllowHighPrecisionMv != 0)
                {
                    for (int index = 0;
                         index < 2;
                         index++)
                    {
                        WriteMvProbabilityUpdate(
                            writer,
                            probs.Class0Hp[index],
                            prev.Class0Hp[index]);

                        WriteMvProbabilityUpdate(
                            writer,
                            probs.Hp[index],
                            prev.Hp[index]);
                    }
                }
            }

            writer.End();

            return writer.ToArray();
        }

        private static void WriteCoefProbabilityUpdate(
            VpxRangeEncoder writer,
            int txMode,
            ref Vp9EntropyProbs probs,
            ref Vp9EntropyProbs prev)
        {
            for (int blockIndex = 0;
                 blockIndex < 4;
                 blockIndex++)
            {
                bool update = false;

                for (int i = 0;
                     i < 2 && !update;
                     i++)
                {
                    for (int j = 0;
                         j < 2 && !update;
                         j++)
                    {
                        for (int k = 0;
                             k < 6 && !update;
                             k++)
                        {
                            for (int l = 0;
                                 l < 6 && !update;
                                 l++)
                            {
                                if (k == 0 &&
                                    l >= 3)
                                {
                                    continue;
                                }

                                for (int m = 0;
                                     m < 3;
                                     m++)
                                {
                                    if (probs.CoefProbs[
                                            blockIndex][i][j][k][l][m] !=
                                        prev.CoefProbs[
                                            blockIndex][i][j][k][l][m])
                                    {
                                        update = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                writer.Write(update);

                if (update)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            for (int k = 0; k < 6; k++)
                            {
                                for (int l = 0; l < 6; l++)
                                {
                                    if (k != 0 || l < 3)
                                    {
                                        for (int m = 0;
                                             m < 3;
                                             m++)
                                        {
                                            WriteProbabilityUpdate(
                                                writer,
                                                probs.CoefProbs[
                                                    blockIndex][i][j][k][l][m],
                                                prev.CoefProbs[
                                                    blockIndex][i][j][k][l][m]);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (blockIndex == txMode)
                {
                    break;
                }
            }
        }

        private static void WriteProbabilityUpdate(
            VpxRangeEncoder writer,
            byte newProb,
            byte oldProb)
        {
            bool update =
                newProb != oldProb;

            writer.Write(
                update,
                DiffUpdateProbability);

            if (update)
            {
                WriteProbabilityDelta(
                    writer,
                    newProb,
                    oldProb);
            }
        }

        private static void WriteProbabilityDelta(
            VpxRangeEncoder writer,
            byte newProb,
            byte oldProb)
        {
            int delta =
                RemapProbability(
                    newProb,
                    oldProb);

            EncodeTermSubExp(
                writer,
                delta);
        }

        private static void EncodeTermSubExp(
            VpxRangeEncoder writer,
            int value)
        {
            if (WriteLessThan(
                writer,
                value,
                16))
            {
                writer.Write(value, 4);
            }
            else if (WriteLessThan(
                writer,
                value,
                32))
            {
                writer.Write(value - 16, 4);
            }
            else if (WriteLessThan(
                writer,
                value,
                64))
            {
                writer.Write(value - 32, 5);
            }
            else
            {
                value -= 64;

                const int Size = 8;
                const int Mask =
                    (1 << Size) - 191;

                int delta =
                    value - Mask;

                if (delta < 0)
                {
                    writer.Write(
                        value,
                        Size - 1);
                }
                else
                {
                    writer.Write(
                        (delta / 2) + Mask,
                        Size - 1);

                    writer.Write(
                        delta & 1,
                        1);
                }
            }
        }

        private static bool WriteLessThan(
            VpxRangeEncoder writer,
            int value,
            int test)
        {
            bool isLessThan =
                value < test;

            writer.Write(!isLessThan);

            return isLessThan;
        }

        private static void WriteMvProbabilityUpdate(
            VpxRangeEncoder writer,
            byte newProb,
            byte oldProb)
        {
            bool update =
                newProb != oldProb;

            writer.Write(
                update,
                DiffUpdateProbability);

            if (update)
            {
                writer.Write(
                    newProb >> 1,
                    7);
            }
        }

        private static int RecenterNonNeg(
            int newProb,
            int oldProb)
        {
            if (newProb > oldProb * 2)
            {
                return newProb;
            }

            if (newProb >= oldProb)
            {
                return
                    (newProb - oldProb) * 2;
            }

            return
                ((oldProb - newProb) * 2) - 1;
        }

        private static int RemapProbability(
            int newProb,
            int oldProb)
        {
            newProb--;
            oldProb--;

            byte i =
                oldProb * 2 <= 0xff
                    ? (byte)Math.Max(
                        0,
                        RecenterNonNeg(
                            newProb,
                            oldProb) - 1)
                    : (byte)Math.Max(
                        0,
                        RecenterNonNeg(
                            0xff - 1 - newProb,
                            0xff - 1 - oldProb) - 1);

            return (i + 7) % 13 == 0
                ? ((i + 7) / 13) - 1
                : i + 20 - ((i + 7) / 13);
        }

        private static int CalcMinLog2TileCols(
            int frameWidth)
        {
            int sb64Cols =
                (frameWidth + 63) / 64;

            int minLog2 = 0;

            while ((64 << minLog2) < sb64Cols)
            {
                minLog2++;
            }

            return minLog2;
        }

        private static int CalcMaxLog2TileCols(
            int frameWidth)
        {
            int sb64Cols =
                (frameWidth + 63) / 64;

            int maxLog2 = 1;

            while ((sb64Cols >> maxLog2) >= 4)
            {
                maxLog2++;
            }

            return maxLog2 - 1;
        }
    }
}
