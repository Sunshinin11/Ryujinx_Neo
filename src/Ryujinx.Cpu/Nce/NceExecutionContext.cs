using ARMeilleure.State;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Nce
{
    class NceExecutionContext : IExecutionContext
    {
        private const ulong AlternateStackSize = 0x20000;
        private const uint FpStateNzcvMask = 0xf0000000u;

        private readonly NceNativeContext _context;
        private readonly ExceptionCallbacks _exceptionCallbacks;

        internal nint NativeContextPtr => _context.BasePtr;

        private readonly MemoryBlock _alternateStackMemory;
        private bool _alternateStackInstalled;

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


        // Protects the native context against RequestInterrupt racing with Dispose.
        private readonly object _lifecycleLock = new();
        private bool _disposed;

        public NceExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            _svcHandler = OnSupervisorCall;
            nint svcHandlerPtr = Marshal.GetFunctionPointerForDelegate(_svcHandler);

            _context = new NceNativeContext();
            _alternateStackMemory = new MemoryBlock(AlternateStackSize);

            ref NceNativeContext.NativeCtxStorage storage = ref _context.GetStorage();
            storage.SvcCallHandler = svcHandlerPtr;
            storage.InManaged = 1u;
            storage.CtrEl0 = 0x8444c004; // TODO: Get value from host CPU instead of using guest one?

            _running = true;
            _exceptionCallbacks = exceptionCallbacks;
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
            Volatile.Write(ref storage.HostThreadHandle, NceThreadPal.GetCurrentThreadHandle());

            RegisterAlternateStack();
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
            return Running;
        }

        public void RequestInterrupt()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                ref NceNativeContext.NativeCtxStorage storage = ref _context.GetStorage();
                nint threadHandle = storage.HostThreadHandle;

                if (threadHandle != nint.Zero)
                {
                    // Bit 0 set means that the thread is currently running managed code.
                    // Bit 1 set means that an interrupt was requested for the thread.
                    ref uint inManaged = ref storage.InManaged;
                    uint oldValue = Interlocked.Or(ref inManaged, 2);

                    if (oldValue == 0)
                    {
                        NceThreadPal.SuspendThread(threadHandle);
                    }
                }
            }
        }

        public void StopRunning()
        {
            lock (_lifecycleLock)
            {
                _running = false;
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _running = false;
                _context.GetStorage().HostThreadHandle = nint.Zero;
                _disposed = true;

                _alternateStackMemory.Dispose();
                _context.Dispose();
            }
        }
    }
}
