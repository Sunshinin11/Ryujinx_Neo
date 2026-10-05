using Ryujinx.Memory;
using System;

namespace Ryujinx.Cpu.Nce
{
    // Native Android mappings only. The lock never encloses a tracking/GPU callback.
    // One interval stores the guest contract separately from the last tracking request.
    internal sealed class NceMemoryProtectionMap
    {
        private const ulong PageSize = 0x1000;
        private const MemoryPermission Data = MemoryPermission.ReadAndWrite;
        private readonly object _lock = new();
        private readonly ulong _start, _end;
        private readonly Action<ulong, ulong, MemoryPermission> _apply;
        private readonly NceProtectionRangeList<Range> _ranges = new();
        private readonly record struct State(MemoryPermission Guest, MemoryPermission Tracking, bool Active)
        {
            internal MemoryPermission Effective => Active ? Compose(Guest, Tracking) : Guest;
        }
        private readonly record struct Range(ulong Start, ulong End, State Permissions);

        internal NceMemoryProtectionMap(ulong start, ulong size, ulong hostPageSize,
            Action<ulong, ulong, MemoryPermission> apply)
        {
            if (hostPageSize != PageSize)
                throw new NotSupportedException("Native NCE requires 4 KiB host pages to enforce 4 KiB guest permissions.");
            if (size == 0 || ((start | size) & (PageSize - 1)) != 0)
                throw new ArgumentException("NCE protection reservation must be page aligned.");
            _start = start; _end = checked(start + size);
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        internal static MemoryPermission Compose(MemoryPermission guest, MemoryPermission tracking)
            => (guest & tracking & Data) | (guest & MemoryPermission.Execute);

        internal void Map(ulong address, ulong size, Action map)
        {
            ulong end = Validate(address, size);
            lock (_lock)
            {
                Split(address); Split(end);
                int first = LowerBound(address), last = first;
                while (last < _ranges.Count && _ranges[last].Start < end) last++;
                map(); // A new MapView starts RW, even when replacing a former mapping.
                if (last != first) _ranges.RemoveRange(first, last - first);
                _ranges.Insert(first, new(address, end, new(Data, Data, false)));
                Merge(address, end);
            }
        }

        internal void Unmap(ulong address, ulong size, Action unmap)
        {
            ulong end = Validate(address, size);
            lock (_lock)
            {
                Split(address); Split(end);
                int first = LowerBound(address), last = first;
                while (last < _ranges.Count && _ranges[last].Start < end) last++;
                unmap();
                if (last != first) _ranges.RemoveRange(first, last - first);
            }
        }

        internal bool SetGuest(ulong address, ulong size, MemoryPermission permission, bool restoreTracking)
        {
            if (size == 0) return false;
            ValidatePermission(permission, tracking: false);
            ulong end = Validate(address, size);
            lock (_lock)
            {
                if (!IsMapped(address, end)) throw new InvalidMemoryRegionException();
                return Update(address, end, permission, guest: true, restoreTracking);
            }
        }

        internal void SetTracking(ulong address, ulong size, MemoryPermission permission)
        {
            if (size == 0) return;
            ValidatePermission(permission, tracking: true);
            ulong end = Validate(address, size);
            lock (_lock)
            {
                // Tracking may cover a partially unmapped region. Never reprotect its holes.
                Update(address, end, permission, guest: false, restoreTracking: true);
            }
        }

        private bool Update(ulong address, ulong end, MemoryPermission permission, bool guest, bool restoreTracking)
        {
            Split(address); Split(end);
            int i = LowerBound(address);
            bool restored = false;
            while (i < _ranges.Count && _ranges[i].Start < end)
            {
                int first = i;
                State state = Next(_ranges[i].Permissions, permission, guest, restoreTracking);
                MemoryPermission effective = state.Effective;
                ulong begin = _ranges[i].Start, finish = _ranges[i].End;
                bool changed = _ranges[i].Permissions.Effective != effective;
                restored |= guest && restoreTracking && state.Tracking != Data;
                i++;
                while (i < _ranges.Count && _ranges[i].Start < end && _ranges[i].Start == finish)
                {
                    State next = Next(_ranges[i].Permissions, permission, guest, restoreTracking);
                    if (next.Effective != effective) break;
                    changed |= _ranges[i].Permissions.Effective != effective;
                    restored |= guest && restoreTracking && next.Tracking != Data;
                    finish = _ranges[i].End; i++;
                }
                // Publish metadata only after a successful host protection call.
                // Previously completed groups remain coherent if a later group fails.
                if (changed) _apply(begin, finish - begin, effective);
                for (int j = first; j < i; j++)
                    _ranges[j] = _ranges[j] with { Permissions = Next(_ranges[j].Permissions, permission, guest, restoreTracking) };
            }
            Merge(address, end);
            return restored;
        }

        private static State Next(State state, MemoryPermission permission, bool guest, bool restoreTracking)
            => guest ? new(permission, state.Tracking, restoreTracking) : new(state.Guest, permission, true);

        internal bool IsGuestAccessAllowed(ulong address, ulong size, bool write)
            => GetAccessState(address, size, write).GuestAllowed;

        internal bool IsEffectiveAccessAllowed(ulong address, ulong size, bool write)
            => GetAccessState(address, size, write).EffectiveAllowed;

        // One coherent snapshot, without an extra lock for the diagnostic effective state.
        internal (bool GuestAllowed, bool EffectiveAllowed) GetAccessState(ulong address, ulong size, bool write)
        {
            if (size == 0 || address < _start || address >= _end || size > _end - address) return default;
            ulong end = address + size;
            MemoryPermission access = write ? MemoryPermission.Write : MemoryPermission.Read;
            lock (_lock)
            {
                bool effectiveAllowed = true;
                int i = Find(address);
                while (address < end && i >= 0 && i < _ranges.Count)
                {
                    Range range = _ranges[i++];
                    if (range.Start > address || range.End <= address ||
                        (range.Permissions.Guest & access) == 0) return default;
                    effectiveAllowed &= (range.Permissions.Effective & access) != 0;
                    address = range.End;
                }
                return address >= end ? (true, effectiveAllowed) : default;
            }
        }

        private bool IsMapped(ulong address, ulong end)
        {
            int i = Find(address);
            while (address < end && i >= 0 && i < _ranges.Count)
            {
                Range range = _ranges[i++];
                if (range.Start > address || range.End <= address) return false;
                address = range.End;
            }
            return address >= end;
        }

        private ulong Validate(ulong address, ulong size)
        {
            if (size == 0 || ((address | size) & (PageSize - 1)) != 0 ||
                address < _start || address >= _end || size > _end - address)
                throw new ArgumentException("Invalid NCE protection range.");
            return address + size;
        }

        private static void ValidatePermission(MemoryPermission permission, bool tracking)
        {
            bool valid = tracking
                ? permission is MemoryPermission.None or MemoryPermission.Read or MemoryPermission.ReadAndWrite
                : permission is MemoryPermission.None or MemoryPermission.Read or MemoryPermission.ReadAndWrite or
                    MemoryPermission.Execute or MemoryPermission.ReadAndExecute or MemoryPermission.ReadWriteExecute;
            if (!valid) throw new ArgumentException("Unsupported NCE permission.");
        }

        private int LowerBound(ulong address)
            => _ranges.LowerBound(address, static range => range.Start);

        private int Find(ulong address)
        {
            int i = LowerBound(address);
            if (i < _ranges.Count && _ranges[i].Start == address) return i;
            i--;
            return i >= 0 && address < _ranges[i].End ? i : -1;
        }

        private void Split(ulong address)
        {
            int i = Find(address);
            if (i < 0 || _ranges[i].Start == address) return;
            Range range = _ranges[i];
            _ranges[i] = range with { End = address };
            _ranges.Insert(i + 1, range with { Start = address });
        }

        private void Merge(ulong address, ulong end)
        {
            for (int i = Math.Max(0, LowerBound(address) - 1); i + 1 < _ranges.Count && _ranges[i].Start <= end;)
            {
                Range a = _ranges[i], b = _ranges[i + 1];
                if (a.End == b.Start && a.Permissions == b.Permissions)
                {
                    _ranges[i] = a with { End = b.End };
                    _ranges.RemoveAt(i + 1);
                }
                else i++;
            }
        }
    }
}
