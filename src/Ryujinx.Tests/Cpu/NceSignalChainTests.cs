using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Tests.Unicorn;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    [TestFixture]
    public class NceSignalChainTests
    {
        private const ulong Code = 0x10000, Return = 0x18000, Handler = 0x19000, Clrex = 0x1a000;
        private const ulong State = 0x20000, Stack = 0x34000, Frame = 0x40000, Info = 0x42000;
        private const ulong InterruptedMask = 1UL << 5, WrapperMask = InterruptedMask | (1UL << 10) | (1UL << 11);
        private const uint SigInfo = 4, NoDefer = 0x40000000, ResetHand = 0x80000000;
        private UnicornAArch64 _uc;
        private List<int> _syscalls;
        private int _calls, _masks;
        private ulong _currentMask;
        private uint _flags;
        private int _failMask;

        [SetUp]
        public void Prepare()
        {
            _uc = new UnicornAArch64();
            _uc.MemoryMap(Code, 0x10000, MemoryPermission.All);
            _uc.MemoryMap(State, 0x1000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(0x30000, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryMap(Frame, 0x4000, MemoryPermission.Read | MemoryPermission.Write);
            _uc.MemoryWrite32(State, 0);
            _uc.MemoryWrite64(Frame + 40, InterruptedMask);
            _uc.MemoryWrite32(Clrex, 0xd5033f5fu);
            _calls = _masks = _failMask = 0; _currentMask = WrapperMask; _syscalls = new List<int>();
        }
        [TearDown] public void Finish() => _uc.Dispose();
        private void Emit(ulong handler, uint flags = 0, ulong mask = 1UL << 14)
        {
            _flags = flags;
            Type type = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceSignalChain", true);
            uint[] words = (uint[])type.GetMethod("Generate", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { (nint)handler, mask, flags, (nint)State });
            _uc.MemoryWrite(Code, MemoryMarshal.AsBytes(words.AsSpan()).ToArray());
            for (int i = 0; i < 31; i++) _uc.X[i] = 0xabc000UL + (ulong)i;
            _uc.X[0] = 11; _uc.X[1] = Info; _uc.X[2] = Frame;
            _uc.SP = Stack; _uc.LR = Return; _uc.PC = Code;
        }

        [Test] public void IgnoreIsNotCalledOrConvertedToDefault()
        {
            Emit(1); Assert.That(Run(), Is.False);
            Assert.That(_calls, Is.Zero); Assert.That(_syscalls, Is.Empty);
        }
        [Test] public void DefaultRestoresDispositionAndRedeliversToSameThread()
        {
            Emit(0); Assert.That(Run(), Is.True);
            Assert.That(_syscalls, Is.EqualTo(new[] { 134, 135, 172, 178, 131, 94 }));
            Assert.That(_calls, Is.Zero);
        }
        [TestCase(0u)] [TestCase(SigInfo)] [TestCase(NoDefer)] [TestCase(SigInfo | NoDefer)]
        public void CustomHandlerReceivesCorrectArgumentsAndMaskThenWrapperMaskIsRestored(uint flags)
        {
            Emit(Handler, flags); Assert.That(Run(), Is.False);
            Assert.That(_calls, Is.EqualTo(1)); Assert.That(_syscalls, Is.EqualTo(new[] { 135, 135 }));
            Assert.That(_currentMask, Is.EqualTo(WrapperMask));
        }
        [Test] public void ResetHandIsAtomicAndOnlyFirstUnhandledFaultInvokesCustomHandler()
        {
            Emit(Handler, SigInfo | ResetHand); Assert.That(Run(), Is.False);
            Assert.That(_uc.MemoryRead32(State), Is.EqualTo(1u));
            Emit(Handler, SigInfo | ResetHand); Assert.That(Run(), Is.True);
            Assert.That(_calls, Is.EqualTo(1));
        }
        [Test] public void ResetHandRetriesAFailedExclusiveStore()
        {
            Emit(Handler, SigInfo | ResetHand); bool invalidated = false;
            Assert.That(Run(word =>
            {
                if (!invalidated && (word & 0xffe0fc00u) == 0x8800fc00u)
                {
                    ulong pc = _uc.PC; _uc.PC = Clrex; _uc.Step(); _uc.PC = pc; invalidated = true;
                }
            }), Is.False);
            Assert.That(invalidated, Is.True); Assert.That(_calls, Is.EqualTo(1));
        }
        [TestCase(1)] [TestCase(2)]
        public void MaskSyscallFailureNeverCallsOrResumesWithWrongMask(int failedCall)
        {
            Emit(Handler, SigInfo); _failMask = failedCall;
            Assert.That(Run(), Is.True); Assert.That(_calls, Is.EqualTo(failedCall == 1 ? 0 : 1));
        }
        [Test] public void DefaultSigactionFailureUsesExitFallback()
        {
            Emit(0); Assert.That(Run(failAction: true), Is.True);
            Assert.That(_syscalls, Is.EqualTo(new[] { 134, 94 }));
        }
        [Test] public void NullFrameDoesNotCallCustomHandlerWithInventedMask()
        {
            Emit(Handler, SigInfo); _uc.X[2] = 0;
            Assert.That(Run(), Is.True); Assert.That(_calls, Is.Zero);
        }
        [Test] public void MissingResetStateIsRejectedDuringCodeGeneration()
        {
            Type type = typeof(NcePatcher).Assembly.GetType("Ryujinx.Cpu.Nce.NceSignalChain", true);
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                type.GetMethod("Generate", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { (nint)Handler, 0UL, ResetHand, nint.Zero }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        // SVC is intercepted before Unicorn executes it. No real mask/action/kill/exit syscall runs.
        private bool Run(Action<uint> before = null, bool failAction = false)
        {
            ulong[] preserved = new ulong[12];
            for (int i = 19; i <= 30; i++) preserved[i - 19] = _uc.X[i];
            for (int step = 0; step < 3000; step++)
            {
                if (_uc.PC == Return)
                {
                    Assert.That(_uc.SP, Is.EqualTo(Stack));
                    for (int i = 19; i <= 30; i++) Assert.That(_uc.X[i], Is.EqualTo(preserved[i - 19]));
                    return false;
                }
                if (_uc.PC == Handler)
                {
                    _calls++;
                    Assert.That(_uc.X[0], Is.EqualTo(11UL));
                    if ((_flags & SigInfo) != 0)
                    { Assert.That(_uc.X[1], Is.EqualTo(Info)); Assert.That(_uc.X[2], Is.EqualTo(Frame)); }
                    Assert.That(_currentMask, Is.EqualTo(InterruptedMask | (1UL << 14) |
                        ((_flags & NoDefer) == 0 ? 1UL << 10 : 0UL)));
                    // Changes to ucontext belong to the previous handler and must survive chaining.
                    _uc.MemoryWrite64(Frame + 440, 0x12345678UL);
                    ulong lr = _uc.LR;
                    for (int i = 0; i <= 17; i++) _uc.X[i] = 0xdeaf0000UL + (ulong)i;
                    _uc.PC = lr;
                    continue;
                }
                uint word = _uc.MemoryRead32(_uc.PC);
                before?.Invoke(word);
                if (word == 0xd4000001u)
                {
                    int number = (int)_uc.X[8]; _syscalls.Add(number);
                    if (number == 94) { Assert.That(_uc.X[0], Is.EqualTo(139UL)); return true; }
                    ulong result = 0;
                    if (number == 135)
                    {
                        Assert.That(_uc.X[3], Is.EqualTo(8UL));
                        _masks++;
                        if (_masks == _failMask) result = unchecked((ulong)-22L);
                        else
                        {
                            if (_uc.X[2] != 0) _uc.MemoryWrite64(_uc.X[2], _currentMask);
                            ulong mask = _uc.MemoryRead64(_uc.X[1]);
                            _currentMask = _uc.X[0] == 2 ? mask : _currentMask & ~mask;
                        }
                    }
                    else if (number == 134)
                    {
                        Assert.That(_uc.X[0], Is.EqualTo(11UL)); Assert.That(_uc.X[3], Is.EqualTo(8UL));
                        Assert.That(_uc.MemoryRead(_uc.X[1], 32), Is.EqualTo(new byte[32]));
                        if (failAction) result = unchecked((ulong)-1L);
                    }
                    else if (number == 172) result = 42;
                    else if (number == 178) result = 43;
                    else if (number == 131)
                    {
                        Assert.That(_uc.X[0], Is.EqualTo(42UL)); Assert.That(_uc.X[1], Is.EqualTo(43UL));
                        Assert.That(_uc.X[2], Is.EqualTo(11UL)); Assert.That(_currentMask & (1UL << 10), Is.Zero);
                    }
                    else Assert.Fail($"Unexpected syscall {number}.");
                    _uc.X[0] = result; _uc.PC += 4;
                    continue;
                }
                _uc.Step();
            }
            Assert.Fail("Prior-handler dispatcher exceeded budget."); return true;
        }
    }
}
