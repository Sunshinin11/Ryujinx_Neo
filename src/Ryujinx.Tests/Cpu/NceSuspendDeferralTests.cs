using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute the real suspend body with a synthetic resolver/callback; never install/send a signal.
    [TestFixture]
    public class NceSuspendDeferralTests
    {
        private const ulong Code = 0x10000;
        private const ulong Return = 0x18000;
        private const ulong Callback = 0x19000;
        private const ulong ClearExclusive = 0x1a000;
        private const ulong ThreadStart = 0x1b000;
        private const ulong GuestEntry = 0x1c000;
        private const ulong Context = 0x20000;
        private const ulong SignalSp = 0x34000;
        private const ulong HostSp = 0x44000;
        private const ulong Ucontext = 0x50000;
        private const ulong GuestSp = 0x64000;
        private const ulong Unmapped = 0xdead0000;
        private const int HostFpcr = 0x400000;
        private const int HostFpsr = 0x10;
        private const int GuestFpcr = 0x800000;
        private const int GuestFpsr = 0x08;
        private const uint FrameNzcv = 0xa0000000;

        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType(
            "Ryujinx.Cpu.Nce.NceNativeContext", throwOnError: true);
        private static readonly Type Pal = typeof(NcePatcher).Assembly.GetType(
            "Ryujinx.Cpu.Nce.NceThreadPal", throwOnError: true);
        // Pc is the last field. Use the CPU's actual managed offset, not a marshalled layout.
        private static readonly int ContextSize = Offset("GetPcOffset") + sizeof(ulong);
        private static int SuspendSignal => (int)Pal.GetProperty("UnixSuspendSignal", BindingFlags.Public | BindingFlags.Static).GetValue(null);

        private UnicornAArch64 _uc;
        private ulong StateAddress => Context + (ulong)Offset("GetInManagedOffset");

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x40000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(Ucontext, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x60000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            byte[] bytes = new byte[ContextSize];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 0x35);
            _uc.MemoryWrite(Context, bytes);
            Store64("GetHostSPOffset", Unmapped);
            Store64("GetGuestSPOffset", GuestSp);
            Store32("GetHostFpcrOffset", HostFpcr);
            Store32("GetHostFpsrOffset", HostFpsr);
            Store32("GetFpcrOffset", GuestFpcr);
            Store32("GetFpsrOffset", GuestFpsr);
            _uc.MemoryWrite64(Ucontext + 448, (ulong)FrameNzcv);
            _uc.MemoryWrite32(ClearExclusive, 0xd5033f5fu);
            _uc.MemoryWrite32(GuestEntry, 0x14000000u); // Loop without SVC.
            uint[] code = Generate("GenerateSuspendExceptionHandler", (nint)Context, (nint)Callback);
            Assert.That(code.Length * sizeof(uint), Is.LessThan(0x1000));
            _uc.MemoryWrite(Code, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
        }

        [TearDown]
        public void Finish() => _uc?.Dispose();

        [TestCase(1u)]
        [TestCase(3u)]
        public void ManagedDeliveryOnlySetsPendingAndNeverUsesHostSpOrUcontext(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            byte[] before = _uc.MemoryRead(Context, (ulong)ContextSize);
            InitializeCall(SuspendSignal, Unmapped, SignalSp);
            ulong[] registers = SaveX();
            SimdValue[] vectors = SaveQ();
            Assert.That(Run(), Is.Zero);
            AssertContextExceptPending(before, state | 2u);
            Assert.That(_uc.SP, Is.EqualTo(SignalSp));
            Assert.That(SaveX(), Is.EqualTo(registers));
            Assert.That(SaveQ(), Is.EqualTo(vectors));
            Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        public void WrongSignalReturnsWithoutChangingContextOrSettingPending(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            byte[] before = _uc.MemoryRead(Context, (ulong)ContextSize);
            InitializeCall(SuspendSignal + 1, Unmapped, SignalSp);
            Assert.That(Run(), Is.Zero);
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(before));
            Assert.That(_uc.SP, Is.EqualTo(SignalSp));
        }

        [TestCase(0u)]
        [TestCase(2u)]
        public void NativeStatesStillUseExistingCallbackAndRelease(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            Store64("GetHostSPOffset", HostSp);
            InitializeCall(SuspendSignal, Ucontext, SignalSp);
            _uc.Fpcr = GuestFpcr;
            _uc.Fpsr = GuestFpsr;
            Assert.That(Run(checkNativeCallback: true), Is.EqualTo(1));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.Zero);
            Assert.That(_uc.SP, Is.EqualTo(SignalSp));
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
            if (OperatingSystem.IsLinux())
                Assert.That(_uc.MemoryRead32(Context + (ulong)Offset("GetPstateOffset")), Is.EqualTo(FrameNzcv));
        }

        [TestCase(1u)]
        [TestCase(3u)]
        public void ConcurrentRequestInvalidatesReservationAndDeferralRetries(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            InitializeCall(SuspendSignal, Unmapped, SignalSp);
            bool injected = false;
            int stores = 0;
            Assert.That(Run(beforeStep: word => { if (IsStore(word)) stores++; }, afterStep: word =>
            {
                if (!injected && IsAcquire(word))
                {
                    _uc.MemoryWrite32(StateAddress, state | 2u);
                    LoseReservation();
                    injected = true;
                }
            }), Is.Zero);
            Assert.That(injected, Is.True);
            Assert.That(stores, Is.GreaterThanOrEqualTo(2));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(state | 2u));
        }

        [TestCase(1u)]
        [TestCase(3u)]
        public void FailedExclusiveStoreDoesNotReturnBeforeRetrySucceeds(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            InitializeCall(SuspendSignal, Unmapped, SignalSp);
            bool invalidated = false;
            int stores = 0;
            Assert.That(Run(beforeStep: word =>
            {
                if (IsStore(word))
                {
                    stores++;
                    if (!invalidated) { LoseReservation(); invalidated = true; }
                }
            }), Is.Zero);
            Assert.That(invalidated, Is.True);
            Assert.That(stores, Is.GreaterThanOrEqualTo(2));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(state | 2u));
        }

        [TestCase(1u)]
        [TestCase(3u)]
        public void RepeatedDelayedDeliveriesCoalesceWithoutCallingManagedAgain(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            for (int repeat = 0; repeat < 8; repeat++)
            {
                byte[] before = _uc.MemoryRead(Context, (ulong)ContextSize);
                InitializeCall(SuspendSignal, Unmapped, SignalSp);
                Assert.That(Run(), Is.Zero);
                AssertContextExceptPending(before, 3u);
            }
        }

        [TestCase(1u)]
        [TestCase(3u)]
        public void ActiveHostStackAboveTheCurrentSignalFrameIsNotReused(uint state)
        {
            _uc.MemoryWrite32(StateAddress, state);
            Store64("GetHostSPOffset", HostSp);
            byte[] active = new byte[0x400];
            Array.Fill(active, (byte)0x4b);
            _uc.MemoryWrite(HostSp - 0x400, active);
            // Model a nested signal frame below an existing host frame.
            InitializeCall(SuspendSignal, Unmapped, HostSp - 0x800);
            Assert.That(Run(), Is.Zero);
            Assert.That(_uc.SP, Is.EqualTo(HostSp - 0x800));
            Assert.That(_uc.MemoryRead(HostSp - 0x400, 0x400), Is.EqualTo(active));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(3u));
        }

        [Test]
        public void InitialEntryR4ConsumesTheRequestDeferredByTheActualSuspendBody()
        {
            _uc.MemoryWrite32(StateAddress, 1u);
            InitializeCall(SuspendSignal, Unmapped, SignalSp);
            Assert.That(Run(), Is.Zero);
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(3u));

            uint[] entry = Generate("GenerateThreadStartCode", (nint)Callback);
            _uc.MemoryWrite(ThreadStart, MemoryMarshal.AsBytes(entry.AsSpan()).ToArray());
            _uc.MemoryWrite64(Context + (ulong)Offset("GetXOffset", 30), GuestEntry);
            _uc.X[0] = Context;
            _uc.LR = Return;
            _uc.SP = SignalSp;
            _uc.PC = ThreadStart;
            int callbacks = 0;
            for (int steps = 0; steps < 2000; steps++)
            {
                if (_uc.PC == Return)
                {
                    Assert.That(callbacks, Is.EqualTo(1));
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    Assert.That(_uc.SP, Is.EqualTo(SignalSp));
                    return;
                }
                Assert.That(_uc.PC, Is.Not.EqualTo(GuestEntry), "The pending stop must be consumed before the no-SVC loop.");
                if (_uc.PC == Callback)
                {
                    callbacks++;
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    Assert.That(_uc.SP, Is.EqualTo(_uc.MemoryRead64(Context + (ulong)Offset("GetHostSPOffset"))));
                    _uc.X[0] = 0; // Same false/stop return consumed by R4's existing initial callback.
                    _uc.PC = _uc.LR;
                }
                else _uc.Step();
            }
            Assert.Fail("Deferred request was not consumed by R4 within the instruction budget.");
        }

        private void InitializeCall(int signal, ulong ucontext, ulong stack)
        {
            for (int i = 0; i < 31; i++) _uc.X[i] = 0x11110000UL + (ulong)i;
            for (int i = 0; i < 32; i++) _uc.Q[i] = new SimdValue(0x22220000UL + (ulong)i, 0x33330000UL + (ulong)i);
            _uc.X[0] = (ulong)signal;
            _uc.X[1] = Unmapped + 0x1000;
            _uc.X[2] = ucontext;
            _uc.LR = Return;
            _uc.SP = stack;
            _uc.Fpcr = HostFpcr;
            _uc.Fpsr = HostFpsr;
            _uc.PC = Code;
        }

        private int Run(bool checkNativeCallback = false, Action<uint> beforeStep = null, Action<uint> afterStep = null)
        {
            int calls = 0;
            for (int steps = 0; steps < 2000; steps++)
            {
                if (_uc.PC == Return) return calls;
                if (_uc.PC == Callback)
                {
                    Assert.That(checkNativeCallback, Is.True, "Managed delivery must defer without reaching the callback.");
                    calls++;
                    Assert.That(calls, Is.EqualTo(1));
                    Assert.That(_uc.SP, Is.EqualTo(HostSp));
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    _uc.X[0] = 1;
                    _uc.PC = _uc.LR;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC);
                beforeStep?.Invoke(word);
                _uc.Step();
                afterStep?.Invoke(word);
            }
            Assert.Fail("Suspend body failed to return within the instruction budget.");
            return -1;
        }

        private void AssertContextExceptPending(byte[] before, uint expectedState)
        {
            BitConverter.GetBytes(expectedState).CopyTo(before, Offset("GetInManagedOffset"));
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(before));
        }

        private ulong[] SaveX()
        {
            ulong[] result = new ulong[31];
            for (int i = 0; i < result.Length; i++) result[i] = _uc.X[i];
            return result;
        }

        private SimdValue[] SaveQ()
        {
            SimdValue[] result = new SimdValue[32];
            for (int i = 0; i < result.Length; i++) result[i] = _uc.Q[i];
            return result;
        }

        private void LoseReservation()
        {
            ulong resume = _uc.PC;
            _uc.PC = ClearExclusive;
            _uc.Step();
            _uc.PC = resume;
        }

        private static bool IsAcquire(uint word) => (word & 0xfffffc00u) == 0x885ffc00u;
        private static bool IsStore(uint word) => (word & 0xffe0fc00u) == 0x8800fc00u;
        private static int Offset(string method, params object[] args) =>
            (int)NativeContext.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);

        private static uint[] Generate(string name, params nint[] pointers)
        {
            Type[] types = new Type[pointers.Length];
            object[] args = new object[pointers.Length];
            for (int i = 0; i < pointers.Length; i++) { types[i] = typeof(nint); args[i] = pointers[i]; }
            MethodInfo method = typeof(NcePatcher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: types, modifiers: null);
            try { return (uint[])method.Invoke(null, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private void Store32(string method, int value) => _uc.MemoryWrite32(Context + (ulong)Offset(method), value);
        private void Store64(string method, ulong value) => _uc.MemoryWrite64(Context + (ulong)Offset(method), value);
    }
}
