using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// Buffer handles given to the client are not the same as those provided by the backend,
    /// as their handle is created at a later point on the queue.
    /// The handle returned is a unique identifier that will map to the real buffer when it is available.
    /// Note that any uses within the queue should be safe, but outside you must use MapBufferBlocking.
    /// </summary>
    class BufferMap
    {
        private const int InitialMapSize = 4096;

        private ulong _bufferHandle = 0;

        // Threaded buffer handles are monotonically increasing integers. Indexing by that
        // identifier avoids a Dictionary lookup and lock for every queued buffer binding.
        // The backend queue is the sole writer. Volatile publication keeps the exceptional
        // MapBufferBlocking reader safe when the table grows or an entry changes.
        private ulong[] _bufferMap = new ulong[InitialMapSize];
        private readonly HashSet<BufferHandle> _inFlight = [];
        private readonly AutoResetEvent _inFlightChanged = new(false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetHandleValue(BufferHandle handle)
        {
            return Unsafe.As<BufferHandle, ulong>(ref handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BufferHandle GetBufferHandle(ulong value)
        {
            return Unsafe.As<ulong, BufferHandle>(ref value);
        }

        private ulong[] EnsureMapCapacity(ulong handle)
        {
            if (handle > int.MaxValue)
            {
                throw new InvalidOperationException("Threaded buffer handle space exhausted.");
            }

            ulong[] map = Volatile.Read(ref _bufferMap);

            if (handle >= (ulong)map.Length)
            {
                int newSize = map.Length;

                while (handle >= (ulong)newSize)
                {
                    newSize = checked(newSize * 2);
                }

                ulong[] newMap = new ulong[newSize];
                Array.Copy(map, newMap, map.Length);
                Volatile.Write(ref _bufferMap, newMap);
                map = newMap;
            }

            return map;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BufferHandle MapBuffer(ulong[] map, BufferHandle handle)
        {
            ulong index = GetHandleValue(handle);

            if (index >= (ulong)map.Length)
            {
                return BufferHandle.Null;
            }

            return GetBufferHandle(Volatile.Read(ref map[(int)index]));
        }

        internal BufferHandle CreateBufferHandle()
        {
            ulong handle64 = Interlocked.Increment(ref _bufferHandle);

            BufferHandle threadedHandle = GetBufferHandle(handle64);

            lock (_inFlight)
            {
                _inFlight.Add(threadedHandle);
            }

            return threadedHandle;
        }

        internal void AssignBuffer(BufferHandle threadedHandle, BufferHandle realHandle)
        {
            ulong index = GetHandleValue(threadedHandle);
            ulong[] map = EnsureMapCapacity(index);

            Volatile.Write(ref map[(int)index], GetHandleValue(realHandle));

            lock (_inFlight)
            {
                _inFlight.Remove(threadedHandle);
            }

            _inFlightChanged.Set();
        }

        internal void UnassignBuffer(BufferHandle threadedHandle)
        {
            ulong index = GetHandleValue(threadedHandle);
            ulong[] map = Volatile.Read(ref _bufferMap);

            if (index < (ulong)map.Length)
            {
                Volatile.Write(ref map[(int)index], 0UL);
            }
        }

        internal BufferHandle MapBuffer(BufferHandle handle)
        {
            // Maps a threaded buffer to a backend one.
            // Threaded buffers are returned on creation as the buffer
            // isn't actually created until the queue runs the command.

            return MapBuffer(Volatile.Read(ref _bufferMap), handle);
        }

        internal BufferHandle MapBufferBlocking(BufferHandle handle)
        {
            // Blocks until the handle is available.

            BufferHandle result = MapBuffer(handle);

            if (result != BufferHandle.Null)
            {
                return result;
            }

            bool signal = false;

            while (true)
            {
                lock (_inFlight)
                {
                    if (!_inFlight.Contains(handle))
                    {
                        break;
                    }
                }

                _inFlightChanged.WaitOne();
                signal = true;
            }

            if (signal)
            {
                // Signal other threads which might still be waiting.
                _inFlightChanged.Set();
            }

            return MapBuffer(handle);
        }

        internal BufferRange MapBufferRange(BufferRange range)
        {
            return new BufferRange(MapBuffer(range.Handle), range.Offset, range.Size, range.Write);
        }

        internal Span<BufferRange> MapBufferRanges(Span<BufferRange> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            ulong[] map = Volatile.Read(ref _bufferMap);

            for (int i = 0; i < ranges.Length; i++)
            {
                ref BufferRange range = ref ranges[i];
                BufferHandle result = MapBuffer(map, range.Handle);

                range = new BufferRange(result, range.Offset, range.Size, range.Write);
            }

            return ranges;
        }

        internal Span<BufferAssignment> MapBufferRanges(Span<BufferAssignment> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            ulong[] map = Volatile.Read(ref _bufferMap);

            for (int i = 0; i < ranges.Length; i++)
            {
                ref BufferAssignment assignment = ref ranges[i];
                BufferRange range = assignment.Range;
                BufferHandle result = MapBuffer(map, range.Handle);

                assignment = new BufferAssignment(assignment.Binding, new BufferRange(result, range.Offset, range.Size, range.Write));
            }

            return ranges;
        }

        internal Span<VertexBufferDescriptor> MapBufferRanges(Span<VertexBufferDescriptor> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            ulong[] map = Volatile.Read(ref _bufferMap);

            for (int i = 0; i < ranges.Length; i++)
            {
                BufferRange range = ranges[i].Buffer;
                BufferHandle result = MapBuffer(map, range.Handle);

                range = new BufferRange(result, range.Offset, range.Size, range.Write);

                ranges[i] = new VertexBufferDescriptor(range, ranges[i].Stride, ranges[i].Divisor);
            }

            return ranges;
        }
    }
}
