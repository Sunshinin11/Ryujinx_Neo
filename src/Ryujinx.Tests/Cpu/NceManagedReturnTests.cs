using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute the actual entry and SVC/counter patches with synthetic pointers.
    // Signal frames, callbacks and reservation loss are modeled; no kernel signal or device.
    [TestFixture]
    public class NceManagedReturnTests
    {
        private const ulong EntryCode = 0x10000, PatchCode = 0x11000, NestedCode = 0x12000;
        private const ulong GuestContinue = 0x18000, HostReturn = 0x18100;
        private const ulong ManagedCallback = 0x19000, InterruptCallback = 0x19100, SignalReturn = 0x19200;
        private const ulong ClearExclusive = 0x1a000, Context = 0x20000;
        private const ulong GuestSp = 0x38000, InitialHostSp = 0x48000, SignalSp = 0x58000;
        private const ulong CounterResult = 0x1020304050607080;
        private const uint SvcId = 0x55, GuestNzcv = 0xa0000000;
        private const int GuestFpcr = 0x800000, GuestFpsr = 8, HostFpcr = 0x400000, HostFpsr = 16;
        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private static readonly Type Pal = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceThreadPal", true);
        private static int ContextSize => Offset("GetPcOffset") + sizeof(ulong);
        private ulong StateAddress => Context + (ulong)Offset("GetInManagedOffset");
        private UnicornAArch64 _uc;
        private readonly ulong[] _guestX = new ulong[31], _hostX = new ulong[31];
        private readonly SimdValue[] _guestQ = new SimdValue[32], _hostQ = new SimdValue[32];
        private byte[] _beforeContext;
        private ulong _hostSp;
        private bool _counter, _claimed;
        private uint _rd;
        private int _calls, _interrupts;

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(0x10000, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x10000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x40000, 0x10000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x50000, 0x10000, MemoryPermission.Read | MemoryPermission.Write);
            byte[] bytes = new byte[ContextSize];
            Array.Fill(bytes, (byte)0x35);
            _uc.MemoryWrite(Context, bytes);
            for (int i = 0; i < 31; i++)
            {
                _guestX[i] = 0x11110000UL + (ulong)i;
                _hostX[i] = 0x55550000UL + (ulong)i;
                _uc.X[i] = _hostX[i];
            }
            _guestX[30] = PatchCode;
            for (int i = 0; i < 31; i++) Store64("GetXOffset", _guestX[i], i);
            for (int i = 0; i < 32; i++)
            {
                _guestQ[i] = new SimdValue(0x22220000UL + (ulong)i, 0x33330000UL + (ulong)i);
                _hostQ[i] = new SimdValue(0x66660000UL + (ulong)i, 0x77770000UL + (ulong)i);
                _uc.Q[i] = _hostQ[i];
                _uc.MemoryWrite(Context + (ulong)Offset("GetVOffset", i), _guestQ[i].ToArray());
            }
            Store64("GetGuestSPOffset", GuestSp);
            Store64("GetSvcCallHandlerOffset", ManagedCallback);
            Store32("GetFpcrOffset", GuestFpcr);
            Store32("GetFpsrOffset", GuestFpsr);
            Store32("GetPstateOffset", unchecked((int)(GuestNzcv | 0x400u)));
            _uc.MemoryWrite32(StateAddress, 1u);
            _uc.MemoryWrite32(ClearExclusive, 0xd5033f5fu);
            _uc.MemoryWrite32(PatchCode, 0xd503201fu);
            _uc.MemoryWrite32(GuestContinue, 0xd503201fu);
            _uc.MemoryWrite32(HostReturn, 0xd503201fu);
            WriteCode(EntryCode, Generate("GenerateThreadStartCode", (nint)InterruptCallback));
            WriteCode(NestedCode, Generate("GenerateSuspendExceptionHandler", (nint)Context, (nint)InterruptCallback));
            _uc.X[0] = Context;
            _uc.SP = InitialHostSp;
            _uc.LR = HostReturn;
            _uc.Fpcr = HostFpcr;
            _uc.Fpsr = HostFpsr;
            _uc.PC = EntryCode;
            int steps = 0;
            while (_uc.PC != PatchCode && steps++ < 2000) _uc.Step();
            Assert.That(_uc.PC, Is.EqualTo(PatchCode), "Real entry must create the host return frame and reach guest.");
            Assert.That(_uc.MemoryRead32(StateAddress), Is.Zero);
            _hostSp = _uc.MemoryRead64(Context + (ulong)Offset("GetHostSPOffset"));
            // Make the stored snapshot stale after real entry, so publishing before claim is observable.
            Store32("GetPstateOffset", unchecked((int)(0xb0000000u | 0x400u)));
            Store32("GetFpcrOffset", 0);
            Store32("GetFpsrOffset", 0);
            Store64("GetGuestSPOffset", 0xdead0000);
            _beforeContext = _uc.MemoryRead(Context, (ulong)ContextSize);
            _claimed = false;
            _calls = _interrupts = 0;
        }

        [TearDown]
        public void Finish() => _uc?.Dispose();

        [TestCase(false, 0u)]
        [TestCase(true, 0u)]
        [TestCase(true, 19u)]
        [TestCase(true, 20u)]
        [TestCase(true, 21u)]
        [TestCase(true, 22u)]
        [TestCase(true, 30u)]
        public void PublishesNativeOnlyWithGuestStackFpAndNzcv(bool counter, uint rd)
        {
            Initialize(counter, rd);
            Run();
            AssertGuestReturn();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingFromPrimaryCallbackRunsOnHost(bool counter)
        {
            Initialize(counter, 20);
            Run(onManaged: () => _uc.MemoryWrite32(StateAddress, 3u));
            Assert.That(_interrupts, Is.EqualTo(1));
            AssertGuestReturn();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RequestOnGuestTailReturnsToHostBeforeCallback(bool counter)
        {
            Initialize(counter, 21);
            bool requested = false;
            Run(afterStep: word =>
            {
                if (_calls > 0 && !requested && _uc.SP != _hostSp)
                {
                    requested = true;
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    _uc.MemoryWrite32(StateAddress, 3u);
                }
            });
            Assert.That(requested, Is.True);
            Assert.That(_interrupts, Is.EqualTo(1));
            AssertGuestReturn();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RequestBetweenTailLoadAndStoreSurvivesRetry(bool counter)
        {
            Initialize(counter, 19);
            bool requested = false;
            Run(beforeStep: word =>
            {
                if (_calls > 0 && !requested && IsStore(word) && _uc.SP != _hostSp)
                {
                    requested = true;
                    _uc.MemoryWrite32(StateAddress, 3u);
                    LoseReservation();
                }
            });
            Assert.That(requested, Is.True);
            Assert.That(_interrupts, Is.EqualTo(1));
            AssertGuestReturn();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedNativePublicationRestoresNzcvOnRetry(bool counter)
        {
            Initialize(counter, 22);
            bool lost = false;
            Run(beforeStep: word =>
            {
                if (_calls > 0 && !lost && IsStore(word) && _uc.SP != _hostSp)
                {
                    lost = true;
                    Assert.That(_uc.Pstate & 0xf0000000u, Is.EqualTo(GuestNzcv));
                    LoseReservation();
                }
            });
            Assert.That(lost, Is.True);
            AssertGuestReturn();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedPendingReloadsGuestTailWithoutLosingResults(bool counter)
        {
            Initialize(counter, 20);
            Run(onManaged: () => _uc.MemoryWrite32(StateAddress, 3u),
                onInterrupt: () => { if (_interrupts == 1) _uc.MemoryWrite32(StateAddress, 3u); });
            Assert.That(_interrupts, Is.EqualTo(2));
            AssertGuestReturn();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SignalOnGuestTailDefersUntilSafeHostCallback(bool counter, bool alreadyPending)
        {
            Initialize(counter, 19);
            bool delivered = false;
            Run(afterStep: word =>
            {
                if (_calls > 0 && !delivered && _uc.SP != _hostSp)
                {
                    delivered = true;
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    if (alreadyPending) _uc.MemoryWrite32(StateAddress, 3u);
                    RunNestedManagedSignal();
                }
            });
            Assert.That(delivered, Is.True);
            Assert.That(_interrupts, Is.EqualTo(1));
            AssertGuestReturn();
        }

        [TestCase(0)] // SVC callback itself returns false.
        [TestCase(1)] // SVC release receives false from interrupt callback.
        [TestCase(2)] // Counter release receives false from interrupt callback.
        public void ExistingStopPolicyReturnsThroughRealHostFrame(int mode)
        {
            Initialize(mode == 2, 20);
            Run(managedResult: mode != 0, interruptResult: false,
                onManaged: mode == 0 ? null : () => _uc.MemoryWrite32(StateAddress, 3u));
            Assert.That(_uc.PC, Is.EqualTo(HostReturn));
            Assert.That(_uc.SP, Is.EqualTo(InitialHostSp));
            Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u), "Preserve the existing managed stop state.");
            Assert.That(_calls, Is.EqualTo(1));
            Assert.That(_interrupts, Is.EqualTo(mode == 0 ? 0 : 1));
            for (int i = 19; i <= 29; i++) Assert.That(_uc.X[i], Is.EqualTo(_hostX[i]), $"host X{i}");
            for (int i = 8; i <= 15; i++) Assert.That(_uc.Q[i].GetUInt64(0), Is.EqualTo(_hostQ[i].GetUInt64(0)), $"host D{i}");
        }

        private void Initialize(bool counter, uint rd)
        {
            _counter = counter;
            _rd = rd;
            uint[] code = counter
                ? Generate("WriteMrsCntpctEl0Patch", rd, (nint)Context, (nint)ManagedCallback, (nint)InterruptCallback)
                : Generate("WriteSvcPatch", SvcId, (nint)Context, (nint)InterruptCallback);
            Assert.That(code[^1], Is.EqualTo(0x14000000u));
            // Model the existing linker replacing the final B(0) with the continuation branch.
            long delta = (long)GuestContinue - (long)(PatchCode + (ulong)(code.Length - 1) * 4);
            code[^1] = 0x14000000u | (uint)((delta >> 2) & 0x03ffffff);
            WriteCode(PatchCode, code);
            _uc.PC = PatchCode;
        }

        private void Run(bool managedResult = true, bool interruptResult = true, Action onManaged = null,
            Action<uint> beforeStep = null, Action<uint> afterStep = null, Action onInterrupt = null)
        {
            for (int steps = 0; steps < 5000; steps++)
            {
                if (_uc.PC == GuestContinue || _uc.PC == HostReturn) return;
                if (_uc.PC == ManagedCallback || _uc.PC == InterruptCallback)
                {
                    Assert.That(_claimed, Is.True);
                    Assert.That(_uc.SP, Is.EqualTo(_hostSp));
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    Assert.That(_uc.MemoryRead32(StateAddress) & 1u, Is.EqualTo(1u));
                    if (_uc.PC == ManagedCallback)
                    {
                        Assert.That(++_calls, Is.EqualTo(1));
                        if (!_counter)
                        {
                            Assert.That(_uc.X[0], Is.EqualTo((ulong)SvcId));
                            for (int i = 0; i < 8; i++) Assert.That(_uc.MemoryRead64(Context + (ulong)Offset("GetXOffset", i)), Is.EqualTo(_guestX[i]));
                        }
                        onManaged?.Invoke();
                        ClobberCallerSaved(_counter ? CounterResult : managedResult ? 1UL : 0UL);
                    }
                    else
                    {
                        Assert.That(++_interrupts, Is.LessThanOrEqualTo(2));
                        onInterrupt?.Invoke();
                        ClobberCallerSaved(interruptResult ? 1UL : 0UL);
                    }
                    _uc.PC = _uc.LR;
                    continue;
                }
                if (!_claimed) AssertBeforeClaim();
                uint word = _uc.MemoryRead32(_uc.PC);
                beforeStep?.Invoke(word);
                _uc.Step();
                if (!_claimed && IsStore(word) && _uc.MemoryRead32(StateAddress) == 1u) _claimed = true;
                if (_calls > 0 && IsStore(word) && _uc.MemoryRead32(StateAddress) == 0u)
                {
                    // Observe the successful publication instruction itself, before any epilogue step.
                    Assert.That(_uc.SP, Is.EqualTo(_uc.MemoryRead64(Context + (ulong)Offset("GetGuestSPOffset"))));
                    Assert.That(_uc.SP, Is.InRange(GuestSp - 0x1000, GuestSp));
                    Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
                    Assert.That(_uc.Pstate & 0xf0000000u, Is.EqualTo(GuestNzcv));
                }
                afterStep?.Invoke(word);
            }
            Assert.Fail("Managed transition exceeded the instruction budget.");
        }

        private void AssertBeforeClaim()
        {
            Assert.That(_uc.SP, Is.InRange(GuestSp - 0x1000, GuestSp));
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
            byte[] expected = (byte[])_beforeContext.Clone();
            BitConverter.GetBytes(_uc.MemoryRead32(StateAddress)).CopyTo(expected, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(expected), "Snapshots must wait for ownership.");
        }

        private void AssertGuestReturn()
        {
            Assert.That(_claimed, Is.True);
            Assert.That(_calls, Is.EqualTo(1));
            Assert.That(_uc.PC, Is.EqualTo(GuestContinue));
            Assert.That(_uc.SP, Is.EqualTo(GuestSp));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.Zero);
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
            Assert.That(_uc.Pstate & 0xf0000000u, Is.EqualTo(GuestNzcv));
            Assert.That(_uc.MemoryRead32(Context + (ulong)Offset("GetPstateOffset")), Is.EqualTo(GuestNzcv | 0x400u));
            for (int i = 0; i < 31; i++) Assert.That(_uc.X[i], Is.EqualTo(_counter && i == _rd ? CounterResult : _guestX[i]), $"guest X{i}");
            for (int i = 0; i < 32; i++) Assert.That(_uc.Q[i], Is.EqualTo(_guestQ[i]), $"guest Q{i}");
        }

        private void RunNestedManagedSignal()
        {
            ulong[] x = new ulong[31];
            SimdValue[] q = new SimdValue[32];
            for (int i = 0; i < x.Length; i++) x[i] = _uc.X[i];
            for (int i = 0; i < q.Length; i++) q[i] = _uc.Q[i];
            ulong pc = _uc.PC, sp = _uc.SP;
            uint pstate = _uc.Pstate;
            int fpcr = _uc.Fpcr, fpsr = _uc.Fpsr;
            byte[] context = _uc.MemoryRead(Context, (ulong)ContextSize);
            ulong frameBytes = InitialHostSp - _hostSp + 0x400;
            byte[] hostFrame = _uc.MemoryRead(_hostSp - 0x400, frameBytes);
            _uc.X[0] = (ulong)(int)Pal.GetProperty("UnixSuspendSignal", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _uc.X[1] = _uc.X[2] = 0xdead0000; // Deferral must not inspect ucontext.
            _uc.SP = SignalSp;
            _uc.LR = SignalReturn;
            _uc.PC = NestedCode;
            int steps = 0;
            while (_uc.PC != SignalReturn && steps++ < 500)
            {
                Assert.That(_uc.PC, Is.Not.EqualTo(InterruptCallback));
                _uc.Step();
            }
            Assert.That(_uc.PC, Is.EqualTo(SignalReturn));
            Assert.That(_uc.SP, Is.EqualTo(SignalSp));
            Assert.That(_uc.MemoryRead(_hostSp - 0x400, frameBytes), Is.EqualTo(hostFrame));
            BitConverter.GetBytes(3u).CopyTo(context, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(context));
            // Synthetic kernel restoration, not validation of Android signal frames or TLS.
            for (int i = 0; i < x.Length; i++) _uc.X[i] = x[i];
            for (int i = 0; i < q.Length; i++) _uc.Q[i] = q[i];
            _uc.Pstate = pstate;
            _uc.Fpcr = fpcr;
            _uc.Fpsr = fpsr;
            _uc.SP = sp;
            _uc.PC = pc;
        }

        private void ClobberCallerSaved(ulong result)
        {
            for (int i = 0; i <= 17; i++) _uc.X[i] = 0xaaaa0000UL + (ulong)i;
            for (int i = 0; i < 32; i++) _uc.Q[i] = new SimdValue(i >= 8 && i <= 15 ? _uc.Q[i].GetUInt64(0) : 0xbbbbUL, 0xccccUL);
            _uc.Pstate = (_uc.Pstate & ~0xf0000000u) | 0xf0000000u;
            _uc.X[0] = result;
        }

        private void LoseReservation()
        {
            ulong pc = _uc.PC;
            _uc.PC = ClearExclusive;
            _uc.Step();
            _uc.PC = pc;
        }
        private void WriteCode(ulong address, uint[] code)
        {
            Assert.That(code.Length * sizeof(uint), Is.LessThan(0x1000));
            _uc.MemoryWrite(address, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
        }
        private static bool IsAcquire(uint word) => (word & 0xfffffc00u) == 0x885ffc00u;
        private static bool IsStore(uint word) => (word & 0xffe0fc00u) == 0x8800fc00u;
        private static int Offset(string name, params object[] args) => (int)NativeContext.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        private void Store32(string name, int value) => _uc.MemoryWrite32(Context + (ulong)Offset(name), value);
        private void Store64(string name, ulong value, params object[] args) => _uc.MemoryWrite64(Context + (ulong)Offset(name, args), value);
        private static uint[] Generate(string name, params object[] args)
        {
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            MethodInfo method = typeof(NcePatcher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null, types, null);
            try { return (uint[])method.Invoke(null, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
