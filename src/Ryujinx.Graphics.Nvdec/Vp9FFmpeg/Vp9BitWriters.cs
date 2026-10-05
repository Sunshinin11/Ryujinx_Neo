// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2020 yuzu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// NEOV2-VP9EDEN1: porte em C# de VpxRangeEncoder e VpxBitStreamWriter
// (eden: src/video_core/host1x/codecs/vp9.cpp).

using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Nvdec.Vp9FFmpeg
{
    /// <summary>Codificador aritmetico booleano do VP9, usado no cabecalho comprimido.</summary>
    internal sealed class VpxRangeEncoder
    {
        private const int HalfProbability = 128;

        private readonly List<byte> _stream = new();
        private uint _lowValue;
        private uint _range = 0xff;
        private int _count = -24;

        public VpxRangeEncoder()
        {
            Write(false);
        }

        /// <summary>Escreve os valueSize bits mais a direita de value.</summary>
        public void Write(int value, int valueSize)
        {
            for (int bit = valueSize - 1; bit >= 0; bit--)
            {
                Write(((value >> bit) & 1) != 0);
            }
        }

        public void Write(bool bit)
        {
            Write(bit, HalfProbability);
        }

        public void Write(bool bit, int probability)
        {
            uint localRange = _range;
            uint split = 1 + (((localRange - 1) * (uint)probability) >> 8);
            localRange = split;

            if (bit)
            {
                _lowValue += split;
                localRange = _range - split;
            }

            int shift = localRange == 0 ? 0 : BitOperations.LeadingZeroCount(localRange) - 24;
            localRange <<= shift;
            _count += shift;

            if (_count >= 0)
            {
                int offset = shift - _count;

                if (((_lowValue << (offset - 1)) >> 31) != 0)
                {
                    // Propaga o "vai um" pelos bytes ja escritos.
                    int index = _stream.Count - 1;

                    while (index >= 0 && _stream[index] == 0xff)
                    {
                        _stream[index] = 0;
                        index--;
                    }

                    if (index >= 0)
                    {
                        _stream[index]++;
                    }
                }

                _stream.Add((byte)(_lowValue >> (24 - offset)));

                _lowValue <<= offset;
                shift = _count;
                _lowValue &= 0xffffff;
                _count -= 8;
            }

            _lowValue <<= shift;
            _range = localRange;
        }

        public void End()
        {
            for (int index = 0; index < 32; index++)
            {
                Write(false);
            }
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    /// <summary>Escritor de bits (MSB primeiro) do cabecalho nao comprimido do VP9.</summary>
    internal sealed class VpxBitStreamWriter
    {
        private readonly List<byte> _bytes = new();
        private int _buffer;
        private int _bufferPos;

        public void WriteU(uint value, int valueSize)
        {
            for (int bit = valueSize - 1; bit >= 0; bit--)
            {
                WriteBit(((value >> bit) & 1) != 0);
            }
        }

        /// <summary>Valor com sinal no formato su(n): magnitude em n bits seguida do bit de sinal.</summary>
        public void WriteS(int value, int valueSize)
        {
            bool sign = value < 0;

            if (sign)
            {
                value = -value;
            }

            WriteU(((uint)value << 1) | (sign ? 1u : 0u), valueSize + 1);
        }

        /// <summary>delta_q do VP9 (6.2.10): bit de presenca e, se presente, su(4).</summary>
        public void WriteDeltaQ(int value)
        {
            bool deltaCoded = value != 0;
            WriteBit(deltaCoded);

            if (deltaCoded)
            {
                WriteS(value, 4);
            }
        }

        public void WriteBit(bool state)
        {
            if (state)
            {
                _buffer |= 1 << (7 - _bufferPos);
            }

            _bufferPos++;

            if (_bufferPos == 8)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_bufferPos == 0)
            {
                return;
            }

            _bytes.Add((byte)_buffer);
            _buffer = 0;
            _bufferPos = 0;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
