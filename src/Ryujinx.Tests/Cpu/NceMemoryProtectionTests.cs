using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Ryujinx.Tests.Cpu
{
    public class NceMemoryProtectionTests
    {
        private const ulong Start = 0x10000, Page = 0x1000;
        private static readonly Type MapType = typeof(NceEngine).Assembly.GetType("Ryujinx.Cpu.Nce.NceMemoryProtectionMap", true);
        private sealed class Harness
        {
            internal readonly List<(ulong Address, ulong Size, MemoryPermission Permission)> Calls = new();
            internal bool Fail;
            private readonly object _map;
            internal Harness(ulong pages = 8)
            {
                Action<ulong, ulong, MemoryPermission> apply = (address, size, permission) =>
                {
                    if (Fail) throw new InvalidOperationException("simulated protection failure");
                    Calls.Add((address, size, permission));
                };
                _map = Activator.CreateInstance(MapType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { Start, Page * pages, Page, apply }, null);
            }
            private object Call(string name, params object[] args)
            {
                try { return MapType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_map, args); }
                catch (TargetInvocationException exception)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }
            internal void Map(ulong address, ulong size, Action action = null) => Call("Map", address, size, action ?? (() => { }));
            internal void Unmap(ulong address, ulong size) => Call("Unmap", address, size, (Action)(() => { }));
            internal bool Guest(ulong address, ulong size, MemoryPermission permission, bool restore = true)
                => (bool)Call("SetGuest", address, size, permission, restore);
            internal void Track(ulong address, ulong size, MemoryPermission permission) => Call("SetTracking", address, size, permission);
            internal bool Allowed(ulong address, ulong size, bool write, bool effective = false)
                => (bool)Call(effective ? "IsEffectiveAccessAllowed" : "IsGuestAccessAllowed", address, size, write);
            internal (bool GuestAllowed, bool EffectiveAllowed) Access(ulong address, ulong size, bool write)
                => ((bool, bool))Call("GetAccessState", address, size, write);
        }

        public static IEnumerable<TestCaseData> CompositionCases()
        {
            MemoryPermission[] guests = { MemoryPermission.None, MemoryPermission.Read, MemoryPermission.ReadAndWrite,
                MemoryPermission.Execute, MemoryPermission.ReadAndExecute, MemoryPermission.ReadWriteExecute };
            MemoryPermission[] tracks = { MemoryPermission.None, MemoryPermission.Read, MemoryPermission.ReadAndWrite };
            foreach (MemoryPermission guest in guests)
                foreach (MemoryPermission tracking in tracks)
                    yield return new TestCaseData(guest, tracking);
        }

        [TestCaseSource(nameof(CompositionCases))]
        public void TrackingOnlyRemovesGuestDataAccess(MemoryPermission guest, MemoryPermission tracking)
        {
            var map = new Harness();
            map.Map(Start, Page);
            map.Guest(Start, Page, guest);
            map.Track(Start, Page, tracking);
            MemoryPermission result = map.Calls.Count == 0 ? MemoryPermission.ReadAndWrite : map.Calls[^1].Permission;
            Assert.That(result & ~guest, Is.EqualTo(MemoryPermission.None));
            Assert.That(result & MemoryPermission.Execute, Is.EqualTo(guest & MemoryPermission.Execute));
            Assert.That(map.Allowed(Start + 7, 1, false, true), Is.EqualTo((guest & tracking & MemoryPermission.Read) != 0));
            Assert.That(map.Allowed(Start + 7, 1, true, true), Is.EqualTo((guest & tracking & MemoryPermission.Write) != 0));
        }

        [Test]
        public void PartialGuestContractsSurviveBroadTrackingRequests()
        {
            var map = new Harness(); map.Map(Start, Page * 3);
            map.Guest(Start, Page, MemoryPermission.None);
            map.Guest(Start + Page, Page, MemoryPermission.Read);
            map.Guest(Start + Page * 2, Page, MemoryPermission.ReadAndExecute);
            map.Track(Start, Page * 3, MemoryPermission.None);
            Assert.That(map.Calls[^1], Is.EqualTo((Start + Page * 2, Page, MemoryPermission.Execute)));
            map.Track(Start, Page * 3, MemoryPermission.ReadAndWrite);
            Assert.That(map.Allowed(Start, 1, false, true), Is.False);
            Assert.That(map.Allowed(Start + Page, 1, true, true), Is.False);
            Assert.That(map.Calls[^1].Permission, Is.EqualTo(MemoryPermission.ReadAndExecute));
        }

        [Test]
        public void Prot1OffOnlyOverwritesTheChangedRangeAndRetainsNextTrackingDemand()
        {
            var map = new Harness(); map.Map(Start, Page * 2); map.Track(Start, Page * 2, MemoryPermission.None);
            Assert.That(map.Guest(Start, Page, MemoryPermission.ReadAndExecute, false), Is.False);
            Assert.That(map.Allowed(Start, 1, false, true), Is.True);
            Assert.That(map.Allowed(Start + Page, 1, false, true), Is.False);
            map.Track(Start, Page, MemoryPermission.None);
            Assert.That(map.Calls[^1].Permission, Is.EqualTo(MemoryPermission.Execute));
            Assert.That(map.Guest(Start, Page, MemoryPermission.ReadAndExecute), Is.True);
        }

        [Test]
        public void HolesAreSkippedForTrackingButRejectGuestChangesBeforeAnyProtection()
        {
            var map = new Harness(); map.Map(Start, Page); map.Map(Start + Page * 2, Page);
            map.Track(Start, Page * 3, MemoryPermission.None);
            Assert.That(map.Calls.Count, Is.EqualTo(2));
            Assert.That(map.Calls[0].Size, Is.EqualTo(Page));
            Assert.Throws<InvalidMemoryRegionException>(() => map.Guest(Start, Page * 3, MemoryPermission.Read));
            Assert.That(map.Calls.Count, Is.EqualTo(2));
            Assert.That(map.Allowed(Start + Page - 1, 2, false), Is.False);
        }

        [Test]
        public void UnmapAndRemapDiscardOldPermissionsAndFailedMapPreservesThem()
        {
            var map = new Harness(); map.Map(Start, Page * 2); map.Guest(Start, Page * 2, MemoryPermission.None);
            Assert.Throws<InvalidOperationException>(() => map.Map(Start, Page, () => throw new InvalidOperationException()));
            Assert.That(map.Allowed(Start, 1, false), Is.False);
            map.Unmap(Start, Page);
            Assert.That(map.Allowed(Start, 1, false), Is.False);
            map.Map(Start, Page);
            Assert.That(map.Allowed(Start, 1, true, true), Is.True);
            Assert.That(map.Allowed(Start + Page, 1, false), Is.False);
        }

        [Test]
        public void DedupAndContiguousGroupingAvoidRedundantProtectionCalls()
        {
            var map = new Harness(); map.Map(Start, Page * 3);
            map.Guest(Start, Page * 3, MemoryPermission.ReadAndWrite);
            Assert.That(map.Calls, Is.Empty);
            map.Guest(Start + Page, Page, MemoryPermission.Read);
            map.Track(Start, Page * 3, MemoryPermission.None);
            Assert.That(map.Calls[^1], Is.EqualTo((Start, Page * 3, MemoryPermission.None)));
            int count = map.Calls.Count;
            map.Track(Start, Page * 3, MemoryPermission.None);
            Assert.That(map.Calls.Count, Is.EqualTo(count));
        }

        [Test]
        public void FailedProtectionDoesNotPublishAFalseAccessibleState()
        {
            var map = new Harness(); map.Map(Start, Page); map.Guest(Start, Page, MemoryPermission.None);
            map.Fail = true;
            Assert.Throws<InvalidOperationException>(() => map.Guest(Start, Page, MemoryPermission.ReadAndWrite));
            Assert.That(map.Allowed(Start, 1, true), Is.False);
            Assert.That(map.Allowed(Start, 1, false, true), Is.False);
        }

        [Test]
        public void GuestAndEffectiveAccessHaveDistinctMeaningsAtTheExactAddress()
        {
            var map = new Harness(); map.Map(Start, Page * 2);
            map.Guest(Start, Page, MemoryPermission.Read);
            map.Track(Start, Page * 2, MemoryPermission.None);
            Assert.That(map.Allowed(Start + Page - 1, 1, true), Is.False);
            Assert.That(map.Allowed(Start + Page, 1, true), Is.True);
            Assert.That(map.Allowed(Start + Page, 1, true, true), Is.False);
            map.Track(Start + Page, Page, MemoryPermission.ReadAndWrite);
            Assert.That(map.Allowed(Start + Page, 1, true, true), Is.True);
            Assert.That(map.Allowed(ulong.MaxValue, 2, false), Is.False);
            Assert.That(map.Allowed(Start, 0, false), Is.False);
        }

        // Deterministic interleaving: tracking handles the access, then another consumer
        // rearms protection before the handler's final snapshot. No scheduling-dependent test.
        [TestCase(true, MemoryPermission.Read)]
        [TestCase(false, MemoryPermission.None)]
        public void TrackingRearmKeepsGuestRetryAllowed(bool write, MemoryPermission rearmed)
        {
            var map = new Harness(); map.Map(Start, Page);
            map.Track(Start, Page, MemoryPermission.None);
            map.Track(Start, Page, MemoryPermission.ReadAndWrite);
            map.Track(Start, Page, rearmed);
            var access = map.Access(Start + 7, 1, write);
            Assert.That(access.GuestAllowed, Is.True);
            Assert.That(access.EffectiveAllowed, Is.False);
        }

        [Test]
        public void GuestRevocationOrUnmapAfterTrackingStillRejectsTheRetry()
        {
            var map = new Harness(); map.Map(Start, Page * 2);
            map.Track(Start, Page * 2, MemoryPermission.ReadAndWrite);
            map.Guest(Start, Page, MemoryPermission.Read);
            Assert.That(map.Access(Start + 7, 1, true), Is.EqualTo((false, false)));
            map.Unmap(Start + Page, Page);
            Assert.That(map.Access(Start + Page, 1, false), Is.EqualTo((false, false)));
            Assert.That(map.Access(Start + Page - 1, 2, false), Is.EqualTo((false, false)));
        }

        [TestCase(19)]
        [TestCase(901)]
        public void FragmentedPermissionsAndAliasesMatchAPageContractOracle(int seed)
        {
            const int pages = 512;
            var map = new Harness(pages); map.Map(Start, Page * pages);
            var guest = new MemoryPermission[pages];
            var tracking = new MemoryPermission[pages];
            var active = new bool[pages];
            var mapped = new bool[pages];
            for (int i = 0; i < pages; i++) { guest[i] = tracking[i] = MemoryPermission.ReadAndWrite; mapped[i] = true; }
            var random = new Random(seed);
            MemoryPermission[] permissions = { MemoryPermission.None, MemoryPermission.Read,
                MemoryPermission.ReadAndWrite, MemoryPermission.Execute, MemoryPermission.ReadAndExecute, MemoryPermission.ReadWriteExecute };
            MemoryPermission[] demands = { MemoryPermission.None, MemoryPermission.Read, MemoryPermission.ReadAndWrite };
            for (int step = 0; step < 700; step++)
            {
                int first = random.Next(pages), count = random.Next(1, Math.Min(17, pages - first) + 1);
                ulong address = Start + (ulong)first * Page, size = (ulong)count * Page;
                int operation = random.Next(6);
                if (operation == 0)
                {
                    map.Unmap(address, size);
                    for (int i = first; i < first + count; i++) mapped[i] = false;
                }
                else if (operation == 1)
                {
                    map.Map(address, size);
                    for (int i = first; i < first + count; i++)
                    { mapped[i] = true; guest[i] = tracking[i] = MemoryPermission.ReadAndWrite; active[i] = false; }
                }
                else if (operation == 2)
                {
                    MemoryPermission permission = permissions[random.Next(permissions.Length)];
                    bool restore = random.Next(2) == 0;
                    bool allMapped = true;
                    for (int i = first; i < first + count; i++) allMapped &= mapped[i];
                    if (allMapped)
                    {
                        map.Guest(address, size, permission, restore);
                        for (int i = first; i < first + count; i++) { guest[i] = permission; active[i] = restore; }
                    }
                    else Assert.Throws<InvalidMemoryRegionException>(() => map.Guest(address, size, permission, restore));
                }
                else
                {
                    MemoryPermission permission = demands[random.Next(demands.Length)];
                    map.Track(address, size, permission);
                    for (int i = first; i < first + count; i++)
                        if (mapped[i]) { tracking[i] = permission; active[i] = true; }
                }
                for (int i = 0; i < pages; i++)
                {
                    MemoryPermission effective = active[i] ? guest[i] & tracking[i] : guest[i];
                    foreach (bool write in new[] { false, true })
                    {
                        MemoryPermission bit = write ? MemoryPermission.Write : MemoryPermission.Read;
                        var access = map.Access(Start + (ulong)i * Page + 7, 1, write);
                        Assert.That(access.GuestAllowed, Is.EqualTo(mapped[i] && (guest[i] & bit) != 0), $"guest step={step} page={i}");
                        Assert.That(access.EffectiveAllowed, Is.EqualTo(mapped[i] && (effective & bit) != 0), $"effective step={step} page={i}");
                    }
                }
            }
        }

        [TestCase(4096ul, true)]
        [TestCase(16384ul, false)]
        [TestCase(65536ul, false)]
        public void BionicHostPagePolicy(ulong size, bool expected)
            => Assert.That(NceEngine.SupportsBionicPageSize(size), Is.EqualTo(expected));

        [Test]
        public void InvalidProtectionRangesAndPermissionsAreRejected()
        {
            var map = new Harness(); map.Map(Start, Page);
            Assert.Throws<ArgumentException>(() => map.Guest(Start + 1, Page, MemoryPermission.Read));
            Assert.Throws<ArgumentException>(() => map.Track(Start, Page * 9, MemoryPermission.None));
            Assert.Throws<ArgumentException>(() => map.Track(Start, Page, MemoryPermission.ReadAndExecute));
            Assert.Throws<ArgumentException>(() => map.Guest(Start, Page, MemoryPermission.Invalid));
            Assert.That(map.Calls, Is.Empty);
        }
    }
}
