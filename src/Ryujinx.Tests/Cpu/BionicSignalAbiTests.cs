using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Pure layout/conversion tests: no signal installation or Android libc calls on the host.
    [TestFixture]
    public class BionicSignalAbiTests
    {
        private static readonly Type Registration = typeof(NcePatcher).Assembly.GetType(
            "Ryujinx.Cpu.Signal.UnixSignalHandlerRegistration", throwOnError: true);
        private static readonly Type BionicAction = Registration.GetNestedType("SigActionBionic", BindingFlags.Public);
        private static readonly Type BionicMask = Registration.GetNestedType("SigSetBionic", BindingFlags.Public);
        private static readonly Type CommonAction = Registration.GetNestedType("SigAction", BindingFlags.Public);
        private static readonly Type CommonMask = Registration.GetNestedType("SigSet", BindingFlags.Public);

        [SetUp]
        public void Require64BitHost()
        {
            if (IntPtr.Size != 8)
            {
                Assert.Ignore("These assertions cover the Android LP64 ABI; Android LP32 is unsupported.");
            }
        }

        [Test]
        public void LayoutMatchesNdkArm64SigactionRatherThanKernelSigaction()
        {
            Assert.That(Marshal.SizeOf(BionicMask), Is.EqualTo(8));
            Assert.That(Marshal.SizeOf(BionicAction), Is.EqualTo(32));
            Assert.That(Marshal.OffsetOf(BionicAction, "sa_flags").ToInt32(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf(BionicAction, "sa_handler").ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf(BionicAction, "sa_mask").ToInt32(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf(BionicAction, "sa_restorer").ToInt32(), Is.EqualTo(24));
            Invoke("ValidateBionicLayout");
        }

        [Test]
        public void SharedMaskAndActionKeepTheirExistingLayout()
        {
            Assert.That(Marshal.SizeOf(CommonMask), Is.EqualTo(128));
            Assert.That(Marshal.SizeOf(CommonAction), Is.EqualTo(148));
            Assert.That(Marshal.OffsetOf(CommonAction, "sa_handler").ToInt32(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf(CommonAction, "sa_mask").ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf(CommonAction, "sa_flags").ToInt32(), Is.EqualTo(136));
            Assert.That(Marshal.OffsetOf(CommonAction, "sa_restorer").ToInt32(), Is.EqualTo(140));
        }

        [TestCase(0UL, 0L, 0, 0L)] // SIG_DFL with no mask/restorer.
        [TestCase(1UL, 1L, 4, 0L)] // SIG_IGN representation.
        [TestCase(0x8000000100000000UL, 0x12345678L, 0x08000004, 0x23456789L)] // RT/high signal bits.
        [TestCase(ulong.MaxValue, 0x12345678L, int.MinValue, 0x23456789L)] // Preserve all flag/mask bits.
        public void PreviousActionRoundTripsWithoutLosingMaskFlagsHandlerOrRestorer(ulong bits, long handler, int flags, long restorer)
        {
            object mask = Activator.CreateInstance(BionicMask);
            BionicMask.GetField("Mask").SetValue(mask, bits);
            object original = Activator.CreateInstance(BionicAction);
            Set(original, "sa_mask", mask);
            Set(original, "sa_handler", (nint)handler);
            Set(original, "sa_flags", flags);
            Set(original, "sa_restorer", (nint)restorer);

            object common = Invoke("ToCommonSigAction", original);
            object restored = Invoke("FromCommonSigAction", common);
            Assert.That(Get(restored, "sa_handler"), Is.EqualTo((nint)handler));
            Assert.That(Get(restored, "sa_flags"), Is.EqualTo(flags));
            Assert.That(Get(restored, "sa_restorer"), Is.EqualTo((nint)restorer));
            Assert.That(BionicMask.GetField("Mask").GetValue(Get(restored, "sa_mask")), Is.EqualTo(bits));

            // The common mask must not retain incidental restorer bytes in its unused tail.
            nint buffer = Marshal.AllocHGlobal(128);
            try
            {
                Marshal.StructureToPtr(Get(common, "sa_mask"), buffer, false);
                Assert.That(unchecked((ulong)Marshal.ReadInt64(buffer)), Is.EqualTo(bits));
                for (int offset = 8; offset < 128; offset++)
                {
                    Assert.That(Marshal.ReadByte(buffer, offset), Is.Zero, $"Unexpected mask tail byte {offset}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Set(object action, string name, object value) => action.GetType().GetField(name).SetValue(action, value);
        private static object Get(object action, string name) => action.GetType().GetField(name).GetValue(action);

        private static object Invoke(string name, params object[] arguments)
        {
            try
            {
                return Registration.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
