using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute the production ARM64 entry code in Unicorn, never installing a host signal or NCE context.
    [TestFixture]
    public class NceThreadStartTests
    {
        private const ulong Code = 0x10000;
        private const ulong Context = 0x20000;
        private const ulong HostSp = 0x34000;
        private const ulong GuestSp = 0x54000;
        private const ulong GuestEntry = 0x60000;
        private const ulong Interrupt = 0x61000;
        private const ulong HostReturn = 0x62000;
        private const ulong ClearExclusive = 0x63000;
        private const uint HostNzcv = 0x60000000;
        private const uint GuestNzcv = 0xa0000000;
        private const int HostFpcr = 0x400000;
        private const int GuestFpcr = 0x800000;
        private const int HostFpsr = 0x10;
        private const int GuestFpsr = 0x08;

        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType(
            "Ryujinx.Cpu.Nce.NceNativeContext", throwOnError: true);

        private UnicornAArch64 _uc;
        private uint[] _entry;
        private readonly ulong[] _hostX = new ulong[31];
        private readonly SimdValue[] _hostQ = new SimdValue[32];
        private readonly ulong[] _guestX = new ulong[31];
        private readonly SimdValue[] _guestQ = new SimdValue[32];

        private ulong StateAddress => Context + (ulong)Offset("GetInManagedOffset");

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x1000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x50000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(GuestEntry, 0x4000, MemoryPermission.All);
            _entry = GenerateEntry((nint)Interrupt);
            Assert.That(_entry.Length * sizeof(uint), Is.LessThan(0x1000));
            _uc.MemoryWrite(Code, MemoryMarshal.AsBytes(_entry.AsSpan()).ToArray());
            _uc.MemoryWrite32(GuestEntry, 0x14000000u); // B . ; no SVC or later safepoint.
            _uc.MemoryWrite32(Interrupt, 0xd65f03c0u);
            _uc.MemoryWrite32(HostReturn, 0xd503201fu);
            _uc.MemoryWrite32(ClearExclusive, 0xd5033f5fu); // CLREX; force a lost reservation.

            for (int i = 0; i < 31; i++)
            {
                _hostX[i] = 0x11110000UL + (ulong)i;
                _guestX[i] = 0x22220000UL + (ulong)i;
                _uc.X[i] = _hostX[i];
                Store64(Offset("GetXOffset", i), _guestX[i]);
            }
            for (int i = 0; i < 32; i++)
            {
                _hostQ[i] = new SimdValue(0x33330000UL + (ulong)i, 0x44440000UL + (ulong)i);
                _guestQ[i] = new SimdValue(0x55550000UL + (ulong)i, 0x66660000UL + (ulong)i);
                _uc.Q[i] = _hostQ[i];
                _uc.MemoryWrite(Context + (ulong)Offset("GetVOffset", i), _guestQ[i].ToArray());
            }
            _guestX[30] = GuestEntry;
            Store64(Offset("GetXOffset", 30), GuestEntry);
            Store64(Offset("GetGuestSPOffset"), GuestSp);
            Store32(Offset("GetPstateOffset"), GuestNzcv);
            Store32(Offset("GetFpcrOffset"), GuestFpcr);
            Store32(Offset("GetFpsrOffset"), GuestFpsr);
            _uc.MemoryWrite32(StateAddress, 1u);
            _uc.Pstate = (_uc.Pstate & ~0xf0000000u) | HostNzcv;
            _uc.Fpcr = HostFpcr;
            _uc.Fpsr = HostFpsr;
            _uc.SP = HostSp;
            _uc.LR = HostReturn;
            _hostX[30] = HostReturn;
            _uc.X[0] = Context;
            _uc.PC = Code;
        }

        [TearDown]
        public void Finish() => _uc?.Dispose();

        [Test]
        public void NoRequestEntersGuestWithAllRegistersAndFlags()
        {
            Assert.That(Run(false), Is.Zero);
            AssertGuest();
        }

        [Test]
        public void SignalAtNativePublicationCapturesGuestFlagsRatherThanHandshakeFlags()
        {
            bool captured = false;
            Assert.That(Run(false, afterStep: word =>
            {
                if (!captured && IsStoreExclusive(word) && _uc.MemoryRead32(StateAddress) == 0)
                {
                    uint nzcv = _uc.Pstate & 0xf0000000u;
                    Assert.That(nzcv, Is.EqualTo(GuestNzcv));
                    // Model the existing suspend handler's PSTATE snapshot at this exact window.
                    Store32(Offset("GetPstateOffset"), nzcv);
                    captured = true;
                }
            }), Is.Zero);
            Assert.That(captured, Is.True);
            AssertGuest();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingRequestIsHandledBeforeTheNoSvcLoop(bool stop)
        {
            _uc.MemoryWrite32(StateAddress, 3u);
            Assert.That(Run(stop), Is.EqualTo(1));
            if (stop) AssertHostReturn(); else AssertGuest();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RequestRightBeforeTheReleaseLoadIsNotLost(bool stop)
        {
            bool injected = false;
            int calls = Run(stop, beforeStep: word =>
            {
                if (!injected && IsAcquire(word))
                {
                    _uc.MemoryWrite32(StateAddress, 3u);
                    injected = true;
                }
            });
            Assert.That(injected, Is.True);
            Assert.That(calls, Is.EqualTo(1));
            if (stop) AssertHostReturn(); else AssertGuest();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RequestAfterTheExclusiveLoadForcesRetryAndIsHandled(bool stop)
        {
            bool injected = false;
            int calls = Run(stop, afterStep: word =>
            {
                if (!injected && IsAcquire(word))
                {
                    // The sender's successful OR on another core invalidates the reservation.
                    // Unicorn's host MemWrite is not assumed to emulate that core; invalidate explicitly.
                    _uc.MemoryWrite32(StateAddress, 3u);
                    LoseReservation();
                    injected = true;
                }
            });
            Assert.That(injected, Is.True);
            Assert.That(calls, Is.EqualTo(1));
            if (stop) AssertHostReturn(); else AssertGuest();
        }

        [Test]
        public void FailedPendingClaimRetriesBeforeCallingManaged()
        {
            _uc.MemoryWrite32(StateAddress, 3u);
            bool invalidated = false;
            int calls = Run(false, beforeStep: word =>
            {
                if (!invalidated && IsStoreExclusive(word) && (word & 31u) == 30u)
                {
                    LoseReservation();
                    invalidated = true;
                }
            });
            Assert.That(invalidated, Is.True);
            Assert.That(calls, Is.EqualTo(1));
            AssertGuest();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnotherRequestDuringTheInitialCallbackIsHandledAgain(bool stopOnSecond)
        {
            _uc.MemoryWrite32(StateAddress, 3u);
            int calls = Run(stopOnSecond, repeatRequest: true);
            Assert.That(calls, Is.EqualTo(2));
            if (stopOnSecond) AssertHostReturn(); else AssertGuest();
        }

        [Test]
        public void CallbackRegisterAndVectorChangesAreReloadedFromContext()
        {
            _uc.MemoryWrite32(StateAddress, 3u);
            Assert.That(Run(false, updateGuest: true), Is.EqualTo(1));
            AssertGuest();
        }

        private int Run(bool stop, bool repeatRequest = false, bool updateGuest = false,
            Action<uint> beforeStep = null, Action<uint> afterStep = null)
        {
            int calls = 0;
            for (int steps = 0; steps < 2000; steps++)
            {
                if (_uc.PC == GuestEntry || _uc.PC == HostReturn) return calls;
                if (_uc.PC == Interrupt)
                {
                    calls++;
                    Assert.That(calls, Is.LessThanOrEqualTo(2));
                    Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
                    Assert.That(_uc.SP, Is.EqualTo(_uc.MemoryRead64(Context + (ulong)Offset("GetHostSPOffset"))));
                    Assert.That(_uc.X[29], Is.EqualTo(_uc.SP));
                    Assert.That(_uc.X[18], Is.EqualTo(_hostX[18]), "Android platform register must remain host-owned for the callback.");
                    Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
                    Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
                    ulong returnPc = _uc.LR;
                    for (int i = 0; i < 18; i++) _uc.X[i] = 0xdead0000UL + (ulong)i;
                    // AAPCS64: low D8-D15 are preserved, upper halves and other vectors may change.
                    for (int i = 0; i < 32; i++)
                        _uc.Q[i] = new SimdValue(i >= 8 && i <= 15 ? _uc.Q[i].GetUInt64(0) : 0xdeadUL, 0xbeefUL);
                    _uc.Pstate = (_uc.Pstate & ~0xf0000000u) | 0xf0000000u;
                    if (repeatRequest && calls == 1) _uc.MemoryWrite32(StateAddress, 3u);
                    if (updateGuest)
                    {
                        _guestX[2] = 0x123456789UL;
                        _guestX[19] = 0x987654321UL;
                        _guestQ[31] = new SimdValue(0xabcUL, 0xdefUL);
                        Store64(Offset("GetXOffset", 2), _guestX[2]);
                        Store64(Offset("GetXOffset", 19), _guestX[19]);
                        _uc.MemoryWrite(Context + (ulong)Offset("GetVOffset", 31), _guestQ[31].ToArray());
                    }
                    _uc.X[0] = stop && (!repeatRequest || calls == 2) ? 0UL : 1UL;
                    _uc.PC = returnPc;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC);
                beforeStep?.Invoke(word);
                _uc.Step();
                afterStep?.Invoke(word);
            }
            Assert.Fail("Initial handshake did not reach guest or host return within the instruction budget.");
            return -1;
        }

        private void AssertGuest()
        {
            Assert.That(_uc.PC, Is.EqualTo(GuestEntry));
            Assert.That(_uc.SP, Is.EqualTo(GuestSp));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.Zero);
            for (int i = 0; i < 31; i++) Assert.That(_uc.X[i], Is.EqualTo(_guestX[i]), $"guest X{i}");
            for (int i = 0; i < 32; i++) Assert.That(_uc.Q[i], Is.EqualTo(_guestQ[i]), $"guest Q{i}");
            Assert.That(_uc.Pstate & 0xf0000000u, Is.EqualTo(GuestNzcv));
            Assert.That(_uc.Fpcr, Is.EqualTo(GuestFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(GuestFpsr));
        }

        private void AssertHostReturn()
        {
            Assert.That(_uc.PC, Is.EqualTo(HostReturn));
            Assert.That(_uc.SP, Is.EqualTo(HostSp));
            Assert.That(_uc.MemoryRead32(StateAddress), Is.EqualTo(1u));
            for (int i = 19; i < 31; i++) Assert.That(_uc.X[i], Is.EqualTo(_hostX[i]), $"host X{i}");
            Assert.That(_uc.X[18], Is.EqualTo(_hostX[18]));
            for (int i = 8; i < 16; i++) Assert.That(_uc.Q[i].GetUInt64(0), Is.EqualTo(_hostQ[i].GetUInt64(0)), $"host D{i}");
            Assert.That(_uc.Fpcr, Is.EqualTo(HostFpcr));
            Assert.That(_uc.Fpsr, Is.EqualTo(HostFpsr));
        }

        private static int Offset(string method, params object[] args) =>
            (int)NativeContext.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);

        private static uint[] GenerateEntry(nint callback)
        {
            MethodInfo method = typeof(NcePatcher).GetMethod("GenerateThreadStartCode",
                BindingFlags.NonPublic | BindingFlags.Static, binder: null,
                types: new[] { typeof(nint) }, modifiers: null);
            try { return (uint[])method.Invoke(null, new object[] { callback }); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private void LoseReservation()
        {
            ulong resumePc = _uc.PC;
            _uc.PC = ClearExclusive;
            _uc.Step();
            _uc.PC = resumePc;
        }

        private static bool IsAcquire(uint word) => (word & 0xfffffc00u) == 0x885ffc00u;
        private static bool IsStoreExclusive(uint word) => (word & 0xffe0fc00u) == 0x8800fc00u;
        private void Store64(int offset, ulong value) => _uc.MemoryWrite64(Context + (ulong)offset, value);
        private void Store32(int offset, uint value) => _uc.MemoryWrite32(Context + (ulong)offset, value);
        private void Store32(int offset, int value) => _uc.MemoryWrite32(Context + (ulong)offset, value);
    }
}
