using ARMeilleure.State;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Nce
{
    class NceExecutionContext : IExecutionContext, IThreadContextProvider
    {
        private const ulong AlternateStackSize = 0x20000;
        private const uint FpStateNzcvMask = 0xf0000000u;

        private readonly NceNativeContext _context;
        private readonly MemoryManagerNative _snapshotMemory;
        public bool SupportsThreadContext => _snapshotMemory != null;
        private readonly ExceptionCallbacks _exceptionCallbacks;

        internal nint NativeContextPtr => _context.BasePtr;

        private readonly MemoryBlock _alternateStackMemory;
        private bool _alternateStackInstalled;

        // NEOV2-COSMIC2: o escalonador (KScheduler.RescheduleOtherCores) pode pedir interrupcao
        // de uma thread cujo contexto nativo ja foi descartado (ponteiro 0 -> NullReferenceException,
        // visto no Marvel Cosmic Invasion). Dispose e RequestInterrupt passam a ser serializados.
        private readonly object _neoDisposeLock = new();
        private bool _neoDisposed;

        public ulong Pc => _context.GetStorage().Pc;

        public long TpidrEl0
        {
            get => (long)_context.GetStorage().TpidrEl0;
            set => _context.GetStorage().TpidrEl0 = (ulong)value;
        }

        public long TpidrroEl0
        {
            get => (long)_context.GetStorage().TpidrroEl0;
            set => _context.GetStorage().TpidrroEl0 = (ulong)value;
        }

        public uint Pstate
        {
            get => _context.GetStorage().Pstate;
            set => _context.GetStorage().Pstate = value;
        }

        public uint Fpcr
        {
            get => _context.GetStorage().Fpcr;
            set => _context.GetStorage().Fpcr = value;
        }

        public uint Fpsr
        {
            get => _context.GetStorage().Fpsr;
            set => _context.GetStorage().Fpsr = value;
        }

        public bool IsAarch32
        {
            get => false;
            set
            {
                if (value)
                {
                    throw new NotSupportedException();
                }
            }
        }

        private volatile bool _running;

        public bool Running => _running;

        private delegate bool SupervisorCallHandler(int imm);
        private SupervisorCallHandler _svcHandler;

        public NceExecutionContext(ExceptionCallbacks exceptionCallbacks)
            : this(exceptionCallbacks, 0, 0)
        {
        }

        internal NceExecutionContext(ExceptionCallbacks exceptionCallbacks, ulong guestAddressStart, ulong guestAddressEnd)
            : this(exceptionCallbacks, guestAddressStart, guestAddressEnd, nint.Zero)
        {
        }

        internal NceExecutionContext(ExceptionCallbacks exceptionCallbacks, ulong guestAddressStart, ulong guestAddressEnd, nint signalExitPc)
            : this(exceptionCallbacks, guestAddressStart, guestAddressEnd, signalExitPc, null)
        {
        }

        internal NceExecutionContext(ExceptionCallbacks exceptionCallbacks, ulong guestAddressStart, ulong guestAddressEnd,
            nint signalExitPc, MemoryManagerNative snapshotMemory)
        {
            _snapshotMemory = snapshotMemory;
            _svcHandler = OnSupervisorCall;
            nint svcHandlerPtr = Marshal.GetFunctionPointerForDelegate(_svcHandler);

            _context = new NceNativeContext();
            _alternateStackMemory = new MemoryBlock(AlternateStackSize);

            ref NceNativeContext.NativeCtxStorage storage = ref _context.GetStorage();
            storage.SvcCallHandler = svcHandlerPtr;
            storage.InManaged = 1u;
            storage.GuestAddressStart = guestAddressStart;
            storage.GuestAddressEnd = guestAddressEnd;
            storage.SignalExitPc = signalExitPc;
            storage.CtrEl0 = 0x8444c004; // TODO: Get value from host CPU instead of using guest one?

            _running = true;
            _exceptionCallbacks = exceptionCallbacks;
        }

        public void InitializeThreadEntry(ulong entrypoint)
        {
            if (SupportsThreadContext) _context.GetStorage().Pc = entrypoint;
        }

        public bool TryGetThreadContext(out CpuThreadContext context)
        {
            // The HLE caller holds its activity lock + kernel critical section and has
            // confirmed the target is stopped. This lock only protects a query against Dispose.
            lock (_neoDisposeLock)
            {
                context = default;
                if (_neoDisposed || !SupportsThreadContext) return false;
                // Pair the already existing scheduler Unlock fence with the HLE stopped-core
                // observation. Publication is completed before the target leaves its core.
                Thread.MemoryBarrier();
                return NceThreadContextReader.Capture(ref _context.GetStorage(), NceSnapshotCodeMap.ForMemory(_snapshotMemory),
                    ReadSignalFrame, ReadSnapshotStack, out context);
            }
        }

        private unsafe byte[] ReadSignalFrame(ulong address, int size)
        {
            ulong start = (ulong)_alternateStackMemory.Pointer;
            if (size < 0 || address < start || (ulong)size > AlternateStackSize || address - start > AlternateStackSize - (ulong)size) return null;
            return new ReadOnlySpan<byte>((void*)address, size).ToArray();
        }

        private byte[] ReadSnapshotStack(ulong address, int size)
        {
            address &= 0x00ffffffffffffffUL; // TBI: retain the architectural tag in the reported SP.
            if (!_snapshotMemory.IsRangeMapped(address, (ulong)size)) return null;
            byte[] bytes = new byte[size];
            _snapshotMemory.Read(address, bytes); // backing-memory read, without tracking/protection mutation.
            return bytes;
        }

        public ulong GetX(int index) => _context.GetStorage().X[index];
        public void SetX(int index, ulong value) => _context.GetStorage().X[index] = value;

        public V128 GetV(int index) => _context.GetStorage().V[index];
        public void SetV(int index, V128 value) => _context.GetStorage().V[index] = value;

        public bool GetPstateFlag(PState flag)
        {
            if ((uint)flag >= 32u)
            {
                throw new ArgumentException($"Invalid PSTATE flag \"{flag}\" specified.", nameof(flag));
            }

            return (Pstate & (1u << (int)flag)) != 0;
        }

        public void SetPstateFlag(PState flag, bool value)
        {
            if ((uint)flag >= 32u)
            {
                throw new ArgumentException($"Invalid PSTATE flag \"{flag}\" specified.", nameof(flag));
            }

            uint mask = 1u << (int)flag;
            ref uint pstate = ref _context.GetStorage().Pstate;
            pstate = value ? pstate | mask : pstate & ~mask;
        }

        public bool GetFPstateFlag(FPState flag)
        {
            if ((uint)flag >= 32u)
            {
                throw new ArgumentException($"Invalid FP state flag \"{flag}\" specified.", nameof(flag));
            }

            uint mask = 1u << (int)flag;

            if ((mask & (uint)FPCR.Mask) != 0)
            {
                return (Fpcr & mask) != 0;
            }

            if ((mask & (uint)FPSR.Mask) != 0)
            {
                return (Fpsr & mask) != 0;
            }

            if ((mask & FpStateNzcvMask) != 0)
            {
                return (Pstate & mask) != 0;
            }

            throw new ArgumentException($"Unsupported FP state flag \"{flag}\" specified for AArch64 NCE.", nameof(flag));
        }

        public void SetFPstateFlag(FPState flag, bool value)
        {
            if ((uint)flag >= 32u)
            {
                throw new ArgumentException($"Invalid FP state flag \"{flag}\" specified.", nameof(flag));
            }

            uint mask = 1u << (int)flag;

            if ((mask & (uint)FPCR.Mask) != 0)
            {
                ref uint fpcr = ref _context.GetStorage().Fpcr;
                fpcr = value ? fpcr | mask : fpcr & ~mask;
                return;
            }

            if ((mask & (uint)FPSR.Mask) != 0)
            {
                ref uint fpsr = ref _context.GetStorage().Fpsr;
                fpsr = value ? fpsr | mask : fpsr & ~mask;
                return;
            }

            if ((mask & FpStateNzcvMask) != 0)
            {
                ref uint pstate = ref _context.GetStorage().Pstate;
                pstate = value ? pstate | mask : pstate & ~mask;
                return;
            }

            throw new ArgumentException($"Unsupported FP state flag \"{flag}\" specified for AArch64 NCE.", nameof(flag));
        }

        public void SetStartAddress(ulong address)
        {
            ref NceNativeContext.NativeCtxStorage storage = ref _context.GetStorage();
            storage.X[30] = address;
            storage.Pc = address;
            // Install the signal stack before publishing a handle that another core may use.
            // The initial InManaged=1 remains set until the native entry handshake consumes pending work.
            RegisterAlternateStack();
            Volatile.Write(ref storage.HostThreadHandle, NceThreadPal.GetCurrentThreadHandle());
        }

        public void Exit()
        {
            Volatile.Write(ref _context.GetStorage().HostThreadHandle, nint.Zero);

            UnregisterAlternateStack();
        }

        private void RegisterAlternateStack()
        {
            // We need to use an alternate stack to handle the suspend signal,
            // as the guest stack may be in a state that is not suitable for the signal handlers.

            if (_alternateStackInstalled)
            {
                return;
            }

            NativeSignalHandler.InstallUnixAlternateStackForCurrentThread(_alternateStackMemory.GetPointer(0UL, AlternateStackSize), AlternateStackSize);
            _alternateStackInstalled = true;
        }

        private void UnregisterAlternateStack()
        {
            if (!_alternateStackInstalled)
            {
                return;
            }

            NativeSignalHandler.UninstallUnixAlternateStackForCurrentThread();
            _alternateStackInstalled = false;
        }

        public bool OnSupervisorCall(int imm)
        {
            _exceptionCallbacks.SupervisorCallback?.Invoke(this, 0UL, imm);
            return Running;
        }

        public bool OnInterrupt()
        {
            _exceptionCallbacks.InterruptCallback?.Invoke(this);
            bool running = Running;
            return running;
        }

        public void RequestInterrupt()
        {


            lock (_neoDisposeLock)
            {
                if (_neoDisposed)
                {
                    // NEOV2-COSMIC2: a thread ja terminou e o contexto foi liberado; nao ha o que interromper.
                    return;
                }

                RequestInterruptCore();
            }
        }

        private void RequestInterruptCore()
        {
            nint threadHandle = Volatile.Read(ref _context.GetStorage().HostThreadHandle);
            if (threadHandle != nint.Zero)
                {
                    // Bit 0 set means that the thread is currently running managed code.
                    // Bit 1 set means that an interrupt was requested for the thread.
                    // This, we only need to send the suspend signal if the value was 0 (not running managed code,
                    // and no interrupt was requested before).

                    ref uint inManaged = ref _context.GetStorage().InManaged;
                    uint oldValue = Interlocked.Or(ref inManaged, 2);

                    if (oldValue == 0)
                    {
                        NceThreadPal.SuspendThread(threadHandle);
                    }
                }
        }

        public void StopRunning()
        {
            _running = false;
        }

        public void Dispose()
        {


            // NEOV2-COSMIC2: marca como descartado antes de liberar a memoria do contexto,
            // para que um RequestInterrupt concorrente nao leia o ponteiro liberado.
            lock (_neoDisposeLock)
            {
                if (_neoDisposed)
                {
                    return;
                }

                _neoDisposed = true;
            }

            _alternateStackMemory.Dispose();
            _context.Dispose();
        }
    }
}
