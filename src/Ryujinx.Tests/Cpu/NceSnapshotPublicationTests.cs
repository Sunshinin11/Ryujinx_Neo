using NUnit.Framework;
using Ryujinx.Cpu;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    [TestFixture]
    public class NceSnapshotPublicationTests
    {
        private NceSnapshotTestMachine _m;
        [SetUp] public void Prepare() => _m = new NceSnapshotTestMachine();
        [TearDown] public void Finish() => _m.Dispose();

        [TestCase(0u, false)] [TestCase(2u, false)] [TestCase(0u, true)] [TestCase(2u, true)]
        public void PrimaryAndroidSignalPublishesTheOriginalKernelFrameBeforeTheCallback(uint state, bool suspend)
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin); _m.Uc.MemoryWrite32(_m.State, state);
            Emit(suspend); bool captured = false;
            for (int i = 0; i < 4000; i++)
            {
                ulong pc = _m.Uc.PC;
                if (pc == NceSnapshotTestMachine.Callback || pc == NceSnapshotTestMachine.Interrupt)
                {
                    Assert.That(_m.Uc.MemoryRead32(NceSnapshotTestMachine.Context + (ulong)NceSnapshotTestMachine.Offset("GetSnapshotKindOffset")), Is.EqualTo(1u));
                    Assert.That(_m.Capture(null, out CpuThreadContext snapshot), Is.True);
                    _m.AssertGuest(snapshot, NceSnapshotTestMachine.Origin); captured = true;
                    _m.CompleteInterrupt(); continue;
                }
                if (pc == NceSnapshotTestMachine.Return) break;
                _m.Uc.Step();
            }
            Assert.That(captured, Is.True);
            Assert.That(_m.Uc.SP, Is.EqualTo(NceSnapshotTestMachine.SignalSp));
        }

        [TestCase(1u, false)] [TestCase(3u, false)] [TestCase(1u, true)] [TestCase(3u, true)]
        public void DeferredAndHostFaultRoutesDoNotReplaceTheExistingSource(uint state, bool suspend)
        {
            _m.BuildFrame(NceSnapshotTestMachine.Origin); _m.Uc.MemoryWrite32(_m.State, state);
            _m.Set32("GetSnapshotKindOffset", 4); _m.Set64("GetSnapshotSourceOffset", 0xabcdef);
            Emit(suspend);
            for (int i = 0; i < 3000 && _m.Uc.PC != NceSnapshotTestMachine.Return && _m.Uc.PC != NceSnapshotTestMachine.Prior; i++) _m.Uc.Step();
            Assert.That(_m.Uc.PC == NceSnapshotTestMachine.Return || _m.Uc.PC == NceSnapshotTestMachine.Prior, Is.True);
            Assert.That(_m.Uc.MemoryRead32(NceSnapshotTestMachine.Context + (ulong)NceSnapshotTestMachine.Offset("GetSnapshotKindOffset")), Is.EqualTo(4u));
            Assert.That(_m.Uc.MemoryRead64(NceSnapshotTestMachine.Context + (ulong)NceSnapshotTestMachine.Offset("GetSnapshotSourceOffset")), Is.EqualTo(0xabcdefUL));
        }

        private void Emit(bool suspend)
        {
            uint[] code = suspend
                ? NceSnapshotTestMachine.Emit("GenerateAndroidSuspendExceptionHandler", (nint)NceSnapshotTestMachine.Context, (nint)NceSnapshotTestMachine.Interrupt)
                : NceSnapshotTestMachine.Emit("GenerateAndroidWrapperExceptionHandler", (nint)NceSnapshotTestMachine.Callback,
                    (nint)NceSnapshotTestMachine.Context, (nint)NceSnapshotTestMachine.Interrupt, (nint)NceSnapshotTestMachine.Prior);
            _m.Uc.MemoryWrite(NceSnapshotTestMachine.Handler, MemoryMarshal.AsBytes(code.AsSpan()).ToArray());
            _m.Uc.MemoryWrite32(NceSnapshotTestMachine.Frame + 0x1800 + 8, 2);
            _m.Uc.X[0] = suspend ? 12UL : 11UL;
            _m.Uc.X[1] = NceSnapshotTestMachine.Frame + 0x1800; _m.Uc.X[2] = NceSnapshotTestMachine.Frame;
            _m.Uc.SP = NceSnapshotTestMachine.SignalSp; _m.Uc.LR = NceSnapshotTestMachine.Return; _m.Uc.PC = NceSnapshotTestMachine.Handler;
        }
    }
}
