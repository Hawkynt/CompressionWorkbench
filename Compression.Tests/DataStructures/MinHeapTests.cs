using Compression.Core.DataStructures;

namespace Compression.Tests.DataStructures;

[TestFixture]
public class MinHeapTests {
  [Test]
  public void Insert_And_ExtractMin_ReturnsInOrder() {
    var heap = new MinHeap<int>();

    heap.Insert(5);
    heap.Insert(3);
    heap.Insert(8);
    heap.Insert(1);
    heap.Insert(4);

    Assert.That(heap.ExtractMin(), Is.EqualTo(1));
    Assert.That(heap.ExtractMin(), Is.EqualTo(3));
    Assert.That(heap.ExtractMin(), Is.EqualTo(4));
    Assert.That(heap.ExtractMin(), Is.EqualTo(5));
    Assert.That(heap.ExtractMin(), Is.EqualTo(8));
  }

  [Test]
  public void Peek_ReturnsMinWithoutRemoving() {
    var heap = new MinHeap<int>();
    heap.Insert(10);
    heap.Insert(5);
    heap.Insert(7);

    Assert.That(heap.Peek(), Is.EqualTo(5));
    Assert.That(heap.Count, Is.EqualTo(3)); // Not removed
  }

  [Test]
  public void ExtractMin_EmptyHeap_Throws() {
    var heap = new MinHeap<int>();
    Assert.Throws<InvalidOperationException>(() => heap.ExtractMin());
  }

  [Test]
  public void Peek_EmptyHeap_Throws() {
    var heap = new MinHeap<int>();
    Assert.Throws<InvalidOperationException>(() => heap.Peek());
  }

  [Test]
  public void Count_TracksCorrectly() {
    var heap = new MinHeap<int>();

    Assert.That(heap.Count, Is.EqualTo(0));
    heap.Insert(1);
    Assert.That(heap.Count, Is.EqualTo(1));
    heap.Insert(2);
    Assert.That(heap.Count, Is.EqualTo(2));
    heap.ExtractMin();
    Assert.That(heap.Count, Is.EqualTo(1));
  }

  [Test]
  public void StressTest_SortsRandomData() {
    var heap = new MinHeap<int>();
    var rng = new Random(42);
    var expected = new List<int>();

    for (int i = 0; i < 1000; ++i) {
      int value = rng.Next(10000);
      heap.Insert(value);
      expected.Add(value);
    }

    expected.Sort();

    for (int i = 0; i < 1000; ++i)
      Assert.That(heap.ExtractMin(), Is.EqualTo(expected[i]));
  }

  [Test]
  public void DuplicateValues_HandledCorrectly() {
    var heap = new MinHeap<int>();

    heap.Insert(5);
    heap.Insert(5);
    heap.Insert(3);
    heap.Insert(3);

    Assert.That(heap.ExtractMin(), Is.EqualTo(3));
    Assert.That(heap.ExtractMin(), Is.EqualTo(3));
    Assert.That(heap.ExtractMin(), Is.EqualTo(5));
    Assert.That(heap.ExtractMin(), Is.EqualTo(5));
  }
}
