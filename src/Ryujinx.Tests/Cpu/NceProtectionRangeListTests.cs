using NUnit.Framework;
using Ryujinx.Cpu.Nce;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Ryujinx.Tests.Cpu
{
    public class NceProtectionRangeListTests
    {
        private sealed class Harness
        {
            private static readonly Type Type = typeof(NceEngine).Assembly
                .GetType("Ryujinx.Cpu.Nce.NceProtectionRangeList`1", true).MakeGenericType(typeof(int));
            private readonly object _list;
            internal Harness(int capacity)
            {
                _list = Activator.CreateInstance(Type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { capacity }, null);
            }
            private object Call(string name, params object[] args)
            {
                try { return Type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_list, args); }
                catch (TargetInvocationException exception)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }
            private object Get(string name) => Type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_list);
            internal int Count => (int)Get("Count");
            internal int Blocks => (int)Get("BlockCount");
            internal int this[int index]
            {
                get => (int)Type.GetProperty("Item", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_list, new object[] { index });
                set => Type.GetProperty("Item", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_list, value, new object[] { index });
            }
            internal void Insert(int index, int value) => Call("Insert", index, value);
            internal void Remove(int index, int count) => Call("RemoveRange", index, count);
            internal int LowerBound(int key) => (int)Call("LowerBound", (ulong)key, (Func<int, ulong>)(value => (ulong)value));
            internal void Check(List<int> expected)
            {
                Assert.That(Count, Is.EqualTo(expected.Count));
                for (int i = 0; i < Count; i++) Assert.That(this[i], Is.EqualTo(expected[i]), $"index {i}");
            }
        }

        [TestCase(8, 1)]
        [TestCase(8, 99)]
        [TestCase(128, 7)]
        [TestCase(128, 701)]
        public void RandomEditsAndLookupMatchTheListOracle(int capacity, int seed)
        {
            var actual = new Harness(capacity);
            var expected = new List<int>();
            var random = new Random(seed);
            for (int step = 0; step < 1800; step++)
            {
                if (expected.Count < capacity * 3 || random.Next(3) != 0)
                {
                    int value = random.Next(100000);
                    int index = expected.BinarySearch(value);
                    if (index < 0) index = ~index;
                    expected.Insert(index, value); actual.Insert(index, value);
                }
                else
                {
                    int index = random.Next(expected.Count);
                    int count = random.Next(Math.Min(expected.Count - index, capacity * 2) + 1);
                    expected.RemoveRange(index, count); actual.Remove(index, count);
                }
                actual.Check(expected);
                int key = random.Next(100000);
                int wanted = expected.FindIndex(value => value >= key);
                Assert.That(actual.LowerBound(key), Is.EqualTo(wanted < 0 ? expected.Count : wanted));
                if (expected.Count != 0)
                {
                    int index = random.Next(expected.Count);
                    actual[index] = expected[index];
                    actual.Check(expected);
                }
            }
        }

        [TestCase(0)]
        [TestCase(4)]
        [TestCase(8)]
        public void FullLeafInsertionAndRemovalPreserveAllBoundaryPositions(int index)
        {
            var actual = new Harness(8); var expected = new List<int>();
            for (int i = 0; i < 8; i++) { actual.Insert(i, i * 2); expected.Add(i * 2); }
            int value = index * 2;
            actual.Insert(index, value); expected.Insert(index, value); actual.Check(expected);
            for (int key = 0; key < 20; key++)
            {
                int wanted = expected.FindIndex(x => x >= key);
                Assert.That(actual.LowerBound(key), Is.EqualTo(wanted < 0 ? expected.Count : wanted));
            }
            actual.Remove(index, 1); expected.RemoveAt(index); actual.Check(expected);
        }

        [Test]
        public void RemovalAcrossLeavesAndEmptyReusePreservePrefixCounts()
        {
            var actual = new Harness(8); var expected = new List<int>();
            for (int i = 0; i < 64; i++) { actual.Insert(i, i); expected.Add(i); }
            actual.Remove(3, 46); expected.RemoveRange(3, 46); actual.Check(expected);
            actual.Remove(2, expected.Count - 2); expected.RemoveRange(2, expected.Count - 2); actual.Check(expected);
            actual.Remove(0, expected.Count); expected.Clear(); actual.Check(expected);
            Assert.That(actual.Blocks, Is.Zero);
            Assert.That(actual.LowerBound(1), Is.Zero);
            actual.Insert(0, 7); expected.Add(7); actual.Check(expected);
        }

        [Test]
        public void HysteresisAvoidsRepeatedLeafSplitsAtTheSameBoundary()
        {
            var actual = new Harness(8);
            for (int i = 0; i < 8; i++) actual.Insert(i, i);
            actual.Insert(4, 4); actual.Remove(4, 1);
            Assert.That(actual.Blocks, Is.EqualTo(2));
            for (int i = 0; i < 100; i++) { actual.Insert(4, 4); actual.Remove(4, 1); }
            actual.Remove(0, 6);
            Assert.That(actual.Count, Is.EqualTo(2));
            Assert.That(actual.Blocks, Is.EqualTo(1));
        }

        [Test]
        public void FrontInsertionPreservesIndicesAcrossALargeCollection()
        {
            var actual = new Harness(128);
            for (int i = 0; i < 16384; i++) actual.Insert(i, i + 1);
            actual.Insert(0, 0);
            Assert.That(actual[0], Is.Zero);
            Assert.That(actual[16384], Is.EqualTo(16384));
        }

        [Test]
        public void InvalidRangeEditsLeaveTheCollectionIntact()
        {
            var actual = new Harness(8); actual.Insert(0, 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => actual.Insert(2, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => actual.Remove(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => actual.Remove(-1, 1));
            Assert.That(actual.Count, Is.EqualTo(1));
            Assert.That(actual[0], Is.EqualTo(1));
        }
    }
}
