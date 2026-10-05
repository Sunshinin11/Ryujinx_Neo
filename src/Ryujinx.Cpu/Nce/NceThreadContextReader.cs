using ARMeilleure.State;
using Ryujinx.Memory;
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    internal static class NceThreadContextReader
    {
        // A source is live only while the HLE target is stopped under activity/critical-section ownership.
        // Kind 0/4: not entered/initial entry; 1: kernel frame; 2/3: SVC/counter save area.
        internal static bool Capture(ref NceNativeContext.NativeCtxStorage storage, NceSnapshotCodeMap map,
            Func<ulong, int, byte[]> readFrame, Func<ulong, int, byte[]> readStack, out CpuThreadContext result)
        {
            result = default;
            try
            {
                CpuThreadContext snapshot = FromStorage(ref storage);
                if (storage.SnapshotKind == 0 || storage.SnapshotKind == 4) { result = snapshot; return true; }
                if (storage.SnapshotKind == 1)
                {
                    byte[] frame = readFrame(storage.SnapshotSource, 464 + 4096);
                    if (frame == null || frame.Length != 464 + 4096) return false;
                    snapshot.Pc = U64(frame, 440); snapshot.Pstate = U32(frame, 448);
                    if (snapshot.Pc >= NceSnapshotCodeMap.EntryStart && snapshot.Pc < NceSnapshotCodeMap.EntryEnd)
                    { result = FromStorage(ref storage); return true; }
                    if (snapshot.Pc < storage.GuestAddressStart || snapshot.Pc >= storage.GuestAddressEnd) return false;
                    if (readStack(snapshot.Pc, 4) is not { Length: 4 }) return false;
                    for (int i = 0; i < 31; i++) snapshot.Registers[i] = U64(frame, 184 + i * 8);
                    snapshot.Registers[31] = U64(frame, 432);
                    if (!ReadFpFrame(frame, ref snapshot)) return false;
                    if (map.Find(snapshot.Pc, out NceSnapshotUnwindInfo info, out ulong origin, out int index))
                    {
                        ulong nzcv = info.NzcvRegister >= 0 ? snapshot.Registers[info.NzcvRegister] : 0;
                        ulong sp = AddOffset(snapshot.Registers[31], -info.StackDelta(index));
                        byte[] saved = null;
                        foreach (var slot in info.Slots)
                        {
                            if (slot.SavedAt >= index || slot.RestoredAt < index) continue;
                            saved ??= readStack(AddOffset(sp, -info.SaveSize), info.SaveSize);
                            if (saved == null || saved.Length != info.SaveSize) return false;
                            ReadSlot(saved, slot, info.SaveSize, ref snapshot);
                        }
                        snapshot.Registers[31] = sp;
                        bool completed = info.Managed ? info.NativePublishAt >= 0 && index > info.NativePublishAt
                            : info.SemanticAt >= 0 && index > info.SemanticAt;
                        snapshot.Pc = checked(origin + (completed ? 4UL : 0UL));
                        if (info.NzcvAt >= 0 && index > info.NzcvAt &&
                            (info.Managed ? !completed : index <= info.NzcvRestoreAt))
                            snapshot.Pstate = (snapshot.Pstate & ~0xf0000000u) | ((uint)nzcv & 0xf0000000u);
                        if (info.IsSvc && completed)
                            for (int r = 0; r < 8; r++) snapshot.Registers[r] = storage.X[r];
                        if (info.IsCounter && completed) snapshot.Registers[info.Rd] = storage.TempStorage;
                        else if (info.IsRead && info.RdStoreAt >= 0 && index > info.RdStoreAt && !completed)
                            snapshot.Registers[info.Rd] = storage.SnapshotRd;
                    }
                }
                else if (storage.SnapshotKind == 2 || storage.SnapshotKind == 3)
                {
                    if (readStack(storage.SnapshotPc, 4) is not { Length: 4 } ||
                        !map.Find(storage.SnapshotPc, out NceSnapshotUnwindInfo info, out ulong origin, out _) || !info.Managed) return false;
                    if ((storage.SnapshotKind == 2) != info.IsSvc) return false;
                    byte[] saved = readStack(storage.SnapshotSource, info.SaveSize);
                    if (saved == null || saved.Length != info.SaveSize) return false;
                    foreach (var slot in info.Slots) ReadSlot(saved, slot, info.SaveSize, ref snapshot);
                    snapshot.Registers[31] = AddOffset(storage.SnapshotSource, info.SaveSize);
                    snapshot.Pc = checked(origin + 4);
                    if (info.IsSvc) for (int r = 0; r < 8; r++) snapshot.Registers[r] = storage.X[r];
                    else snapshot.Registers[info.Rd] = storage.TempStorage;
                }
                else return false;
                result = snapshot; return true;
            }
            catch (InvalidMemoryRegionException) { return false; }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static CpuThreadContext FromStorage(ref NceNativeContext.NativeCtxStorage storage)
            => new() { Registers = storage.X, Vectors = storage.V, Pc = storage.Pc, Pstate = storage.Pstate,
                Fpcr = storage.Fpcr, Fpsr = storage.Fpsr, TpidrroEl0 = (long)storage.TpidrroEl0 };

        private static void ReadSlot(byte[] saved, NceSnapshotUnwindInfo.Slot slot, int size, ref CpuThreadContext snapshot)
        {
            int offset = checked(size + slot.Offset);
            if (slot.Vector) snapshot.Vectors[slot.Register] = MemoryMarshal.Read<V128>(saved.AsSpan(offset, 16));
            else snapshot.Registers[slot.Register] = U64(saved, offset);
        }

        private static bool ReadFpFrame(byte[] frame, ref CpuThreadContext snapshot)
        {
            // FPSIMD must be inside sigcontext.__reserved; optional records may precede it.
            // Never guess an absent record, loop on a terminator or follow an arbitrary extra-context pointer.
            int offset = 464, end = frame.Length;
            while (offset <= end - 8)
            {
                uint magic = U32(frame, offset), length = U32(frame, offset + 4);
                if (magic == 0 && length == 0) return false;
                if (length < 16 || (length & 15) != 0 || length > end - offset) return false;
                if (magic == 0x46508001)
                {
                    if (length < 528) return false;
                    snapshot.Fpsr = U32(frame, offset + 8); snapshot.Fpcr = U32(frame, offset + 12);
                    for (int i = 0; i < 32; i++) snapshot.Vectors[i] = MemoryMarshal.Read<V128>(frame.AsSpan(offset + 16 + i * 16, 16));
                    return true;
                }
                offset += checked((int)length);
            }
            return false;
        }

        private static ulong AddOffset(ulong value, int offset)
            => offset < 0 ? checked(value - (ulong)-(long)offset) : checked(value + (ulong)offset);
        private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }
}
