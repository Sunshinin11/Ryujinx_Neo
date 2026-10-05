using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Inspect generated instructions without executing ARM64 or opening a guest context.
    // Reflection keeps patch construction details out of the production public API.
    [TestFixture]
    public class NcePatchSafetyTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(0xd53bd05fu)] // MRS XZR, TPIDR_EL0
        [TestCase(0xd53bd07fu)] // MRS XZR, TPIDRRO_EL0
        [TestCase(0xd53be03fu)] // MRS XZR, CNTPCT_EL0
        public void DiscardedSystemReadIsOneInPlaceNop(uint instruction)
        {
            NceCpuCodePatch patch = CreatePatch(instruction);
            Assert.That(patch.Size, Is.Zero);
            Assert.That(Replacements(patch), Is.EqualTo(new[] { (0, 0xd503201fu) }));
        }

        [Test]
        public void CtrDiscardOnlyChangesWhereCtrIsAlreadyEmulated()
        {
            NceCpuCodePatch patch = CreatePatch(0xd53b003fu);
            Assert.That(patch.Size, Is.Zero);
            Assert.That(Replacements(patch).Count, Is.EqualTo(OperatingSystem.IsMacOS() ? 1 : 0));
        }

        [Test]
        public void UnhandledSystemReadIsLeftAlone()
        {
            NceCpuCodePatch patch = CreatePatch(0xd53b421fu); // MRS XZR, NZCV
            Assert.That(patch.Size, Is.Zero);
            Assert.That(Replacements(patch), Is.Empty);
        }

        [TestCase(0xd53bd040u)]
        [TestCase(0xd53bd060u)]
        public void TlsReadToRealRegisterStillHasTrampoline(uint instruction)
        {
            NceCpuCodePatch patch = CreatePatch(instruction);
            Assert.That(patch.Size, Is.GreaterThan(0UL));
            Assert.That(Replacements(patch), Is.Empty);
        }

        [Test]
        public void TlsWriteFromXzrStillStoresZeroThroughTrampoline()
        {
            NceCpuCodePatch patch = CreatePatch(0xd51bd05fu); // MSR TPIDR_EL0, XZR
            Assert.That(patch.Size, Is.GreaterThan(0UL));
            Assert.That(Replacements(patch), Is.Empty);
            List<uint> code = (List<uint>)typeof(NceCpuCodePatch).GetField("_code", Instance).GetValue(patch);
            Assert.That(code.Any(word => (word & 0xffc0001fu) == 0xf900001fu), Is.True,
                "The generated unsigned-immediate STR must keep Rt=31 (XZR).");
        }

        [TestCase(0x10000000UL, 0x10000000UL, 0x14000000u)]
        [TestCase(0x10000000UL, 0x17fffffcUL, 0x15ffffffu)]
        [TestCase(0x10000000UL, 0x08000000UL, 0x16000000u)]
        [TestCase(0x10000000UL, 0x0ffffffcUL, 0x17ffffffu)]
        public void BranchAcceptsExactSignedImm26Bounds(ulong source, ulong target, uint expected)
        {
            Assert.That(EncodeBranch(source, target), Is.EqualTo(expected));
        }

        [TestCase(0x10000000UL, 0x18000000UL)]
        [TestCase(0x10000000UL, 0x07fffffcUL)]
        [TestCase(0UL, 0xfffffffffffffffcUL)]
        [TestCase(0xfffffffffffffffcUL, 0UL)]
        public void BranchRejectsOutOfRangeWithoutSignedAddressWrap(ulong source, ulong target)
        {
            Assert.Throws<NotSupportedException>(() => EncodeBranch(source, target));
        }

        [TestCase(0x1001UL, 0x2000UL)]
        [TestCase(0x1000UL, 0x2002UL)]
        public void BranchRejectsUnalignedAddresses(ulong source, ulong target)
        {
            Assert.Throws<ArgumentException>(() => EncodeBranch(source, target));
        }

        [Test]
        public void AllRelocationsAreCheckedBeforeAnyInPlaceOrBranchWrite()
        {
            NceCpuCodePatch patch = new();
            typeof(NceCpuCodePatch).GetMethod("ReplaceInstruction", Instance).Invoke(patch, new object[] { 0, 0xd503201fu });
            AddBranch(patch, 1); // Valid, but must not be written before checking the next target.
            AddBranch(patch, 0x02000000); // Text target more than 128 MiB from patch.
            // A null writer is an intentional trap: any premature write produces a NullReferenceException.
            Assert.Throws<NotSupportedException>(() => patch.Write(null, 0x1000, 0x2000));
        }

        [Test]
        public void ReturnBranchIsCheckedIndependentlyOfForwardBranch()
        {
            NceCpuCodePatch patch = new();
            AddBranch(patch, 0);
            // Forward delta -128 MiB fits; return delta +128 MiB+4 does not.
            Assert.Throws<NotSupportedException>(() => patch.Write(null, 0x1000, 0x08001000));
        }

        private static NceCpuCodePatch CreatePatch(uint instruction) =>
            NcePatcher.CreatePatch(MemoryMarshal.AsBytes(new uint[] { instruction }.AsSpan()));

        private static List<(int, uint)> Replacements(NceCpuCodePatch patch) =>
            (List<(int, uint)>)typeof(NceCpuCodePatch).GetField("_inPlaceInstructions", Instance).GetValue(patch);

        private static void AddBranch(NceCpuCodePatch patch, int index) =>
            typeof(NceCpuCodePatch).GetMethod("AddCode", Instance, new Type[] { typeof(int), typeof(System.Collections.Generic.IEnumerable<uint>) }).Invoke(patch, new object[] { index, new uint[] { 0x14000000u } });

        private static uint EncodeBranch(ulong source, ulong target)
        {
            try
            {
                return (uint)typeof(NceCpuCodePatch).GetMethod("EncodeBranch", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { source, target });
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
