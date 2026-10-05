using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Real emitted wrappers; synthetic context, callbacks and nested kernel register restoration.
    // No real signal, TLS lookup, scheduler or device is used.
    [TestFixture]
    public class NceFaultTransitionTests
    {
        private const ulong Code = 0x10000, Return = 0x18000, FaultCallback = 0x19000;
        private const ulong ClearExclusive = 0x1a000, NestedCode = 0x1b000, SuspendCallback = 0x1c000;
        private const ulong Context = 0x20000, SignalSp = 0x34000, HostSp = 0x44000;
        private const ulong Ucontext = 0x50000, GuestSp = 0x64000, Unmapped = 0xdead0000;
        private const int GuestFpcr = 0x800000, HostFpcr = 0x400000, GuestFpsr = 8, HostFpsr = 16;
        private const uint FrameNzcv = 0xa0000000;
        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private static readonly Type Pal = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceThreadPal", true);
        private static int ContextSize => Offset("GetPcOffset") + sizeof(ulong);
        private ulong StateAddress => Context + (ulong)Offset("GetInManagedOffset");
        private UnicornAArch64 _uc;
        private ulong[] _initialX;
        private byte[] _initialContext;
        private bool _claimed;
        private int _faultCalls, _suspendCalls;

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x40000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(Ucontext, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            byte[] bytes = new byte[ContextSize];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 0x35);
            _uc.MemoryWrite(Context, bytes);
            Store64("GetHostSPOffset", HostSp);
            Store64("GetGuestSPOffset", GuestSp);
            Store32("GetHostFpcrOffset", HostFpcr);
            Store32("GetHostFpsrOffset", HostFpsr);
            Store32("GetFpcrOffset", GuestFpcr);
            Store32("GetFpsrOffset", GuestFpsr);
            _uc.MemoryWrite32(StateAddress, 0u);
            _uc.MemoryWrite64(Ucontext + 432, GuestSp);
            _uc.MemoryWrite64(Ucontext + 440, 0x12345678UL);
            _uc.MemoryWrite64(Ucontext + 448, FrameNzcv);
            _uc.MemoryWrite32(ClearExclusive, 0xd5033f5fu);
            WriteCode(Code, Generate("GenerateWrapperExceptionHandler", (nint)Unmapped,
                (nint)FaultCallback, (nint)Context, (nint)SuspendCallback));
            WriteCode(NestedCode, Generate("GenerateSuspendExceptionHandler", (nint)Context, (nint)SuspendCallback));
            for (int i = 0; i < 31; i++) _uc.X[i] = 0x11110000UL + (ulong)i;
            for (int i = 0; i < 32; i++) _uc.Q[i] = new SimdValue(0x22220000UL + (ulong)i, 0x33330000UL + (ulong)i);
            _uc.X[0] = 11;
            _uc.X[1] = Unmapped + 0x1000;
            _uc.X[2] = Ucontext;
            _uc.LR = Return;
            _uc.SP = SignalSp;
            _uc.Fpcr = GuestFpcr;
            _uc.Fpsr = GuestFpsr;
            _uc.PC = Code;
            _initialX = SaveX();
            _initialContext = _uc.MemoryRead(Context, (ulong)ContextSize);
            _claimed = false;
            _faultCalls = _suspendCalls = 0;
        }

        [TearDown]
        public void Finish() => _uc?.Dispose();

        [Test]
        public void ClaimsBeforeSnapshotFpChangeAndHostStackUse()
        {
            Run();
            Assert.That(_faultCalls, Is.EqualTo(1));
            Assert.That(_suspendCalls, Is.Zero);
            AssertReturned();
        }

        [Test]
        public void PendingNativeRequestWaitsOnSignalStackUntilServiced()
        {
            _uc.MemoryWrite32(StateAddress, 2u);
            int waits = 0;
            Run(afterStep: word =>
            {
                if (!_claimed && IsAcquire(word) && ++waits == 4)
                    _uc.MemoryWrite32(StateAddress, 0u); // Model completion by the existing suspend handler.
            });
            Assert.That(waits, Is.GreaterThanOrEqualTo(4));
            AssertReturned();
        }

        [Test]
        public void FailedStoreRetriesBeforeAnyGuestOrHostTransition()
        {
            int attempts = 0;
            Run(beforeStep: word =>
            {
                if (!_claimed && IsStore(word) && ++attempts == 1) LoseReservation();
            });
            Assert.That(attempts, Is.GreaterThanOrEqualTo(2));
            AssertReturned();
        }

        [Test]
        public void RequestBetweenLoadAndClaimMustBeServicedBeforeTransition()
        {
            bool injected = false;
            int waits = 0;
            Run(afterStep: word =>
            {
                if (!_claimed && IsAcquire(word))
                {
                    if (!injected)
                    {
                        _uc.MemoryWrite32(StateAddress, 2u);
                        LoseReservation();
                        injected = true;
                    }
                    else if (++waits == 4) _uc.MemoryWrite32(StateAddress, 0u);
                }
            });
            Assert.That(injected, Is.True);
            Assert.That(waits, Is.GreaterThanOrEqualTo(4));
            AssertReturned();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReleaseConsumesPendingAndKeepsExistingNoneReturnPolicy(bool callbackResult)
        {
            Run(onFault: () => _uc.MemoryWrite32(StateAddress, 3u), suspendResult: callbackResult);
            Assert.That(_suspendCalls, Is.EqualTo(1));
            AssertReturned(); // None intentionally still ignores false; R7 does not fix signal-stop policy.
        }

        [TestCase(false, 1u)]
        [TestCase(false, 3u)]
        [TestCase(true, 1u)]
        [TestCase(true, 3u)]
        public void ActualR5DefersNestedDeliveryBeforeOrAfterHostStackSwitch(bool afterStackSwitch, uint state)
        {
            bool delivered = false;
            Action deliver = () =>
            {
                _uc.MemoryWrite32(StateAddress, state);
                RunNestedManagedSignal();
                delivered = true;
            };
            Run(beforeStep: word =>
            {
                // ctx has been restored to its base after the successful R6 claim.
                if (!afterStackSwitch && !delivered && _claimed && _uc.X[19] == Context) deliver();
            }, onFault: () => { if (afterStackSwitch) deliver(); });
            Assert.That(delivered, Is.True);
            Assert.That(_suspendCalls, Is.EqualTo(1));
            AssertReturned();
        }

        private void Run(Action<uint> beforeStep = null, Action<uint> afterStep = null,
            Action onFault = null, bool suspendResult = true)
        {
            for (int steps = 0; steps < 3000; steps++)
            {
                if (_uc.PC == Return) return;
                if (_uc.PC == FaultCallback)
                {
                    Assert.That(++_faultCalls, Is.EqualTo(1));
                    Assert.That(_claimed, Is.True);
                    Assert.That(_uc.SP, Is.EqualTo(HostSp));
                    Assert.That(_uc.MemoryRead32(StateAddress) & 1u, Is.EqualTo(1u));
                    Assert.That(_uc.X[19], Is.EqualTo(Context));
                    Assert.That(_uc.X[0], Is.EqualTo(11UL));
                    Assert.That(_uc.X[1], Is.EqualTo(Unmapped + 0x1000));
                    Assert.That(_uc.X[2], Is.EqualTo(Ucontext));
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    if (OperatingSystem.IsLinux())
                    {
                        Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetGuestSPOffset")), Is.EqualTo(GuestSp));
                        Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetPcOffset")), Is.EqualTo(0x12345678UL));
                        Assert.That(_uc.MemoryRead32(Context + (ulong)Offset("GetPstateOffset")), Is.EqualTo(FrameNzcv));
                    }
                    onFault?.Invoke();
                    ClobberCallerSaved(1);
                    _uc.PC = _uc.LR;
                    continue;
                }
                if (_uc.PC == SuspendCallback)
                {
                    _suspendCalls++;
                    Assert.That(_uc.SP, Is.EqualTo(HostSp));
                    Assert.That(_uc.X[19], Is.EqualTo(Context));
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
                    ClobberCallerSaved(suspendResult ? 1UL : 0UL);
                    _uc.PC = _uc.LR;
                    continue;
                }
                if (!_claimed) AssertBeforeClaim();
                uint word = _uc.MemoryRead32(_uc.PC);
                beforeStep?.Invoke(word);
                _uc.Step();
                if (!_claimed && IsStore(word) && _uc.MemoryRead32(StateAddress) == 1u) _claimed = true;
                afterStep?.Invoke(word);
            }
            Assert.Fail("Fault wrapper exceeded its instruction budget.");
        }

        private void AssertBeforeClaim()
        {
            Assert.That(_uc.SP, Is.InRange(SignalSp - 0x100, SignalSp), "Must remain on the signal stack while acquiring.");
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
            byte[] expected = (byte[])_initialContext.Clone();
            BitConverter.GetBytes(_uc.MemoryRead32(StateAddress)).CopyTo(expected, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(expected), "No guest snapshot/FP mutation before ownership.");
        }

        private void RunNestedManagedSignal()
        {
            ulong[] registers = SaveX();
            SimdValue[] vectors = new SimdValue[32];
            for (int i = 0; i < vectors.Length; i++) vectors[i] = _uc.Q[i];
            ulong pc = _uc.PC, sp = _uc.SP;
            int fpcr = _uc.Fpcr, fpsr = _uc.Fpsr;
            uint pstate = _uc.Pstate;
            // Synthetic signal-return restoration; this explicitly does not test a real Linux/Bionic frame.
            byte[] contextBefore = _uc.MemoryRead(Context, (ulong)ContextSize);
            byte[] activeStack = _uc.MemoryRead(HostSp - 0x400, 0x400);
            int signal = (int)Pal.GetProperty("UnixSuspendSignal", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _uc.X[0] = (ulong)signal;
            _uc.X[1] = Unmapped;
            _uc.X[2] = Unmapped; // Managed deferral must not read ucontext.
            _uc.SP = sp == HostSp ? HostSp - 0x800 : SignalSp - 0x800;
            ulong nestedSp = _uc.SP;
            _uc.LR = Return;
            _uc.PC = NestedCode;
            int steps = 0;
            while (_uc.PC != Return && steps++ < 500)
            {
                Assert.That(_uc.PC, Is.Not.EqualTo(SuspendCallback), "Managed delivery must defer.");
                _uc.Step();
            }
            Assert.That(_uc.PC, Is.EqualTo(Return));
            Assert.That(_uc.SP, Is.EqualTo(nestedSp));
            Assert.That(_uc.MemoryRead(HostSp - 0x400, 0x400), Is.EqualTo(activeStack));
            BitConverter.GetBytes(3u).CopyTo(contextBefore, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(contextBefore));
            // Restore interrupted register state as the kernel would, including NZCV.
            for (int i = 0; i < registers.Length; i++) _uc.X[i] = registers[i];
            for (int i = 0; i < vectors.Length; i++) _uc.Q[i] = vectors[i];
            _uc.Fpcr = fpcr;
            _uc.Fpsr = fpsr;
            _uc.Pstate = pstate;
            _uc.SP = sp;
            _uc.PC = pc;
        }

        private void AssertReturned()
        {
            Assert.That(_claimed, Is.True);
            Assert.That(_faultCalls, Is.EqualTo(1));
            Assert.That(_uc.SP, Is.EqualTo(SignalSp));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.Zero);
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
            for (int i = 19; i <= 30; i++) Assert.That(_uc.X[i], Is.EqualTo(_initialX[i]), $"X{i}");
        }

        private void ClobberCallerSaved(ulong result)
        {
            // Preserve Android's reserved X18, callee-saved GPR and D8-D15.
            for (int i = 0; i <= 17; i++) _uc.X[i] = 0x77770000UL + (ulong)i;
            for (int i = 0; i < 32; i++)
                if (i < 8 || i > 15) _uc.Q[i] = new SimdValue(0x88880000UL + (ulong)i, 0x99990000UL + (ulong)i);
            _uc.X[0] = result;
        }

        private ulong[] SaveX()
        {
            ulong[] values = new ulong[31];
            for (int i = 0; i < values.Length; i++) values[i] = _uc.X[i];
            return values;
        }

        private void LoseReservation()
        {
            ulong resume = _uc.PC;
            _uc.PC = ClearExclusive;
            _uc.Step();
            _uc.PC = resume;
        }

        private void WriteCode(ulong address, uint[] code)
        {
            Assert.That(code.Length * sizeof(uint), Is.LessThan(0x1000));
            _uc.MemoryWrite(address, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
        }

        private static bool IsAcquire(uint word) => (word & 0xfffffc00u) == 0x885ffc00u;
        private static bool IsStore(uint word) => (word & 0xffe0fc00u) == 0x8800fc00u;
        private static int Offset(string method) => (int)NativeContext.GetMethod(method,
            BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        private void Store32(string method, int value) => _uc.MemoryWrite32(Context + (ulong)Offset(method), value);
        private void Store64(string method, ulong value) => _uc.MemoryWrite64(Context + (ulong)Offset(method), value);

        private static uint[] Generate(string name, params nint[] pointers)
        {
            Type[] types = new Type[pointers.Length];
            object[] args = new object[pointers.Length];
            for (int i = 0; i < pointers.Length; i++) { types[i] = typeof(nint); args[i] = pointers[i]; }
            MethodInfo method = typeof(NcePatcher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static,
                null, types, null);
            try { return (uint[])method.Invoke(null, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
