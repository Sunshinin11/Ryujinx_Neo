using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    [TestFixture]
    public class NceExclusiveProtocolTests
    {
        private const ulong Code = 0x10000;
        private const ulong Return = 0x18000;
        private const ulong Callback = 0x19000;
        private const ulong ClearExclusive = 0x1a000;
        private const ulong Context = 0x20000;
        private const ulong Stack = 0x34000;
        private static readonly Type NativeContext = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceNativeContext", true);
        private static readonly Type Assembler = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.Arm64.Assembler", true);
        private static readonly Type OperandType = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.Arm64.OperandType", true);
        private static readonly int StateOffset = Offset("GetInManagedOffset");
        private static readonly int ContextSize = Offset("GetPcOffset") + sizeof(ulong);
        private UnicornAArch64 _uc;
        private byte[] _before;
        private ulong[] _callee;
        private int _contextReg;

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(Context, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryWrite32(ClearExclusive, 0xd5033f5fu);
            byte[] data = new byte[ContextSize];
            Array.Fill(data, (byte)0x3d);
            _uc.MemoryWrite(Context, data);
            for (int i = 0; i < 31; i++) _uc.X[i] = 0x55550000UL + (ulong)i;
            _uc.SP = Stack;
            _uc.LR = Return;
            _callee = new ulong[11];
            for (int i = 0; i < _callee.Length; i++) _callee[i] = _uc.X[19 + i];
        }

        [TearDown]
        public void Finish() => _uc?.Dispose();

        [TestCase(19, 20, 21)] // SVC / counter, no blacklisted scratch register.
        [TestCase(19, 20, 22)] // Signal handlers: X21 keeps the signal SP.
        [TestCase(20, 21, 22)] // Counter destination X19.
        [TestCase(19, 21, 22)] // Counter destination X20.
        public void AcquireRestoresContextAndPreservesCalleeSavedRegisters(int ctx, int status, int value)
        {
            Initialize(0, 0u, ctx, status, value);
            Assert.That(Run(), Is.Zero);
            AssertFinished(1u, Context);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void ReleaseWithoutPendingRestoresContext(int operation)
        {
            Initialize(operation, 1u);
            Assert.That(Run(), Is.Zero);
            AssertFinished(0u, Context);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void PendingCallbackReceivesContextBaseAndContinues(int operation)
        {
            Initialize(operation, 3u);
            Assert.That(Run(), Is.EqualTo(1));
            AssertFinished(0u, Context);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void FalseCallbackKeepsTheExistingExitPolicy(int operation)
        {
            Initialize(operation, 3u);
            Assert.That(Run(callbackResult: false), Is.EqualTo(1));
            uint state = operation is 2 or 3 ? 1u : 0u;
            ulong result = operation == 2 ? 0UL : operation == 3 ? 0xeeeeUL : Context;
            AssertFinished(state, result); // None/signal still ignore false; this is not an F03 fix.
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void FailedExclusiveStoreRetriesWithoutUsingTheStatusAsAnAddress(int operation)
        {
            Initialize(operation, operation == 0 ? 0u : 3u);
            bool invalidated = false;
            int attempts = 0;
            int callbacks = Run(beforeStep: word =>
            {
                if (!IsStore(word)) return;
                attempts++;
                if (!invalidated)
                {
                    LoseReservation();
                    invalidated = true;
                }
            });
            Assert.That(invalidated, Is.True);
            Assert.That(attempts, Is.GreaterThanOrEqualTo(operation == 0 ? 2 : 3));
            Assert.That(callbacks, Is.EqualTo(operation == 0 ? 0 : 1));
            AssertFinished(operation == 0 ? 1u : 0u, Context);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void RequestsDuringCallbacksAreConsumedWithoutLosingTheContextBase(int operation)
        {
            Initialize(operation, 3u);
            Assert.That(Run(onCallback: count =>
            {
                if (count < 3) _uc.MemoryWrite32(Context + (ulong)StateOffset, 3u);
            }), Is.EqualTo(3));
            AssertFinished(0u, Context);
        }

        [Test]
        public void ConcurrentNativeRequestMakesAcquireWaitUntilPendingIsServiced()
        {
            Initialize(0, 0u);
            bool requested = false;
            int pendingLoads = 0;
            Assert.That(Run(afterStep: word =>
            {
                if (!IsAcquire(word)) return;
                if (!requested)
                {
                    _uc.MemoryWrite32(Context + (ulong)StateOffset, 2u);
                    LoseReservation();
                    requested = true;
                }
                else if (_uc.MemoryRead32(Context + (ulong)StateOffset) == 2u && ++pendingLoads == 3)
                {
                    // Model completion by the existing suspend path, not delivery by a real kernel.
                    _uc.MemoryWrite32(Context + (ulong)StateOffset, 0u);
                }
            }), Is.Zero);
            Assert.That(requested, Is.True);
            Assert.That(pendingLoads, Is.EqualTo(3));
            AssertFinished(1u, Context);
        }

        [TestCase(0, 1, 0, false)]
        [TestCase(0, 1, 0, true)]
        [TestCase(1, 0, 0, false)]
        [TestCase(1, 0, 0, true)]
        public void GeneratorRejectsStatusOverlappingValueOrBase(int value, int address, int status, bool wide)
        {
            object asm = Activator.CreateInstance(Assembler, nonPublic: true);
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                Assembler.GetMethod("Stlxr").Invoke(asm, new[] { Register(value, wide), Register(address, true), Register(status, false) }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [TestCase(1, 19, 20)]
        [TestCase(1, 31, 31)] // SP base and WZR status are distinct architectural roles.
        [TestCase(1, 19, 31)]
        public void GeneratorAcceptsNonOverlappingOperandsAndSpException(int value, int address, int status)
        {
            object asm = Activator.CreateInstance(Assembler, nonPublic: true);
            Assembler.GetMethod("Stlxr").Invoke(asm, new[] { Register(value, true), Register(address, true), Register(status, false) });
            uint[] code = (uint[])Assembler.GetMethod("GetCode").Invoke(asm, null);
            Assert.That(code, Has.Length.EqualTo(1));
            CheckStores(code);
        }

        [Test]
        public void AllProductionProtocolGeneratorsUseNonOverlappingExclusiveOperands()
        {
            int stores = 0;
            stores += CheckStores(Generate("GenerateThreadStartCode", (nint)Callback));
            stores += CheckStores(Generate("GenerateSuspendExceptionHandler", (nint)Context, (nint)Callback));
            // Generate but never execute the production resolver/table code or install a handler.
            stores += CheckStores(Generate("GenerateWrapperExceptionHandler", nint.Zero, (nint)Callback));
            stores += CheckStores(Generate("WriteSvcPatch", 0u));
            for (uint rd = 0; rd < 31; rd++) stores += CheckStores(Generate("WriteMrsCntpctEl0Patch", rd));
            Assert.That(stores, Is.GreaterThan(30));
        }

        private void Initialize(int operation, uint state, int ctx = 19, int status = 20, int value = 22)
        {
            _contextReg = ctx;
            _uc.MemoryWrite32(Context + (ulong)StateOffset, state);
            _before = _uc.MemoryRead(Context, (ulong)ContextSize);
            uint[] code = Generate("GenerateInManagedProtocolForTest", operation, (nint)Context, (nint)Callback, ctx, status, value);
            Assert.That(code.Length * sizeof(uint), Is.LessThan(0x1000));
            Assert.That(CheckStores(code), Is.GreaterThan(0));
            _uc.MemoryWrite(Code, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
            _uc.PC = Code;
        }

        private int Run(bool callbackResult = true, Action<int> onCallback = null, Action<uint> beforeStep = null, Action<uint> afterStep = null)
        {
            int callbacks = 0;
            for (int steps = 0; steps < 2000; steps++)
            {
                if (_uc.PC == Return) return callbacks;
                if (_uc.PC == Callback)
                {
                    Assert.That(_uc.X[_contextReg], Is.EqualTo(Context), "Context must be restored before the ABI callback.");
                    Assert.That(_uc.MemoryRead32(Context + (ulong)StateOffset), Is.EqualTo(1u));
                    callbacks++;
                    onCallback?.Invoke(callbacks);
                    Assert.That(callbacks, Is.LessThanOrEqualTo(3));
                    for (int i = 0; i < 19; i++) _uc.X[i] = 0xaaaa0000UL + (ulong)i; // ABI caller-saved clobber.
                    _uc.X[0] = callbackResult ? 1UL : 0UL;
                    _uc.PC = _uc.LR;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC);
                beforeStep?.Invoke(word);
                _uc.Step();
                afterStep?.Invoke(word);
            }
            Assert.Fail("Protocol did not finish within the instruction budget.");
            return -1;
        }

        private void AssertFinished(uint state, ulong result)
        {
            BitConverter.GetBytes(state).CopyTo(_before, StateOffset);
            Assert.That(_uc.MemoryRead(Context, (ulong)ContextSize), Is.EqualTo(_before));
            Assert.That(_uc.X[0], Is.EqualTo(result));
            Assert.That(_uc.SP, Is.EqualTo(Stack));
            for (int i = 0; i < _callee.Length; i++) Assert.That(_uc.X[19 + i], Is.EqualTo(_callee[i]));
        }

        private void LoseReservation()
        {
            ulong resume = _uc.PC;
            _uc.PC = ClearExclusive;
            _uc.Step();
            _uc.PC = resume;
        }

        private static bool IsAcquire(uint word) => (word & 0xfffffc00u) == 0x885ffc00u;
        private static bool IsStore(uint word) => (word & 0x3fe0fc00u) == 0x0800fc00u;

        private static int CheckStores(uint[] code)
        {
            int count = 0;
            foreach (uint word in code)
            {
                if (!IsStore(word)) continue;
                int status = (int)((word >> 16) & 31);
                int address = (int)((word >> 5) & 31);
                int value = (int)(word & 31);
                Assert.That(status, Is.Not.EqualTo(value));
                Assert.That(address == 31 || address != status, Is.True, $"STLXR 0x{word:X8} overlaps base/status.");
                count++;
            }
            return count;
        }

        private static object Register(int index, bool wide) => typeof(NcePatcher).GetMethod("Gpr", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new[] { (object)index, Enum.Parse(OperandType, wide ? "I64" : "I32") });

        private static int Offset(string name) => (int)NativeContext.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

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
