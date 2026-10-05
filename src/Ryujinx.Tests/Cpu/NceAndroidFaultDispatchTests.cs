using ARMeilleure.Signal;
using NUnit.Framework;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute the complete production Android dispatcher IR on x64 with synthetic Bionic
    // frames. No signal, native kernel fault, device or managed NCE callback ABI is exercised.
    [TestFixture, NonParallelizable]
    public class NceAndroidFaultDispatchTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Dispatch(int signal, nint info, nint frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Tracking(ulong offset, ulong size, ulong write);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Previous(int signal, nint info, nint frame);
        private MemoryBlock _config, _info, _frame, _code, _instruction;
        private Tracking _tracking;
        private Previous _previous;
        private Dispatch _dispatch;
        private int _tracks, _chains;
        private ulong _write, _trackingResult;
        private const ulong Address = 0x1100, EsrOffset = 464;
        private const uint EsrMagic = 0x45535201;

        [SetUp]
        public void Prepare()
        {
            Assert.That(RuntimeInformation.ProcessArchitecture, Is.EqualTo(Architecture.X64), "Run the host fixture on x64.");
            _config = new MemoryBlock(0x1000); _info = new MemoryBlock(0x1000);
            _frame = new MemoryBlock(0x2000); _instruction = new MemoryBlock(0x1000);
            _config.Write(0, new byte[0x1000]); _info.Write(0, new byte[0x1000]); _frame.Write(0, new byte[0x2000]);
            _tracks = _chains = 0; _trackingResult = Address;
            _tracking = (offset, size, write) =>
            {
                _tracks++; Assert.That(offset, Is.EqualTo(0x100UL)); Assert.That(size, Is.EqualTo(1UL));
                _write = write; return _trackingResult;
            };
            _previous = (signal, info, frame) =>
            {
                _chains++; Assert.That(signal, Is.EqualTo(11));
                Assert.That(info, Is.EqualTo(_info.Pointer)); Assert.That(frame, Is.EqualTo(_frame.Pointer));
            };
            Write64(_config, 8, (ulong)Marshal.GetFunctionPointerForDelegate(_previous));
            Write32(_config, 16, 4); Write32(_config, 20, 1);
            Write64(_config, 24, 0x1000); Write64(_config, 32, 0x2000);
            Write64(_config, 40, (ulong)Marshal.GetFunctionPointerForDelegate(_tracking));
            Write32(_info, 8, 2); Write64(_info, 16, Address);
            Write32(_frame, EsrOffset, EsrMagic); Write32(_frame, EsrOffset + 4, 16);
            Write64(_frame, EsrOffset + 8, (0x24UL << 26) | 0x40UL);
            Write64(_frame, 440, (ulong)_instruction.Pointer);
            Write32(_instruction, 0, 0xf9400020u); // ldr x0, [x1]
            Write64(_frame, 184 + 8, Address + 8);
            byte[] bytes = NativeSignalHandlerGenerator.GenerateAndroidFaultDispatcherForTest(_config.Pointer, 28, Architecture.X64);
            _code = new MemoryBlock(((ulong)bytes.Length + 0xfffUL) & ~0xfffUL);
            _code.Write(0, bytes); _code.Reprotect(0, _code.Size, MemoryPermission.ReadAndExecute);
            _dispatch = Marshal.GetDelegateForFunctionPointer<Dispatch>(_code.Pointer);
        }
        [TearDown]
        public void Finish()
        {
            _dispatch = null; _code?.Dispose(); _instruction?.Dispose();
            _frame?.Dispose(); _info?.Dispose(); _config?.Dispose();
            GC.KeepAlive(_tracking); GC.KeepAlive(_previous);
        }

        [TestCase(0x24u, false)] [TestCase(0x24u, true)]
        [TestCase(0x25u, false)] [TestCase(0x25u, true)]
        public void KnownDataAbortsNormalizeReadWriteAndCallTracking(uint ec, bool write)
        {
            Write64(_frame, EsrOffset + 8, ((ulong)ec << 26) | (write ? 0x40UL : 0UL));
            _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.EqualTo(1)); Assert.That(_chains, Is.Zero);
            Assert.That(_write, Is.EqualTo(write ? 1UL : 0UL));
        }
        [TestCase(0x20u)] [TestCase(0x21u)] [TestCase(0x00u)] [TestCase(0x3fu)]
        public void InstructionOrUnknownSyndromeNeverEntersDirtyTracking(uint ec)
        {
            Write64(_frame, EsrOffset + 8, ((ulong)ec << 26) | 0x40UL);
            ExpectChain();
        }
        [TestCase(0)] [TestCase(-1)] [TestCase(-6)] [TestCase(8)] [TestCase(9)]
        public void SoftwareAndMteSignalsDoNotBecomeOrdinaryDirtyFaults(int code)
        {
            Write32(_info, 8, unchecked((uint)code)); ExpectChain();
        }
        [TestCase(1)] [TestCase(2)]
        public void RecognizedMapAndAccessFaultsRetainExistingTrackingDispatch(int code)
        {
            Write32(_info, 8, (uint)code); _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.EqualTo(1)); Assert.That(_chains, Is.Zero);
        }
        [Test] public void MissingEsrUsesPreviousHandler()
        { Write32(_frame, EsrOffset, 0); ExpectChain(); }
        [Test] public void MalformedEsrUsesPreviousHandler()
        { Write32(_frame, EsrOffset + 4, 32); ExpectChain(); }
        [Test] public void UnknownRangeUsesPreviousHandler()
        { Write64(_info, 16, 0x3000); ExpectChain(); }
        [Test] public void UnhandledTrackingNeverReadsPcOrPatchesOriginalRegisters()
        {
            _trackingResult = 0; Write64(_frame, 440, 0xdead0000);
            _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.EqualTo(1)); Assert.That(_chains, Is.EqualTo(1));
            Assert.That(Read64(_frame, 184 + 8), Is.EqualTo(Address + 8));
            Assert.That(Read64(_frame, 440), Is.EqualTo(0xdead0000UL));
        }
        [Test] public void HandledAliasAdjustsOnlyAddressRegisterUsingRealAndroidFrameOffsets()
        {
            _trackingResult = Address + 0x4000;
            _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.EqualTo(1)); Assert.That(_chains, Is.Zero);
            Assert.That(Read64(_frame, 184 + 8), Is.EqualTo(Address + 0x4000 + 8));
        }
        [Test] public void ExposedTbiTagDoesNotHideALegitimateTrackedAddressOrChangeOriginalTag()
        {
            ulong tagged = Address | (0xb4UL << 56);
            Write64(_info, 16, tagged); Write64(_frame, 184 + 8, tagged + 8);
            _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.EqualTo(1)); Assert.That(_chains, Is.Zero);
            Assert.That(Read64(_info, 16), Is.EqualTo(tagged));
            Assert.That(Read64(_frame, 184 + 8), Is.EqualTo(tagged + 8));
        }
        private void ExpectChain()
        {
            _dispatch(11, _info.Pointer, _frame.Pointer);
            Assert.That(_tracks, Is.Zero); Assert.That(_chains, Is.EqualTo(1));
        }
        private static void Write32(MemoryBlock b, ulong p, uint v) => b.Write(p, BitConverter.GetBytes(v));
        private static void Write64(MemoryBlock b, ulong p, ulong v) => b.Write(p, BitConverter.GetBytes(v));
        private static ulong Read64(MemoryBlock b, ulong p) => unchecked((ulong)Marshal.ReadInt64(b.Pointer + checked((nint)p)));
    }
}
