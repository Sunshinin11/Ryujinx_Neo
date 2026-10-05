using System;
using System.Collections.Generic;

namespace Ryujinx.Cpu.Nce
{
    // Owner provides synchronization. Sorted keys must remain sorted after indexer writes.
    // Index changes move at most one leaf's items; the small directory stores prefix counts.
    internal sealed class NceProtectionRangeList<T> where T : struct
    {
        private readonly int _capacity;
        private readonly List<List<T>> _blocks = new();
        private readonly List<int> _ends = new();
        private int _count, _cachedBlock = -1;
        internal int Count => _count;
        internal int BlockCount => _blocks.Count;

        internal NceProtectionRangeList(int capacity = 128)
        {
            if (capacity < 4) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        internal T this[int index]
        {
            get { int block = Locate(index); return _blocks[block][index - Start(block)]; }
            set { int block = Locate(index); _blocks[block][index - Start(block)] = value; }
        }

        internal int LowerBound(ulong key, Func<T, ulong> start)
        {
            int low = 0, high = _blocks.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                List<T> block = _blocks[mid];
                if (start(block[^1]) < key) low = mid + 1; else high = mid;
            }
            if (low == _blocks.Count) return _count;
            int selected = low;
            List<T> items = _blocks[selected];
            low = 0; high = items.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (start(items[mid]) < key) low = mid + 1; else high = mid;
            }
            _cachedBlock = selected;
            return Start(selected) + low;
        }

        internal void Insert(int index, T value)
        {
            if (index < 0 || index > _count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_blocks.Count == 0)
            {
                _blocks.Add(new List<T>(_capacity)); _ends.Add(0);
            }
            int selected = index == _count ? _blocks.Count - 1 : Locate(index);
            int offset = index - Start(selected);
            List<T> items = _blocks[selected];
            if (items.Count == _capacity)
            {
                if (index == _count)
                {
                    _blocks.Add(new List<T>(_capacity)); _ends.Add(_count);
                    selected++; offset = 0; items = _blocks[selected];
                }
                else
                {
                    int middle = items.Count / 2;
                    List<T> right = new(_capacity);
                    for (int i = middle; i < items.Count; i++) right.Add(items[i]);
                    items.RemoveRange(middle, items.Count - middle);
                    _blocks.Insert(selected + 1, right); _ends.Insert(selected + 1, 0);
                    if (offset >= middle) { offset -= middle; selected++; items = right; }
                }
            }
            items.Insert(offset, value); _count++;
            // The left half also changed if selected now points at the new right half.
            Rebuild(Math.Max(0, selected - 1));
        }

        internal void RemoveAt(int index) => RemoveRange(index, 1);

        internal void RemoveRange(int index, int count)
        {
            if (index < 0 || index > _count || count < 0 || count > _count - index)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;
            int first = Locate(index), selected = first, offset = index - Start(first);
            int remaining = count;
            while (remaining != 0)
            {
                List<T> items = _blocks[selected];
                int take = Math.Min(remaining, items.Count - offset);
                items.RemoveRange(offset, take); remaining -= take;
                if (items.Count == 0) { _blocks.RemoveAt(selected); _ends.RemoveAt(selected); }
                else selected++;
                offset = 0;
            }
            _count -= count;
            int begin = Math.Max(0, first - 1);
            // Hysteresis: merge only half-full pairs, avoiding split/merge allocation churn
            // when hot protection ranges alternate around a full leaf's boundary.
            for (int i = begin; i + 1 < _blocks.Count && i <= first + 1;)
            {
                List<T> left = _blocks[i], right = _blocks[i + 1];
                if (left.Count + right.Count <= _capacity / 2)
                {
                    left.AddRange(right); _blocks.RemoveAt(i + 1); _ends.RemoveAt(i + 1);
                }
                else i++;
            }
            Rebuild(begin);
        }

        private int Start(int block) => block == 0 ? 0 : _ends[block - 1];

        private int Locate(int index)
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_cachedBlock >= 0 && _cachedBlock < _blocks.Count &&
                index >= Start(_cachedBlock) && index < _ends[_cachedBlock]) return _cachedBlock;
            int low = 0, high = _ends.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (_ends[mid] <= index) low = mid + 1; else high = mid;
            }
            _cachedBlock = low;
            return low;
        }

        private void Rebuild(int first)
        {
            int end = Start(first);
            for (int i = first; i < _blocks.Count; i++)
            {
                end += _blocks[i].Count; _ends[i] = end;
            }
            _cachedBlock = -1;
        }
    }
}
