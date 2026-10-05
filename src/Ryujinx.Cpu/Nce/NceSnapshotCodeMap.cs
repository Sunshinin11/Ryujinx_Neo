using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Cpu.Nce
{
    // Read-side metadata only: no runtime guest dispatch, extra executable instruction or flush.
    internal sealed class NceSnapshotCodeMap
    {
        private static readonly ConditionalWeakTable<IVirtualMemoryManager, NceSnapshotCodeMap> Maps = new();
        private readonly object _lock = new();
        private readonly List<Range> _ranges = new();
        internal static ulong EntryStart, EntryEnd;
        private readonly record struct Range(ulong Start, ulong End, ulong Origin, NceSnapshotUnwindInfo Info);

        internal static NceSnapshotCodeMap ForMemory(IVirtualMemoryManager memory)
            => Maps.GetValue(memory, key => new NceSnapshotCodeMap(key));

        private NceSnapshotCodeMap(IVirtualMemoryManager memory)
        {
            if (memory is MemoryManagerNative native) native.UnmapEvent += Remove;
        }

        internal void Register(ulong start, ulong origin, uint instruction, uint[] code)
        {
            NceSnapshotUnwindInfo info = new(instruction, code);
            ulong end = checked(start + (ulong)code.Length * 4);
            RegisterRanges(new List<Range> { new(start, end, origin, info) });
        }

        internal void RegisterBlock(ulong patchAddress, ulong textAddress,
            IReadOnlyList<(int TextIndex, int PatchIndex, uint Instruction, uint[] Code)> targets)
        {
            List<Range> ranges = new(targets.Count);
            foreach (var target in targets)
            {
                ulong start = checked(patchAddress + (ulong)target.PatchIndex * 4);
                ranges.Add(new Range(start, checked(start + (ulong)target.Code.Length * 4),
                    checked(textAddress + (ulong)target.TextIndex * 4), new NceSnapshotUnwindInfo(target.Instruction, target.Code)));
            }
            if (ranges.Count != 0) RegisterRanges(ranges);
        }

        private void RegisterRanges(List<Range> ranges)
        {
            ulong start = ranges[0].Start, end = ranges[^1].End;
            lock (_lock)
            {
                _ranges.RemoveAll(r => r.Start < end && start < r.End);
                _ranges.AddRange(ranges);
                _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }

        private void Remove(ulong start, ulong size)
        {
            ulong end = checked(start + size);
            lock (_lock) _ranges.RemoveAll(r => r.Start < end && start < r.End || r.Origin >= start && r.Origin < end);
        }

        internal bool Find(ulong pc, out NceSnapshotUnwindInfo info, out ulong origin, out int index)
        {
            lock (_lock)
            {
                int low = 0, high = _ranges.Count - 1;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2; Range range = _ranges[mid];
                    if (pc < range.Start) high = mid - 1;
                    else if (pc >= range.End) low = mid + 1;
                    else
                    {
                        info = range.Info; origin = range.Origin; index = checked((int)((pc - range.Start) / 4));
                        return (pc & 3) == 0;
                    }
                }
            }
            info = null; origin = 0; index = 0; return false;
        }
    }

    // Decode only the save/restore instructions emitted by RegisterSaveRestore.
    // A paused PC may be at any instruction of a trampoline, including a partial prologue/epilogue.
    internal sealed class NceSnapshotUnwindInfo
    {
        internal readonly record struct Slot(int Register, bool Vector, int Offset, int SavedAt, int RestoredAt);
        internal readonly List<Slot> Slots = new();
        private readonly List<(int Index, int Delta)> _stackChanges = new();
        internal readonly uint Instruction;
        internal readonly int SaveSize, PrologueEnd, EpilogueStart, NzcvRegister, NzcvAt, NzcvRestoreAt;
        internal readonly int RdStoreAt, SemanticAt, NativePublishAt;
        internal bool Managed => IsSvc || IsCounter;
        internal bool IsSvc => (Instruction & ~(0xffffu << 5)) == 0xd4000001u;
        internal bool IsCounter => (Instruction & ~31u) == 0xd53be020u;
        internal bool IsRead => !IsSvc && (Instruction & 0xfff00000u) == 0xd5300000u;
        internal int Rd => (int)(Instruction & 31);

        internal NceSnapshotUnwindInfo(uint instruction, uint[] code)
        {
            Instruction = instruction;
            int delta = 0, i = 0;
            for (; i < code.Length && DecodeStack(code[i], out bool load, out bool vector, out int rt, out int rt2, out int offset, out int update, out bool pre) && !load; i++)
            {
                if (pre) delta += update;
                if (rt >= 0) Slots.Add(new Slot(rt, vector, delta + offset, i, int.MaxValue));
                if (rt2 >= 0) Slots.Add(new Slot(rt2, vector, delta + offset + (vector ? 16 : 8), i, int.MaxValue));
                if (!pre) delta += update;
                if (update != 0) _stackChanges.Add((i, delta));
            }
            PrologueEnd = i; SaveSize = -delta;
            // Tail is the guest epilogue, then an optional TLS destination load, then B textReturn.
            int tail = code.Length - (IsRead && !IsCounter ? 2 : 1);
            int first = tail;
            while (first > 0 && DecodeStack(code[first - 1], out bool load, out _, out _, out _, out _, out _, out _) && load) first--;
            EpilogueStart = first;
            for (i = first; i < tail; i++)
            {
                DecodeStack(code[i], out _, out bool vector, out int rt, out int rt2, out _, out int update, out _);
                for (int s = 0; s < Slots.Count; s++)
                    if (Slots[s].Vector == vector && (Slots[s].Register == rt || Slots[s].Register == rt2)) Slots[s] = Slots[s] with { RestoredAt = i };
                delta += update; if (update != 0) _stackChanges.Add((i, delta));
            }
            if (SaveSize <= 0 || delta != 0 || Slots.Count == 0 || first == tail)
                throw new ArgumentException("NCE snapshot metadata does not match the emitted save/restore frame.");
            NzcvAt = NzcvRestoreAt = RdStoreAt = SemanticAt = NativePublishAt = -1;
            NzcvRegister = -1;
            for (i = PrologueEnd; i < first; i++)
            {
                uint word = code[i];
                if ((word & ~31u) == 0xd53b4200u && NzcvAt < 0) { NzcvAt = i; NzcvRegister = (int)(word & 31); }
                if ((word & ~31u) == 0xd51b4200u && !Managed) NzcvRestoreAt = i;
                if ((word & 0xffc00000u) == 0xf9000000u)
                {
                    int off = (int)((word >> 10) & 4095) * 8;
                    if ((word & 31) == Rd && off == NceNativeContext.GetSnapshotRdOffset()) RdStoreAt = i;
                    if ((word & 31) == Rd && off == NceNativeContext.GetTpidrEl0Offset() && !IsRead) SemanticAt = i;
                }
                if ((word & 0xffe0fc1fu) == 0x8800fc1fu) NativePublishAt = i; // STLXR Wstatus,WZR,[base].
            }
            if (IsRead && !IsCounter) SemanticAt = code.Length - 2;
        }

        internal int StackDelta(int index)
        {
            int delta = 0;
            foreach ((int at, int value) in _stackChanges) if (at < index) delta = value;
            return delta;
        }

        // integer64/Q pair and unsigned/pre/post single stores/loads on SP.
        private static bool DecodeStack(uint word, out bool load, out bool vector, out int rt, out int rt2,
            out int offset, out int update, out bool pre)
        {
            load = vector = pre = false; rt = (int)(word & 31); rt2 = -1; offset = update = 0;
            if (((word >> 5) & 31) != 31) return false;
            if ((word & 0xbfc003ffu) == 0x910003ffu)
            {
                int imm = (int)((word >> 10) & 4095); if (imm == 0) return false;
                if ((word & 0x00400000) != 0) imm <<= 12;
                bool subtract = (word & 0x40000000) != 0;
                rt = -1; update = subtract ? -imm : imm; pre = subtract; load = !subtract;
                return true;
            }
            uint pair = word & 0x3a000000u;
            if (pair == 0x28000000u)
            {
                vector = (word & 0x04000000) != 0;
                if ((word >> 30) != 2) return false; // X64 or Q128, not W/D.
                load = (word & 0x00400000) != 0; rt2 = (int)((word >> 10) & 31);
                int imm = (int)((word >> 15) & 127); if (imm >= 64) imm -= 128;
                imm *= vector ? 16 : 8;
                int mode = (int)((word >> 23) & 3);
                pre = mode == 3; update = mode == 1 || pre ? imm : 0;
                offset = pre || mode == 1 ? 0 : imm;
                return mode != 0;
            }
            if ((word & 0xff000000u) == 0xf9000000u)
            { load = (word & 0x00400000) != 0; offset = (int)((word >> 10) & 4095) * 8; return true; }
            if ((word & 0xff200000u) == 0xf8000000u)
            {
                load = (word & 0x00400000) != 0;
                int mode = (int)((word >> 10) & 3); if (mode != 1 && mode != 3) return false;
                int imm = (int)((word >> 12) & 511); if (imm >= 256) imm -= 512;
                pre = mode == 3; update = imm; return true;
            }
            return false;
        }
    }
}
