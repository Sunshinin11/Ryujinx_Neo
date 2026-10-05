using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Signal;
using ARMeilleure.Translation;
using NUnit.Framework;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace Ryujinx.Tests.Cpu
{
    // Execute the production parser IR on an x64 test host; never install/send a signal.
    // A guard page immediately after __reserved detects reads outside the ABI's 4 KiB area.
    [TestFixture, NonParallelizable]
    public class Arm64SignalEsrTests
    {
        private const uint EsrMagic = 0x45535201;
        private const uint ExtraMagic = 0x45585401;
        private const uint FpsimdMagic = 0x46508001;
        private const uint FutureMagic = 0x60000001;
        private const int ReservedSize = 4096;
        private const int AuxOffset = 464;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long ReadWriteFlag(nint ucontext);

        private MemoryBlock _code;
        private MemoryBlock _frame;
        private ReadWriteFlag _read;
        private ulong _pageSize;
        private ulong _reservedOffset;
        private nint _ucontext;

        private static byte[] CompileParser(Architecture target)
        {
            EmitterContext context = new();
            Operand result = NativeSignalHandlerGenerator.GenerateLinuxArm64WriteFlag(
                context, context.LoadArgument(OperandType.I64, 0));
            context.Return(result);
            return Compiler.Compile(context.GetControlFlowGraph(), [OperandType.I64], OperandType.I64,
                CompilerOptions.HighCq, target).Code;
        }

        [OneTimeSetUp]
        public void CreateExecutableParser()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                Assert.Ignore("Execute these synthetic-frame tests on a Windows/Linux x64 host.");
            }
            _pageSize = MemoryBlock.GetPageSize();
            Assert.That(_pageSize, Is.GreaterThanOrEqualTo((ulong)ReservedSize));
            byte[] code = CompileParser(Architecture.X64);
            ulong size = ((ulong)code.Length + _pageSize - 1) / _pageSize * _pageSize;
            _code = new MemoryBlock(size);
            _code.Write(0, code);
            _code.Reprotect(0, size, MemoryPermission.ReadAndExecute);
            _read = Marshal.GetDelegateForFunctionPointer<ReadWriteFlag>(_code.Pointer);
        }

        [SetUp]
        public void CreateGuardedFrame()
        {
            _frame = new MemoryBlock(_pageSize * 3);
            _reservedOffset = _pageSize * 2 - ReservedSize;
            _ucontext = _frame.Pointer + checked((nint)(_reservedOffset - AuxOffset));
            _frame.Reprotect(_pageSize * 2, _pageSize, MemoryPermission.None);
        }

        [TearDown]
        public void ReleaseFrame() => _frame?.Dispose();

        [OneTimeTearDown]
        public void ReleaseCode()
        {
            _read = null;
            _code?.Dispose();
        }

        [TestCase(0UL, 0L)]
        [TestCase(0x40UL, 0x40L)]
        [TestCase(0xffffffffffffffbfUL, 0L)]
        [TestCase(ulong.MaxValue, 0x40L)]
        public void NormalFaultKeepsExactlyTheExistingWnrBit(ulong esr, long expected)
        {
            Header(0, FpsimdMagic, 528);
            Esr(528, esr);
            Assert.That(_read(_ucontext), Is.EqualTo(expected));
        }

        [Test]
        public void RecordOrderIsNotAssumed()
        {
            Esr(0, 0x40);
            Assert.That(_read(_ucontext), Is.EqualTo(0x40L));
        }

        [Test]
        public void WellFormedUnknownRecordsAreSkipped()
        {
            Header(0, FutureMagic, 32);
            Header(32, FutureMagic, 16);
            Esr(48, 0x40);
            Assert.That(_read(_ucontext), Is.EqualTo(0x40L));
        }

        [Test]
        public void TerminatorWithoutEsrIsUnknownRatherThanRead()
        {
            Header(0, FpsimdMagic, 528);
            Header(528, 0, 0);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(8u)]
        [TestCase(15u)]
        [TestCase(17u)]
        [TestCase(24u)]
        [TestCase(4112u)]
        [TestCase(0xfffffff0u)]
        [TestCase(uint.MaxValue)]
        public void InvalidGenericSizesCannotLoopOrEscapeTheReservedArea(uint size)
        {
            Header(0, FutureMagic, size);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [TestCase(0u)]
        [TestCase(8u)]
        [TestCase(15u)]
        [TestCase(17u)]
        [TestCase(32u)]
        [TestCase(4096u)]
        public void EsrRequiresItsAbiSizeBeforeReadingPayload(uint size)
        {
            Header(0, EsrMagic, size);
            _frame.Write(_reservedOffset + 8, 0x40UL);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void NullMagicWithNonzeroSizeIsRejected()
        {
            Header(0, 0, 16);
            Esr(16, 0x40);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void EsrAtTheLastCompleteRecordFitsWithoutReadingTheGuardPage()
        {
            Header(0, FutureMagic, 4080);
            Esr(4080, 0x40);
            Assert.That(_read(_ucontext), Is.EqualTo(0x40L));
        }

        [Test]
        public void LastRecordCannotClaimMoreThanTheRemainingSpace()
        {
            Header(0, FutureMagic, 4080);
            Header(4080, FutureMagic, 32);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void FullAreaWithoutTerminatorStopsBeforeTheGuardPage()
        {
            Header(0, FutureMagic, ReservedSize);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void MaximumNumberOfSmallRecordsStillTerminates()
        {
            for (int offset = 0; offset < ReservedSize; offset += 16)
            {
                Header(offset, FutureMagic, 16);
            }
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void ExtraDatapIsNeverDereferencedWhenEsrIsAbsent()
        {
            Header(0, ExtraMagic, 32);
            _frame.Write(_reservedOffset + 8, ulong.MaxValue);
            _frame.Write(_reservedOffset + 16, uint.MaxValue);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void EsrInExtraSpaceIsNotAcceptedAsAnAbiValidRecord()
        {
            Header(0, ExtraMagic, 32);
            _frame.Write(_reservedOffset + 8, (ulong)(_frame.Pointer + (nint)(_reservedOffset + 48)));
            _frame.Write(_reservedOffset + 16, 32u);
            Header(32, 0, 0);
            Esr(48, 0x40);
            Assert.That(_read(_ucontext), Is.EqualTo(-1L));
        }

        [Test]
        public void ValidEsrBeforeExtraKeepsItsResult()
        {
            Esr(0, 0x40);
            Header(16, ExtraMagic, 32);
            _frame.Write(_reservedOffset + 24, ulong.MaxValue);
            Assert.That(_read(_ucontext), Is.EqualTo(0x40L));
        }

        [Test]
        public void NullMisalignedAndWrappingPointersAreRejectedBeforeLoads()
        {
            Assert.That(_read(nint.Zero), Is.EqualTo(-1L));
            Assert.That(_read(_ucontext + 1), Is.EqualTo(-1L));
            Assert.That(_read(unchecked((nint)(ulong.MaxValue - 16))), Is.EqualTo(-1L));
            Assert.That(_read(unchecked((nint)(ulong.MaxValue - 1024))), Is.EqualTo(-1L));
        }

        [Test]
        public void TheSameParserIrAlsoCompilesForArm64()
        {
            byte[] code = CompileParser(Architecture.Arm64);
            Assert.That(code.Length, Is.GreaterThan(0));
            Assert.That(code.Length % 4, Is.Zero);
            TestContext.Out.WriteLine($"ARM64 ESR parser bytes: {code.Length}");
        }

        private void Header(int offset, uint magic, uint size)
        {
            _frame.Write(_reservedOffset + (ulong)offset, magic);
            _frame.Write(_reservedOffset + (ulong)offset + 4, size);
        }

        private void Esr(int offset, ulong value)
        {
            Header(offset, EsrMagic, 16);
            _frame.Write(_reservedOffset + (ulong)offset + 8, value);
        }
    }
}
