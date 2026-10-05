using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.Cpu.Nce;
using Ryujinx.HLE.HOS.Services.Ro;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Prepared regression cases; these do not execute native guest code or build an APK.
    [TestFixture]
    public class NceDynamicModulePatchTests
    {
        private const ulong Source = 0x10000, Island = 0x20000, Image = 0x30000;
        private static readonly uint[] Original = { 0xd4000001u, 0xc85f7c00u, 0xd53bd05fu, 0xd503201fu };
        private MemoryBlock _backing;
        private MemoryManager _memory;

        [SetUp]
        public void Setup()
        {
            _backing = new MemoryBlock(0x4000);
            _memory = new MemoryManager(_backing, 0x40000);
            _memory.IncrementReferenceCount();
            _memory.Map(Source, 0, 0x1000, MemoryMapFlags.None);
            _memory.Map(Island, 0x3000, 0x1000, MemoryMapFlags.None);
            _memory.Map(Image, 0, 0x1000, MemoryMapFlags.None);
            _memory.Write(Source, MemoryMarshal.AsBytes(Original.AsSpan()));
        }

        [TearDown]
        public void Cleanup()
        {
            _memory?.DecrementReferenceCount();
            _backing?.Dispose();
        }

        [Test]
        public void UnloadRestoresPatchWordsThroughTheOriginalSourceAlias()
        {
            NceCpuCodePatch patch = FixturePatch();
            patch.Write(_memory, Island, Image);
            Assert.That(_memory.Read<uint>(Source), Is.Not.EqualTo(Original[0]));
            _memory.Unmap(Image, 0x1000);
            patch.RestoreText(_memory, Source, Island, Image, MemoryMarshal.AsBytes(Original.AsSpan()));
            uint[] restored = new uint[Original.Length];
            _memory.Read(Source, MemoryMarshal.AsBytes(restored.AsSpan()));
            Assert.That(restored, Is.EqualTo(Original));
        }

        [Test]
        public void ReloadAtTheSameVirtualAddressDoesNotPatchTheOldBranchAgain()
        {
            for (int round = 0; round < 3; round++)
            {
                if (round != 0) _memory.Map(Image, 0, 0x1000, MemoryMapFlags.None);
                NceCpuCodePatch patch = FixturePatch();
                patch.ValidateAddresses(Island, Image);
                patch.Write(_memory, Island, Image);
                _memory.Unmap(Image, 0x1000);
                patch.RestoreText(_memory, Source, Island, Image, MemoryMarshal.AsBytes(Original.AsSpan()));
                Assert.That(_memory.Read<uint>(Source), Is.EqualTo(Original[0]));
            }
        }

        [Test]
        public void RestorationPreservesGuestChangesAtPatchedAndUnrelatedWords()
        {
            NceCpuCodePatch patch = FixturePatch();
            patch.Write(_memory, Island, Image);
            _memory.Write(Source + 4, 0xaabbccddu);
            _memory.Write(Source + 12, 0x11223344u);
            patch.RestoreText(_memory, Source, Island, Image, MemoryMarshal.AsBytes(Original.AsSpan()));
            Assert.That(_memory.Read<uint>(Source), Is.EqualTo(Original[0]));
            Assert.That(_memory.Read<uint>(Source + 4), Is.EqualTo(0xaabbccddu));
            Assert.That(_memory.Read<uint>(Source + 8), Is.EqualTo(Original[2]));
            Assert.That(_memory.Read<uint>(Source + 12), Is.EqualTo(0x11223344u));
        }

        [Test]
        public void InPlaceOnlyModuleDoesNotRequireAnIsland()
        {
            NceCpuCodePatch patch = NcePatcher.CreatePatch(MemoryMarshal.AsBytes(new uint[] { 0xd53bd05fu, 0xc85f7c00u }.AsSpan()));
            Assert.That(patch.Size, Is.Zero);
            patch.Write(_memory, 0, Image);
            patch.RestoreText(_memory, Source, 0, Image,
                MemoryMarshal.AsBytes(new uint[] { 0xd53bd05fu, 0xc85f7c00u }.AsSpan()));
            Assert.That(_memory.Read<uint>(Source), Is.EqualTo(0xd53bd05fu));
            Assert.That(_memory.Read<uint>(Source + 4), Is.EqualTo(0xc85f7c00u));
        }

        [Test]
        public void InvalidSavedTextIsRejectedBeforeTheFirstRestorationWrite()
        {
            NceCpuCodePatch patch = FixturePatch();
            patch.Write(_memory, Island, Image);
            uint branch = _memory.Read<uint>(Source);
            Assert.Throws<ArgumentException>(() => patch.RestoreText(_memory, Source, Island, Image, new byte[4]));
            Assert.That(_memory.Read<uint>(Source), Is.EqualTo(branch));
        }

        [Test]
        public void OutOfRangeForwardAndReturnBranchesAreRejectedBeforePublication()
        {
            NceCpuCodePatch patch = FixturePatch();
            Assert.Throws<NotSupportedException>(() => patch.ValidateAddresses(0x1000, 0x8001000));
            Assert.Throws<NotSupportedException>(() => patch.ValidateAddresses(0x18001000, 0x10001000));
            Assert.That(_memory.Read<uint>(Source), Is.EqualTo(Original[0]));
        }

        [TestCase(0UL, 0UL)]
        [TestCase(0x1000UL, 0UL)]
        [TestCase(0UL, 0x1000UL)]
        [TestCase(0x1000UL, 0x1000UL)]
        public void LayoutKeepsAllOuterAndIslandGuards(ulong patchSize, ulong bssSize)
        {
            Assert.That(NroNceMapping.TryLayout(0x1000, 0x20000, 0x5000, patchSize, 0x3000, bssSize,
                out ulong image, out ulong end), Is.True);
            Assert.That(image, Is.EqualTo(0x5000UL + patchSize + (patchSize == 0 ? 0UL : 0x4000UL)));
            Assert.That(end, Is.EqualTo(image + 0x3000UL + bssSize + 0x4000UL));
        }

        [TestCase(0x4000UL, 0x1000UL, 0x3000UL, 0UL)] // Left guard outside ASLR.
        [TestCase(0x5001UL, 0x1000UL, 0x3000UL, 0UL)] // Unaligned destination.
        [TestCase(0x5000UL, 0x1001UL, 0x3000UL, 0UL)]
        [TestCase(0x5000UL, 0x1000UL, 0x3001UL, 0UL)]
        [TestCase(0x5000UL, 0x1000UL, 0x3000UL, 1UL)]
        [TestCase(0x5000UL, 0x1000UL, 0UL, 0UL)]
        [TestCase(0x1b000UL, 0x1000UL, 0x3000UL, 0UL)] // Right guard outside ASLR.
        [TestCase(0xfffffffffffff000UL, 0x1000UL, 0x3000UL, 0UL)]
        [TestCase(0x5000UL, 0xfffffffffffff000UL, 0x3000UL, 0UL)]
        public void InvalidLayoutPublishesNoAddress(ulong candidate, ulong patchSize, ulong nroSize, ulong bssSize)
        {
            Assert.That(NroNceMapping.TryLayout(0x1000, 0x20000, candidate, patchSize, nroSize, bssSize,
                out ulong image, out ulong end), Is.False);
            Assert.That(image, Is.Zero);
            Assert.That(end, Is.Zero);
        }

        private static NceCpuCodePatch FixturePatch()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            NceCpuCodePatch patch = new();
            typeof(NceCpuCodePatch).GetMethod("AddCode", flags, new[] { typeof(int), typeof(IEnumerable<uint>) })
                .Invoke(patch, new object[] { 0, new uint[] { 0x14000000u } });
            MethodInfo replace = typeof(NceCpuCodePatch).GetMethod("ReplaceInstruction", flags);
            replace.Invoke(patch, new object[] { 1, 0xc85ffc00u });
            replace.Invoke(patch, new object[] { 2, 0xd503201fu });
            return patch;
        }
    }
}
