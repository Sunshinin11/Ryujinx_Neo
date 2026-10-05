using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Actual Android production generators on a host CPU emulator. Kernel masks/frames,
    // callbacks and exclusive-reservation loss are synthetic, never device measurements.
    [TestFixture]
    public class NceAndroidSignalTests
    {
        private const ulong Code = 0x10000, Return = 0x18000, Fault = 0x19000, Interrupt = 0x1a000, Clrex = 0x1b000, Prior = 0x1c000;
        private const ulong Context = 0x20000, SignalSp = 0x34000, HostSp = 0x44000, Frame = 0x50000, Info = 0x52000;
        private const ulong GuestStart = 0x60000, GuestEnd = 0x70000, GuestPc = 0x68000, GuestSp = 0x69000;
        private const int GuestFpcr = 0x800000, HostFpcr = 0x400000, GuestFpsr = 8, HostFpsr = 16;
        private static readonly Type ContextType = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private UnicornAArch64 _uc;
        private byte[] _initial, _hostStack;
        private ulong[] _registers;
        private int _faults, _interrupts, _publications, _chains;
        private bool _native, _hostSuspend;
        private int _interruptedFpcr, _interruptedFpsr, _originalFpcr, _originalFpsr;
        private ulong State => Context + (ulong)Offset("GetInManagedOffset");
        private int Size => Offset("GetSignalExitPcOffset") + 8;

        [SetUp]
        public void Prepare()
        {
            _native = _hostSuspend = false; _faults = _interrupts = _publications = _chains = 0;
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x40000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(Frame, 0x3000, MemoryPermission.Read | MemoryPermission.Write);
            byte[] data = new byte[Size];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 11 + 23);
            _uc.MemoryWrite(Context, data);
            Set64("GetGuestAddressStartOffset", GuestStart);
            Set64("GetGuestAddressEndOffset", GuestEnd);
            Set64("GetSignalExitPcOffset", 0x1d000);
            Set64("GetHostX18Offset", 0x11110012);
            Set64("GetHostSPOffset", HostSp);
            Set64("GetGuestSPOffset", GuestSp - 0x100);
            Set32("GetFpcrOffset", GuestFpcr);
            Set32("GetFpsrOffset", GuestFpsr);
            Set32("GetHostFpcrOffset", HostFpcr);
            Set32("GetHostFpsrOffset", HostFpsr);
            _uc.MemoryWrite32(State, 0);
            _uc.MemoryWrite64(Frame + 432, GuestSp);
            _uc.MemoryWrite64(Frame + 440, GuestPc);
            _uc.MemoryWrite64(Frame + 448, 0xa0000000UL);
            _uc.MemoryWrite32(Info + 8, 2);
            _uc.MemoryWrite32(Clrex, 0xd5033f5fu);
            for (int i = 0; i < 31; i++) _uc.X[i] = 0x11110000UL + (ulong)i;
            for (int i = 0; i < 32; i++) _uc.Q[i] = new SimdValue(0x22220000UL + (ulong)i, 0x33330000UL + (ulong)i);
            _uc.X[0] = 11; _uc.X[1] = Info; _uc.X[2] = Frame;
            _uc.SP = SignalSp; _uc.LR = Return; _uc.PC = Code;
            _uc.Fpcr = GuestFpcr; _uc.Fpsr = GuestFpsr;
            _registers = new ulong[31];
            for (int i = 0; i < 31; i++) _registers[i] = _uc.X[i];
            Capture();
        }
        [TearDown] public void Finish() => _uc.Dispose();
        private void Capture()
        {
            _registers[2] = _uc.X[2];
            _originalFpcr = _uc.Fpcr; _originalFpsr = _uc.Fpsr;
            _initial = _uc.MemoryRead(Context, (ulong)Size);
            _hostStack = _uc.MemoryRead(HostSp - 0x800, 0x800);
        }

        [TestCase(0u)] [TestCase(2u)]
        public void NativeFaultClaimsAndPreservesPendingWithoutWaitingForMaskedSignal(uint state)
        {
            _uc.MemoryWrite32(State, state); Capture(); _native = true;
            EmitFault(); Run();
            Assert.That(_faults, Is.EqualTo(1));
            Assert.That(_interrupts, Is.EqualTo(state == 2 ? 1 : 0));
            AssertPublished();
            Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetGuestSPOffset")), Is.EqualTo(GuestSp));
            Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetPcOffset")), Is.EqualTo(GuestPc));
            Assert.That(_uc.MemoryRead32(Context + (ulong)Offset("GetPstateOffset")), Is.EqualTo(0xa0000000u));
        }

        [TestCase(1u, false)] [TestCase(3u, false)]
        [TestCase(1u, true)] [TestCase(3u, true)]
        public void ManagedAndTransitionFaultsChainNativelyWithoutManagedTrackingOrContextWrites(uint state, bool guestFp)
        {
            _uc.MemoryWrite32(State, state);
            _uc.Fpcr = guestFp ? GuestFpcr : HostFpcr;
            _uc.Fpsr = guestFp ? GuestFpsr : HostFpsr;
            _uc.MemoryWrite32(Context + (ulong)Offset("GetFpcrOffset"), 0x1234u); // stale guest state must survive.
            Capture(); EmitFault(); Run();
            Assert.That(_uc.Fpcr, Is.EqualTo(guestFp ? GuestFpcr : HostFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(guestFp ? GuestFpsr : HostFpsr));
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(_initial));
            Assert.That(_interrupts, Is.Zero);
            Assert.That(_publications, Is.Zero);
            Assert.That(_faults, Is.Zero); Assert.That(_chains, Is.EqualTo(1));
        }

        [TestCase(0u, GuestStart - 4)] [TestCase(2u, GuestStart - 4)]
        [TestCase(0u, GuestEnd)] [TestCase(2u, GuestEnd)]
        public void HostPcIsNotGuestEvenWithNativeState(uint state, ulong pc)
        {
            _uc.MemoryWrite32(State, state); _uc.MemoryWrite64(Frame + 440, pc);
            Capture(); EmitFault(); Run();
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(_initial));
            Assert.That(_publications, Is.Zero);
        }

        [TestCase(0)] [TestCase(-1)] [TestCase(8)] [TestCase(9)]
        public void NonMemorySignalDoesNotClaimOrOverwriteGuestState(int code)
        {
            _uc.MemoryWrite32(Info + 8, code); Capture(); EmitFault(); Run();
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(_initial));
            Assert.That(_publications, Is.Zero);
        }

        [TestCase(GuestStart)] [TestCase(GuestEnd - 4)]
        public void GuestReservationBoundsIncludePatchesAndUseExclusiveEnd(ulong pc)
        {
            _uc.MemoryWrite64(Frame + 440, pc); _native = true;
            EmitFault(); Run(); AssertPublished();
        }

        [TestCase(1u)] [TestCase(3u)]
        public void ManagedClassificationDoesNotReadAnUnmappedUcontext(uint state)
        {
            _uc.MemoryWrite32(State, state); _uc.X[2] = 0xdead1000;
            Capture(); EmitFault(); Run();
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(_initial));
        }

        [TestCase(1u)] [TestCase(3u)]
        public void RequestDuringManagedFaultIsKeptForTheInterruptedOwnersRelease(uint state)
        {
            _uc.MemoryWrite32(State, state); Capture(); EmitFault();
            Run(onPrior: () => _uc.MemoryWrite32(State, 3));
            byte[] expected = (byte[])_initial.Clone();
            BitConverter.GetBytes(3u).CopyTo(expected, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(expected));
            Assert.That(_interrupts, Is.Zero);
        }

        [TestCase(0UL)] [TestCase(8UL)] [TestCase(GuestPc)]
        public void MissingThreadContextChainsNativelyEvenForNullAddressInsideTrackedReservation(ulong address)
        {
            Type table = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceThreadTable", true);
            ulong entries = (ulong)(nint)table.GetProperty("EntriesPointer", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _uc.MemoryMap((entries - 8) & ~0xfffUL, 0x2000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryWrite64(entries - 8, 0); // Synthetic empty bounded table; no production table mutation.
            _uc.Fpcr = HostFpcr; _uc.Fpsr = HostFpsr;
            _uc.MemoryWrite64(Info + 16, address); Capture();
            Write("GenerateAndroidWrapperExceptionHandler", (nint)Fault, nint.Zero, (nint)Interrupt, (nint)Prior);
            Run();
            Assert.That(_faults, Is.Zero); Assert.That(_chains, Is.EqualTo(1)); Assert.That(_publications, Is.Zero);
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(_initial));
        }

        [TestCase(false)] [TestCase(true)]
        public void ReturnPublishesOnlyAfterFpAndSignalStackRestoredWithPendingOnTail(bool suspend)
        {
            _native = true; if (suspend) EmitSuspend(); else EmitFault();
            bool injected = false;
            Run(before: word =>
            {
                if (!injected && _uc.MemoryRead32(State) == 1 && IsStore(word) && _uc.SP == SignalSp - 64)
                { _uc.MemoryWrite32(State, 3); LoseReservation(); injected = true; }
            });
            Assert.That(injected, Is.True);
            Assert.That(_interrupts, Is.EqualTo(suspend ? 2 : 1)); AssertPublished();
        }

        [TestCase(false)] [TestCase(true)]
        public void FailedPublicationRetriesOnSignalStackAndRepeatedRequestsAreServiced(bool suspend)
        {
            _native = true; if (suspend) EmitSuspend(); else EmitFault();
            bool invalidated = false;
            Run(before: word =>
            {
                if (!invalidated && IsStore(word) && _uc.SP == SignalSp - 64 && _uc.MemoryRead32(State) == 1)
                { LoseReservation(); invalidated = true; }
            }, onFault: () => _uc.MemoryWrite32(State, 3), onInterrupt: () =>
            {
                if (_interrupts == 1) _uc.MemoryWrite32(State, 3);
            });
            Assert.That(invalidated, Is.True);
            Assert.That(_interrupts, Is.EqualTo(2)); AssertPublished();
        }

        [Test]
        public void RequestBetweenFaultProbeAndClaimIsRetained()
        {
            bool injected = false; _native = true; EmitFault();
            Run(before: word =>
            {
                if (!injected && IsStore(word))
                { _uc.MemoryWrite32(State, 2); LoseReservation(); injected = true; }
            });
            Assert.That(injected, Is.True); Assert.That(_interrupts, Is.EqualTo(1)); AssertPublished();
        }

        [TestCase(1u)] [TestCase(3u)]
        public void SuspendStillDefersManagedDeliveryWithoutReadingFrame(uint state)
        {
            _uc.MemoryWrite32(State, state); _uc.X[2] = 0xdead1000;
            Capture(); EmitSuspend(); Run();
            byte[] expected = (byte[])_initial.Clone();
            BitConverter.GetBytes(3u).CopyTo(expected, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(expected));
            Assert.That(_interrupts, Is.Zero); Assert.That(_publications, Is.Zero);
        }

        [TestCase(0u, false, false)] [TestCase(2u, false, false)]
        [TestCase(0u, true, false)] [TestCase(2u, true, false)]
        [TestCase(0u, false, true)] [TestCase(2u, false, true)]
        [TestCase(0u, true, true)] [TestCase(2u, true, true)]
        public void NativeModeWithHostHelperPcServicesOnSignalStackWithoutReplacingGuestSnapshot(uint state, bool guestFp, bool pending)
        {
            _uc.MemoryWrite32(State, state); _uc.MemoryWrite64(Frame + 440, GuestEnd + 4);
            _uc.Fpcr = _interruptedFpcr = guestFp ? GuestFpcr : HostFpcr;
            _uc.Fpsr = _interruptedFpsr = guestFp ? GuestFpsr : HostFpsr;
            Capture(); _hostSuspend = true; EmitSuspend();
            Run(onInterrupt: () => { if (pending && _interrupts == 1) _uc.MemoryWrite32(State, 3); });
            byte[] expected = (byte[])_initial.Clone();
            BitConverter.GetBytes(0u).CopyTo(expected, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)Size), Is.EqualTo(expected));
            Assert.That(_interrupts, Is.EqualTo(pending ? 2 : 1)); AssertPublished();
            Assert.That(_uc.Fpcr, Is.EqualTo(_interruptedFpcr)); Assert.That(_uc.Fpsr, Is.EqualTo(_interruptedFpsr));
        }

        [TestCase(false)] [TestCase(true)]
        public void FalseSignalCallbackRewritesFrameAndDoesNotRepublishNativeState(bool suspend)
        {
            _native = true; if (suspend) EmitSuspend(); else EmitFault();
            Run(onFault: () => _uc.MemoryWrite32(State, 3), interruptResult: false);
            Assert.That(_interrupts, Is.EqualTo(1)); Assert.That(_publications, Is.Zero);
            Assert.That(_uc.MemoryRead32(State) & 1u, Is.EqualTo(1u));
            Assert.That(_uc.MemoryRead64(Frame + 440), Is.EqualTo(0x1d000UL));
            Assert.That(_uc.MemoryRead64(Frame + 432), Is.EqualTo(HostSp));
            Assert.That(_uc.MemoryRead64(Frame + 184), Is.EqualTo(Context));
        }

        [Test]
        public void RegistrationMaskBlocksOnlyTheSuspendSignal()
        {
            Type registration = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Signal.UnixSignalHandlerRegistration", true);
            ulong mask = (ulong)registration.GetProperty("NceSuspendMask", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.That(mask, Is.EqualTo(1UL << 11)); // Host fixtures run Windows/Linux, Android signal number is 12.
        }

        private void EmitFault() => Write("GenerateAndroidWrapperExceptionHandler", (nint)Fault, (nint)Context, (nint)Interrupt, (nint)Prior);
        private void EmitSuspend()
        {
            _uc.X[0] = 12;
            Write("GenerateAndroidSuspendExceptionHandler", (nint)Context, (nint)Interrupt);
        }
        private void Write(string name, params nint[] args)
        {
            Type[] types = new Type[args.Length]; object[] objects = new object[args.Length];
            for (int i = 0; i < args.Length; i++) { types[i] = typeof(nint); objects[i] = args[i]; }
            uint[] code = (uint[])typeof(NcePatcher).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, types, null).Invoke(null, objects);
            Assert.That(code.Length * 4, Is.LessThan(0x8000));
            _uc.MemoryWrite(Code, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
        }
        private void Run(Action<uint> before = null, Action onFault = null, Action onInterrupt = null, bool interruptResult = true, Action onPrior = null)
        {
            for (int step = 0; step < 4000; step++)
            {
                if (_uc.PC == Return)
                {
                    Assert.That(_uc.SP, Is.EqualTo(SignalSp));
                    for (int i = 19; i <= 30; i++) Assert.That(_uc.X[i], Is.EqualTo(_registers[i]), $"X{i}");
                    Assert.That(_uc.MemoryRead(HostSp - 0x800, 0x800), Is.EqualTo(_hostStack));
                    return;
                }
                if (_uc.PC == Prior)
                {
                    _chains++;
                    Assert.That(_uc.SP, Is.EqualTo(SignalSp));
                    Assert.That(_uc.Fpcr, Is.EqualTo(_originalFpcr)); Assert.That(_uc.Fpsr, Is.EqualTo(_originalFpsr));
                    Assert.That(_uc.X[0], Is.EqualTo(11UL)); Assert.That(_uc.X[1], Is.EqualTo(Info));
                    Assert.That(_uc.X[2], Is.EqualTo(_registers[2]));
                    for (int i = 19; i <= 30; i++) Assert.That(_uc.X[i], Is.EqualTo(_registers[i]), $"Chained X{i}");
                    onPrior?.Invoke();
                    _uc.PC = _uc.LR;
                    continue;
                }
                if (_uc.PC == Fault || _uc.PC == Interrupt)
                {
                    bool fault = _uc.PC == Fault;
                    if (fault) _faults++; else _interrupts++;
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr)); Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    if (_native)
                    {
                        Assert.That(_uc.SP, Is.EqualTo(HostSp));
                        Assert.That(_uc.MemoryRead32(State) & 1u, Is.EqualTo(1u));
                        Assert.That(_uc.X[19], Is.EqualTo(Context));
                    }
                    else
                    {
                        Assert.That(fault, Is.False, "A host/runtime fault must never enter the managed tracking thunk.");
                        Assert.That(_hostSuspend, Is.True);
                        Assert.That(_uc.SP, Is.EqualTo(SignalSp - 80));
                    }
                    if (fault)
                    {
                        Assert.That(_uc.X[0], Is.EqualTo(11UL)); Assert.That(_uc.X[1], Is.EqualTo(Info));
                        onFault?.Invoke();
                    }
                    else onInterrupt?.Invoke();
                    ulong lr = _uc.LR;
                    for (int i = 0; i <= 17; i++) _uc.X[i] = 0xaaaa0000UL + (ulong)i;
                    _uc.X[0] = fault || interruptResult ? 1UL : 0UL; _uc.PC = lr;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC);
                before?.Invoke(word);
                _uc.Step();
                if (IsStore(word) && _uc.MemoryRead32(State) == 0)
                {
                    _publications++;
                    Assert.That(_uc.SP, Is.EqualTo(_hostSuspend ? SignalSp - 80 : SignalSp - 64), "Publication on original signal frame, never active host frame.");
                    Assert.That(_uc.Fpcr, Is.EqualTo(_hostSuspend ? _interruptedFpcr : GuestFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(_hostSuspend ? _interruptedFpsr : GuestFpsr));
                }
            }
            Assert.Fail("Android signal generator exceeded budget (possible managed-spin deadlock).");
        }
        private void AssertPublished() { Assert.That(_publications, Is.EqualTo(1)); Assert.That(_uc.MemoryRead32(State), Is.Zero); }
        private static bool IsStore(uint w) => (w & 0xffe0fc00u) == 0x8800fc00u;
        private void LoseReservation() { ulong pc = _uc.PC; _uc.PC = Clrex; _uc.Step(); _uc.PC = pc; }
        private static int Offset(string method) => (int)ContextType.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        private void Set32(string m, int v) => _uc.MemoryWrite32(Context + (ulong)Offset(m), unchecked((uint)v));
        private void Set64(string m, ulong v) => _uc.MemoryWrite64(Context + (ulong)Offset(m), v);
    }
}
