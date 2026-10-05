using ARMeilleure.State;
using NUnit.Framework;
using Ryujinx.Cpu;
using Ryujinx.Cpu.Nce;
using Ryujinx.Memory;
using Ryujinx.Tests.Unicorn;
using MemoryPermission = Ryujinx.Tests.Unicorn.MemoryPermission;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    [TestFixture]
    public class NceThreadContextSnapshotTests
    {
        private NceSnapshotTestMachine _m;
        [SetUp] public void Prepare() => _m = new NceSnapshotTestMachine();
        [TearDown] public void Finish() => _m.Dispose();

        [Test]
        public void SignalSnapshotReadsAllRegistersWithoutChangingExecutionStorageOrTheFrame()
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin);
            Assert.That(_m.Capture(1, out CpuThreadContext snapshot), Is.True);
            _m.AssertGuest(snapshot, NceSnapshotTestMachine.Origin);
        }

        [TestCase(16)] [TestCase(64)]
        public void FpsimdMayFollowOtherValidRecords(int padding)
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin, padding);
            Assert.That(_m.Capture(1, out CpuThreadContext snapshot), Is.True);
            _m.AssertGuest(snapshot, NceSnapshotTestMachine.Origin);
        }

        [TestCase(0u, 0u)] [TestCase(1u, 0u)] [TestCase(1u, 8u)]
        [TestCase(1u, 17u)] [TestCase(1u, 8192u)] [TestCase(0x46508001u, 16u)]
        public void InvalidOrMissingFpsimdFailsWithoutReturningStaleVectors(uint magic, uint size)
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin);
            _m.Uc.MemoryWrite32(NceSnapshotTestMachine.Frame + 464, magic);
            _m.Uc.MemoryWrite32(NceSnapshotTestMachine.Frame + 468, size);
            Assert.That(_m.Capture(1, out _), Is.False);
        }

        [TestCase(0u)] [TestCase(4u)]
        public void BeforeTheInitialHandoffTheConfiguredGuestContextIsTheSource(uint kind)
        {
            Assert.That(_m.Capture(kind, out CpuThreadContext snapshot), Is.True);
            _m.AssertGuest(snapshot, NceSnapshotTestMachine.Origin);
        }

        [Test]
        public void AnUnknownSourceCannotBeReportedAsACompleteContext()
            => Assert.That(_m.Capture(99, out _), Is.False);

        [TestCase(0, 0)]
        [TestCase(1, 0)] [TestCase(1, 18)] [TestCase(1, 19)] [TestCase(1, 29)] [TestCase(1, 30)]
        [TestCase(2, 0)] [TestCase(2, 18)] [TestCase(2, 19)] [TestCase(2, 29)] [TestCase(2, 30)]
        [TestCase(3, 0)] [TestCase(3, 19)] [TestCase(3, 31)]
        public void EveryNativeTrampolineBoundaryReportsArchitecturalSpPcRegistersAndFlags(int kind, int rd)
        {
            uint[] code = _m.PrepareHelper(kind, rd);
            bool callback = false; int captures = 0;
            for (int i = 0; i < 4000; i++)
            {
                ulong pc = _m.Uc.PC;
                if (pc == NceSnapshotTestMachine.Callback)
                {
                    // The clock getter does not pause in HLE; its result is published only after return.
                    if (kind == 0)
                    {
                        Assert.That(_m.Capture(null, out CpuThreadContext managed), Is.True);
                        _m.AssertGuest(managed, NceSnapshotTestMachine.Origin + 4);
                    }
                    _m.CompleteCallback(kind); callback = true; continue;
                }
                if (pc == NceSnapshotTestMachine.Patch + (ulong)(code.Length - 1) * 4)
                {
                    _m.BuildFrame(pc);
                    Assert.That(_m.Capture(1, out CpuThreadContext last), Is.True);
                    _m.AssertGuest(last, NceSnapshotTestMachine.Origin + 4, kind == 3 ? -1 : kind, rd, completed: kind != 3);
                    break; // Final B is relocated by production Write, not by this fixture.
                }
                if (_m.Uc.MemoryRead32(_m.State) == 0)
                {
                    _m.BuildFrame(pc);
                    Assert.That(_m.Capture(1, out CpuThreadContext signal), Is.True, $"PC {pc:X}");
                    bool semanticDone = callback || kind == 3 && _m.Uc.MemoryRead64(NceSnapshotTestMachine.Context + (ulong)NceSnapshotTestMachine.Offset("GetTpidrEl0Offset")) != NceSnapshotTestMachine.Tls;
                    _m.AssertGuest(signal, NceSnapshotTestMachine.Origin + (semanticDone ? 4UL : 0UL), kind == 3 ? -1 : kind, rd, callback);
                    captures++;
                }
                _m.Uc.Step();
            }
            Assert.That(captures, Is.GreaterThan(3));
            Assert.That(_m.Uc.SP, Is.EqualTo(NceSnapshotTestMachine.GuestSp));
        }

        [Test]
        public void CounterPendingQueryUsesItsResultForTheBlacklistedDestination()
        {
            uint[] code = _m.PrepareHelper(1, 18);
            bool pending = false;
            for (int i = 0; i < 4000; i++)
            {
                if (_m.Uc.PC == NceSnapshotTestMachine.Callback)
                { _m.CompleteCallback(1); _m.Uc.MemoryWrite32(_m.State, 3); continue; }
                if (_m.Uc.PC == NceSnapshotTestMachine.Interrupt)
                {
                    Assert.That(_m.Capture(null, out CpuThreadContext snapshot), Is.True);
                    _m.AssertGuest(snapshot, NceSnapshotTestMachine.Origin + 4, 1, 18, true);
                    pending = true; _m.CompleteInterrupt(); continue;
                }
                if (_m.Uc.PC == NceSnapshotTestMachine.Patch + (ulong)(code.Length - 1) * 4) break;
                _m.Uc.Step();
            }
            Assert.That(pending, Is.True);
        }

        [Test]
        public void UnmappedSavedStackFailsTheQueryWithoutChangingTheNativeContext()
        {
            _m.PrepareHelper(0, 0);
            for (int i = 0; i < 2000 && _m.Uc.PC != NceSnapshotTestMachine.Callback; i++) _m.Uc.Step();
            _m.MissingStack = true;
            Assert.That(_m.Capture(null, out _), Is.False);
        }

        [Test]
        public void HleConversionKeepsTheKernelPstateMaskAndArchitecturalSp()
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin);
            Assert.That(_m.Capture(1, out CpuThreadContext snapshot), Is.True);
            snapshot.Pstate = uint.MaxValue;
            Type thread = typeof(Ryujinx.HLE.Switch).Assembly.GetType("Ryujinx.HLE.HOS.Kernel.Threading.KThread", true);
            object context = thread.GetMethod("ConvertThreadContext", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { snapshot });
            Type t = context.GetType();
            Assert.That(t.GetField("Sp").GetValue(context), Is.EqualTo(NceSnapshotTestMachine.GuestSp));
            Assert.That(t.GetField("Pc").GetValue(context), Is.EqualTo(NceSnapshotTestMachine.Origin));
            Assert.That(t.GetField("Pstate").GetValue(context), Is.EqualTo(0xFF0FFE20u));
        }
    }

    // Shared fixture executes real generated helpers; signal delivery and scheduler ownership are simulated.
    internal sealed class NceSnapshotTestMachine : IDisposable
    {
        internal const ulong Entry = 0x10000, Patch = 0x12000, Handler = 0x16000, Origin = 0x18000;
        internal const ulong Callback = 0x19000, Interrupt = 0x1a000, Prior = 0x1b000, Return = 0x1c000;
        internal const ulong Context = 0x20000, Frame = 0x30000, SignalSp = 0x44000, GuestSp = 0x54000;
        internal const ulong Tick = 0x99887766, Tls = 0x77665544;
        private static readonly Type StorageType = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext+NativeCtxStorage", true);
        private static readonly Type Native = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private static readonly Type MapType = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceSnapshotCodeMap", true);
        internal readonly UnicornAArch64 Uc = new();
        internal readonly object Map;
        internal readonly ulong[] Guest = new ulong[31];
        internal readonly V128[] Vectors = new V128[32];
        internal bool MissingStack;
        internal ulong State => Context + (ulong)Offset("GetInManagedOffset");

        internal NceSnapshotTestMachine()
        {
            Uc.MemoryMap(Entry, 0x10000, MemoryPermission.All);
            Uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            Uc.MemoryMap(Frame, 0x2000, MemoryPermission.Read | MemoryPermission.Write);
            Uc.MemoryMap(0x40000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            Uc.MemoryMap(0x50000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            for (int r = 0; r < 31; r++) { Guest[r] = 0x12340000UL + (ulong)r; Uc.X[r] = Guest[r]; Set64("GetXOffset", Guest[r], r); }
            for (int r = 0; r < 32; r++)
            { Vectors[r] = new V128(0x23450000UL + (ulong)r, 0x34560000UL + (ulong)r); Uc.Q[r] = new SimdValue(Vectors[r].Extract<ulong>(0), Vectors[r].Extract<ulong>(1)); Uc.MemoryWrite(Context + (ulong)Offset("GetVOffset", r), Vectors[r].ToArray()); }
            Set64("GetGuestSPOffset", GuestSp); Set64("GetHostSPOffset", SignalSp);
            Set64("GetPcOffset", Origin); Set64("GetTpidrroEl0Offset", Tls); Set64("GetTpidrEl0Offset", Tls);
            Set64("GetGuestAddressStartOffset", Entry); Set64("GetGuestAddressEndOffset", Entry + 0x10000);
            Set64("GetSvcCallHandlerOffset", Callback); Set64("GetSignalExitPcOffset", Return);
            Set32("GetFpcrOffset", 0x800000); Set32("GetFpsrOffset", 8);
            Set32("GetHostFpcrOffset", 0x400000); Set32("GetHostFpsrOffset", 16);
            Set32("GetPstateOffset", 0xa0000000); Uc.Pstate = 0xa0000000; Uc.Fpcr = 0x800000; Uc.Fpsr = 8;
            Uc.SP = GuestSp; Uc.PC = Origin;
            Map = MapType.GetMethod("ForMemory", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                new object[] { DispatchProxy.Create<IVirtualMemoryManager, NceSnapshotMemoryProxy>() });
        }

        internal uint[] PrepareHelper(int kind, int rd)
        {
            uint instruction; uint[] code;
            if (kind == 0) { instruction = 0xd4000021; code = Emit("WriteSvcPatch", 1u, (nint)Context, (nint)Interrupt, true); }
            else if (kind == 1) { instruction = 0xd53be020u | (uint)rd; code = Emit("WriteMrsCntpctEl0Patch", (uint)rd, (nint)Context, (nint)Callback, (nint)Interrupt, true); }
            else if (kind == 2) { instruction = 0xd53bd060u | (uint)rd; code = Emit("WriteMrsContextRead", (uint)rd, Offset("GetTpidrroEl0Offset"), (nint)Context, true); }
            else { instruction = 0xd51bd040u | (uint)rd; code = Emit("WriteMsrTpidrEl0Patch", (uint)rd, (nint)Context); }
            Uc.MemoryWrite(Patch, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
            MapType.GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Map, new object[] { Patch, Origin, instruction, code });
            Uc.PC = Patch; return code;
        }

        internal void BuildFrame(ulong pc, int padding = 0)
        {
            Uc.MemoryWrite(Frame, new byte[464 + 4096]);
            for (int r = 0; r < 31; r++) Uc.MemoryWrite64(Frame + 184 + (ulong)r * 8, Uc.X[r]);
            Uc.MemoryWrite64(Frame + 432, Uc.SP); Uc.MemoryWrite64(Frame + 440, pc); Uc.MemoryWrite64(Frame + 448, Uc.Pstate);
            if (padding > 0) { Uc.MemoryWrite32(Frame + 464, 0x45535201); Uc.MemoryWrite32(Frame + 468, (uint)padding); }
            ulong fp = Frame + 464 + (ulong)padding;
            Uc.MemoryWrite32(fp, 0x46508001); Uc.MemoryWrite32(fp + 4, 528);
            Uc.MemoryWrite32(fp + 8, (uint)Uc.Fpsr); Uc.MemoryWrite32(fp + 12, (uint)Uc.Fpcr);
            for (int r = 0; r < 32; r++) Uc.MemoryWrite(fp + 16 + (ulong)r * 16, Uc.Q[r].ToArray());
        }

        internal bool Capture(uint? kind, out CpuThreadContext snapshot)
        {
            int size = Marshal.SizeOf(StorageType); byte[] before = Uc.MemoryRead(Context, (ulong)size);
            byte[] frameBefore = Uc.MemoryRead(Frame, 464 + 4096); byte[] stackBefore = Uc.MemoryRead(0x50000, 0x4000);
            nint allocation = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(before, 0, allocation, size); object storage = Marshal.PtrToStructure(allocation, StorageType);
                if (kind.HasValue)
                { StorageType.GetField("SnapshotKind").SetValue(storage, kind.Value); StorageType.GetField("SnapshotSource").SetValue(storage, Frame); }
                Func<ulong, int, byte[]> readFrame = (a, n) => Uc.MemoryRead(a, (ulong)n);
                Func<ulong, int, byte[]> readStack = (a, n) => MissingStack && a >= 0x50000 && a < GuestSp
                    ? null : Uc.MemoryRead(a & 0x00ffffffffffffffUL, (ulong)n);
                object[] args = { storage, Map, readFrame, readStack, null };
                bool valid = (bool)typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceThreadContextReader", true)
                    .GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
                snapshot = (CpuThreadContext)args[4];
                Assert.That(Uc.MemoryRead(Context, (ulong)size), Is.EqualTo(before));
                Assert.That(Uc.MemoryRead(Frame, 464 + 4096), Is.EqualTo(frameBefore));
                Assert.That(Uc.MemoryRead(0x50000, 0x4000), Is.EqualTo(stackBefore));
                return valid;
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }

        internal void CompleteCallback(int kind)
        {
            ulong lr = Uc.LR;
            if (kind == 0) for (int r = 0; r < 8; r++) Set64("GetXOffset", Guest[r], r);
            for (int r = 0; r < 18; r++) Uc.X[r] = 0xdead0000UL + (ulong)r;
            Uc.X[0] = kind == 1 ? Tick : 1UL; Uc.Pstate = 0x60000000;
            for (int r = 0; r < 32; r++) if (r < 8 || r >= 16) Uc.Q[r] = new SimdValue(0xdeadbeef, 0xabcdef);
            Uc.PC = lr;
        }
        internal void CompleteInterrupt() { ulong lr = Uc.LR; Uc.X[0] = 1; Uc.PC = lr; }
        internal void AssertGuest(CpuThreadContext s, ulong pc, int kind = -1, int rd = -1, bool completed = false)
        {
            Assert.That(s.Pc, Is.EqualTo(pc)); Assert.That(s.Registers[31], Is.EqualTo(GuestSp));
            for (int r = 0; r < 31; r++)
            {
                ulong expected = completed && r == rd ? (kind == 1 ? Tick : kind == 2 ? Tls : Guest[r]) : Guest[r];
                Assert.That(s.Registers[r], Is.EqualTo(expected), $"X{r}, PC {Uc.PC:X}");
            }
            for (int r = 0; r < 32; r++) Assert.That(s.Vectors[r], Is.EqualTo(Vectors[r]), $"V{r}, PC {Uc.PC:X}");
            Assert.That(s.Pstate & 0xf0000000, Is.EqualTo(0xa0000000)); Assert.That(s.Fpcr, Is.EqualTo(0x800000u));
            Assert.That(s.Fpsr, Is.EqualTo(8u)); Assert.That(s.TpidrroEl0, Is.EqualTo((long)Tls));
        }
        internal static uint[] Emit(string name, params object[] args)
            => (uint[])typeof(NcePatcher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null, Array.ConvertAll(args, a => a.GetType()), null).Invoke(null, args);
        internal static int Offset(string method, params object[] args) => (int)Native.GetMethod(method).Invoke(null, args);
        internal void Set64(string method, ulong value, params object[] args) => Uc.MemoryWrite64(Context + (ulong)Offset(method, args), value);
        internal void Set32(string method, uint value) => Uc.MemoryWrite32(Context + (ulong)Offset(method), value);
        public void Dispose() => Uc.Dispose();
    }

    public class NceSnapshotMemoryProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
            => targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
