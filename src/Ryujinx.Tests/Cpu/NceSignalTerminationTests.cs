using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute real Android entry/handlers/exit in Unicorn. Signal delivery and sigreturn
    // are explicitly simulated; this does not validate the real kernel or NativeAOT GC.
    [TestFixture]
    public class NceSignalTerminationTests
    {
        private const ulong Entry = 0x10000, Handler = 0x12000, Exit = 0x16000;
        private const ulong HostReturn = 0x18000, Fault = 0x1a000, Interrupt = 0x1b000;
        private const ulong Prior = 0x1c000, SigReturn = 0x1d000, Clrex = 0x1e000;
        private const ulong Context = 0x20000, SignalSp = 0x34000, HostSp = 0x44000;
        private const ulong Frame = 0x50000, Info = 0x52000, GuestPc = 0x60000, GuestSp = 0x74000;
        private const int HostFpcr = 0x400000, HostFpsr = 16, GuestFpcr = 0x800000, GuestFpsr = 8;
        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private UnicornAArch64 _uc;
        private readonly ulong[] _host = new ulong[31];
        private readonly SimdValue[] _hostVectors = new SimdValue[32];
        private byte[] _originalFrame;
        private int _interrupts, _faults;
        private ulong State => Context + (ulong)Offset("GetInManagedOffset");
        private ulong SavedHostSp => _uc.MemoryRead64(Context + (ulong)Offset("GetHostSPOffset"));

        [SetUp]
        public void Prepare()
        {
            _interrupts = _faults = 0;
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Entry, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x40000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(Frame, 0x3000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(GuestPc, 0x1000, MemoryPermission.All);
            _uc.MemoryMap(0x70000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            for (int i = 0; i < 31; i++)
            {
                _host[i] = 0x11110000UL + (ulong)i;
                _uc.X[i] = _host[i];
                Set64("GetXOffset", 0x22220000UL + (ulong)i, i);
            }
            for (int i = 0; i < 32; i++)
            {
                _hostVectors[i] = new SimdValue(0x33330000UL + (ulong)i, 0x44440000UL + (ulong)i);
                _uc.Q[i] = _hostVectors[i];
                _uc.MemoryWrite(Context + (ulong)Offset("GetVOffset", i), new SimdValue(0x55550000UL + (ulong)i, 0x66660000UL + (ulong)i).ToArray());
            }
            _host[30] = HostReturn; _uc.LR = HostReturn;
            Set64("GetXOffset", GuestPc, 30); Set64("GetGuestSPOffset", GuestSp);
            Set64("GetGuestAddressStartOffset", GuestPc); Set64("GetGuestAddressEndOffset", GuestPc + 0x1000);
            Set64("GetSignalExitPcOffset", Exit);
            Set32("GetFpcrOffset", GuestFpcr); Set32("GetFpsrOffset", GuestFpsr);
            Set32("GetPstateOffset", unchecked((int)0xa0000000));
            _uc.MemoryWrite32(State, 1); _uc.MemoryWrite32(GuestPc, 0x14000000); // B . ; no SVC exit.
            _uc.MemoryWrite32(Clrex, 0xd5033f5f);
            Emit(Entry, "GenerateThreadStartCode", (nint)Interrupt, true);
            Emit(Exit, "GenerateAndroidSignalExitCode");
            _uc.X[0] = Context; _uc.SP = HostSp; _uc.PC = Entry;
            _uc.Fpcr = HostFpcr; _uc.Fpsr = HostFpsr;
            RunUntil(GuestPc);
            Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetHostX18Offset")), Is.EqualTo(_host[18]));
            Assert.That(_uc.SP, Is.EqualTo(GuestSp)); Assert.That(_uc.MemoryRead32(State), Is.Zero);
        }
        [TearDown] public void Finish() => _uc.Dispose();

        [TestCase(0u, false)] [TestCase(2u, false)]
        [TestCase(0u, true)] [TestCase(2u, true)]
        public void NativeSuspendFalseLeavesTheNoSvcLoopThroughKernelFrameAndHostEpilogue(uint state, bool helperPc)
        {
            _uc.MemoryWrite32(State, state);
            if (helperPc) _uc.PC = Entry + 4;
            Deliver(suspend: true);
            RunHandler(stopOnInterrupt: 1);
            Assert.That(_interrupts, Is.EqualTo(1)); Assert.That(_faults, Is.Zero);
            AssertStoppedFrame(); RestoreKernelFrame(); RunUntil(HostReturn); AssertHost();
        }

        [TestCase(0u, false)] [TestCase(2u, false)]
        [TestCase(0u, true)] [TestCase(2u, true)]
        public void PendingFaultInterruptFalseExitsWithoutResumingTheFaultingInstruction(uint state, bool stopOnSecond)
        {
            _uc.MemoryWrite32(State, state); Deliver(suspend: false);
            RunHandler(stopOnInterrupt: stopOnSecond ? 2 : 1, requestDuringFault: state == 0, repeatRequest: stopOnSecond);
            Assert.That(_faults, Is.EqualTo(1)); Assert.That(_interrupts, Is.EqualTo(stopOnSecond ? 2 : 1));
            AssertStoppedFrame(); RestoreKernelFrame(); RunUntil(HostReturn); AssertHost();
        }

        [TestCase(false)] [TestCase(true)]
        public void ASecondSuspendRequestCanStopAfterTheFirstCallbackContinues(bool helperPc)
        {
            if (helperPc) _uc.PC = Entry + 4;
            Deliver(suspend: true); RunHandler(stopOnInterrupt: 2, repeatRequest: true);
            Assert.That(_interrupts, Is.EqualTo(2));
            AssertStoppedFrame(); RestoreKernelFrame(); RunUntil(HostReturn); AssertHost();
        }

        [TestCase(false)] [TestCase(true)]
        public void ContinuingSignalsLeaveTheWholeKernelFrameUnchanged(bool suspend)
        {
            _uc.MemoryWrite32(State, suspend ? 0u : 2u); Deliver(suspend);
            RunHandler(stopOnInterrupt: int.MaxValue);
            Assert.That(_uc.MemoryRead(Frame, 0x1800), Is.EqualTo(_originalFrame));
            Assert.That(_uc.MemoryRead32(State), Is.Zero);
            RestoreKernelFrame(); Assert.That(_uc.PC, Is.EqualTo(GuestPc)); Assert.That(_uc.SP, Is.EqualTo(GuestSp));
        }

        [TestCase(1u)] [TestCase(3u)]
        public void DelayedSuspendAtTheExitTrampolineDefersWithoutCallingManagedCode(uint state)
        {
            Deliver(suspend: true); RunHandler(stopOnInterrupt: 1); RestoreKernelFrame();
            _uc.MemoryWrite32(State, state);
            int calls = _interrupts;
            Deliver(suspend: true); RunHandler(stopOnInterrupt: 1);
            Assert.That(_interrupts, Is.EqualTo(calls)); Assert.That(_uc.MemoryRead32(State), Is.EqualTo(3u));
            Assert.That(_uc.MemoryRead(Frame, 0x1800), Is.EqualTo(_originalFrame));
            RestoreKernelFrame(); RunUntil(HostReturn); AssertHost();
        }

        [TestCase(false)] [TestCase(true)]
        public void ARequestAtStopPreparationCannotPublishNativeStateAgain(bool suspend)
        {
            _uc.MemoryWrite32(State, suspend ? 0u : 2u); Deliver(suspend);
            bool injected = false;
            RunHandler(stopOnInterrupt: 1, beforeStep: word =>
            {
                // Inject after the false callback, when the PC field is being written last.
                if (!injected && _interrupts == 1 && _uc.MemoryRead64(Frame + 432) == SavedHostSp)
                { _uc.MemoryWrite32(State, 3); injected = true; }
            });
            Assert.That(injected, Is.True); Assert.That(_uc.MemoryRead32(State), Is.EqualTo(3u));
            AssertStoppedFrame(); RestoreKernelFrame(); RunUntil(HostReturn); AssertHost();
        }

        private void Deliver(bool suspend)
        {
            for (int i = 0; i < 31; i++) _uc.MemoryWrite64(Frame + 184 + (ulong)i * 8, _uc.X[i]);
            _uc.MemoryWrite64(Frame + 432, _uc.SP); _uc.MemoryWrite64(Frame + 440, _uc.PC);
            _uc.MemoryWrite64(Frame + 448, _uc.Pstate);
            _uc.MemoryWrite64(Frame + 40, 0x1234); // interrupted mask: handlers must leave it intact.
            _uc.MemoryWrite64(Frame + 16, 0x87654321); // synthetic altstack metadata sentinel.
            _uc.MemoryWrite32(Frame + 464, 0x46508001); _uc.MemoryWrite32(Frame + 468, 528);
            _uc.MemoryWrite32(Frame + 472, unchecked((uint)_uc.Fpsr)); _uc.MemoryWrite32(Frame + 476, unchecked((uint)_uc.Fpcr));
            for (int i = 0; i < 32; i++) _uc.MemoryWrite(Frame + 480 + (ulong)i * 16, _uc.Q[i].ToArray());
            _uc.MemoryWrite32(Frame + 992, 0x45535201); _uc.MemoryWrite32(Frame + 996, 16);
            _uc.MemoryWrite64(Frame + 1000, 0x9200004f);
            _originalFrame = _uc.MemoryRead(Frame, 0x1800);
            _uc.MemoryWrite32(Info + 8, 2);
            _uc.X[0] = suspend ? 12UL : 11UL; _uc.X[1] = Info; _uc.X[2] = Frame;
            _uc.SP = SignalSp; _uc.LR = SigReturn; _uc.PC = Handler;
            if (suspend) Emit(Handler, "GenerateAndroidSuspendExceptionHandler", (nint)Context, (nint)Interrupt);
            else Emit(Handler, "GenerateAndroidWrapperExceptionHandler", (nint)Fault, (nint)Context, (nint)Interrupt, (nint)Prior);
        }

        private void RunHandler(int stopOnInterrupt, bool requestDuringFault = false, bool repeatRequest = false, Action<uint> beforeStep = null)
        {
            for (int i = 0; i < 5000; i++)
            {
                if (_uc.PC == SigReturn) { Assert.That(_uc.SP, Is.EqualTo(SignalSp)); return; }
                Assert.That(_uc.PC, Is.Not.EqualTo(Prior), "A valid guest signal must not enter the host fault chain.");
                Assert.That(_uc.PC, Is.Not.EqualTo(Exit), "The exit epilogue must run only after kernel sigreturn.");
                if (_uc.PC == Fault || _uc.PC == Interrupt)
                {
                    bool fault = _uc.PC == Fault;
                    Assert.That(_uc.MemoryRead32(State) & 1u, Is.EqualTo(1u));
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr)); Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    ulong lr = _uc.LR;
                    if (fault) { _faults++; if (requestDuringFault) _uc.MemoryWrite32(State, 3); }
                    else { _interrupts++; if (repeatRequest && _interrupts < stopOnInterrupt) _uc.MemoryWrite32(State, 3); }
                    for (int r = 0; r <= 17; r++) _uc.X[r] = 0xaaaa0000UL + (ulong)r;
                    _uc.X[0] = fault || _interrupts < stopOnInterrupt ? 1UL : 0UL;
                    _uc.PC = lr;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC); beforeStep?.Invoke(word); _uc.Step();
            }
            Assert.Fail("Signal handler did not return to the kernel within budget.");
        }

        private void AssertStoppedFrame()
        {
            Assert.That(_uc.MemoryRead32(State) & 1u, Is.EqualTo(1u), "A stopped thread must never publish native mode.");
            Assert.That(_uc.MemoryRead64(Frame + 432), Is.EqualTo(SavedHostSp));
            Assert.That(_uc.MemoryRead64(Frame + 440), Is.EqualTo(Exit));
            Assert.That(_uc.MemoryRead64(Frame + 184), Is.EqualTo(Context));
            Assert.That(_uc.MemoryRead64(Frame + 328), Is.EqualTo(_host[18]));
            byte[] expected = (byte[])_originalFrame.Clone();
            BitConverter.GetBytes(SavedHostSp).CopyTo(expected, 432); BitConverter.GetBytes(Exit).CopyTo(expected, 440);
            BitConverter.GetBytes(Context).CopyTo(expected, 184); BitConverter.GetBytes(_host[18]).CopyTo(expected, 328);
            Assert.That(_uc.MemoryRead(Frame, 0x1800), Is.EqualTo(expected), "Only SP/PC/X0/X18 may change: mask, altstack, vectors and optional records must remain intact.");
        }

        private void RestoreKernelFrame()
        {
            for (int r = 0; r < 31; r++) _uc.X[r] = _uc.MemoryRead64(Frame + 184 + (ulong)r * 8);
            _uc.SP = _uc.MemoryRead64(Frame + 432); _uc.PC = _uc.MemoryRead64(Frame + 440);
            _uc.Pstate = (uint)_uc.MemoryRead64(Frame + 448);
            _uc.Fpsr = unchecked((int)_uc.MemoryRead32(Frame + 472)); _uc.Fpcr = unchecked((int)_uc.MemoryRead32(Frame + 476));
            for (int r = 0; r < 32; r++) _uc.Q[r] = new SimdValue(_uc.MemoryRead64(Frame + 480 + (ulong)r * 16), _uc.MemoryRead64(Frame + 488 + (ulong)r * 16));
        }

        private void RunUntil(ulong pc)
        {
            for (int i = 0; i < 2000; i++)
            {
                if (_uc.PC == pc) return;
                Assert.That(_uc.PC, Is.Not.EqualTo(Interrupt)); // Initial state has no request.
                _uc.Step();
            }
            Assert.Fail($"Native code failed to reach 0x{pc:X}; the no-SVC loop must not resume on stop.");
        }
        private void AssertHost()
        {
            Assert.That(_uc.SP, Is.EqualTo(HostSp)); Assert.That(_uc.PC, Is.EqualTo(HostReturn));
            Assert.That(_uc.X[18], Is.EqualTo(_host[18]));
            for (int r = 19; r < 31; r++) Assert.That(_uc.X[r], Is.EqualTo(_host[r]), $"Host X{r}");
            for (int r = 8; r < 16; r++) Assert.That(_uc.Q[r].GetUInt64(0), Is.EqualTo(_hostVectors[r].GetUInt64(0)), $"Host D{r}");
            Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr)); Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
        }
        private void Emit(ulong address, string method, params object[] args)
        {
            Type[] types = Array.ConvertAll(args, a => a.GetType());
            uint[] code = (uint[])typeof(NcePatcher).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static, null, types, null).Invoke(null, args);
            Assert.That(code.Length * 4, Is.LessThan(0x2000));
            _uc.MemoryWrite(address, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
        }
        private static int Offset(string method, params object[] args) => (int)NativeContext.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        private void Set64(string method, ulong value, params object[] args) => _uc.MemoryWrite64(Context + (ulong)Offset(method, args), value);
        private void Set32(string method, int value) => _uc.MemoryWrite32(Context + (ulong)Offset(method), unchecked((uint)value));
    }
}
