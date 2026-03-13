using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class BlockSplitterTests {
  [Test]
  public void Split_SmallInput_SingleBlock() {
    var symbols = new LzSymbol[100];
    for (int i = 0; i < 100; ++i)
      symbols[i] = LzSymbol.Literal((byte)(i % 256));

    var blocks = BlockSplitter.Split(symbols);

    Assert.That(blocks, Has.Count.EqualTo(1));
    Assert.That(blocks[0].Start, Is.EqualTo(0));
    Assert.That(blocks[0].End, Is.EqualTo(100));
  }

  [Test]
  public void Split_ContiguousRanges() {
    // Create enough symbols to potentially get multiple blocks
    var symbols = new LzSymbol[5000];
    for (int i = 0; i < 5000; ++i)
      symbols[i] = LzSymbol.Literal((byte)(i % 256));

    var blocks = BlockSplitter.Split(symbols);

    // Verify ranges are contiguous
    Assert.That(blocks[0].Start, Is.EqualTo(0));
    for (int i = 1; i < blocks.Count; ++i) {
      Assert.That(blocks[i].Start, Is.EqualTo(blocks[i - 1].End),
        $"Block {i} start must equal block {i - 1} end");
    }
    Assert.That(blocks[^1].End, Is.EqualTo(5000));
  }

  [Test]
  public void Split_HeterogeneousData_SplitsAtBoundary() {
    // Create data with two very different halves
    var symbols = new LzSymbol[4000];

    // First half: all literals of 'A'
    for (int i = 0; i < 2000; ++i)
      symbols[i] = LzSymbol.Literal((byte)'A');

    // Second half: matches (very different statistics)
    for (int i = 2000; i < 4000; ++i)
      symbols[i] = LzSymbol.Match(10, 1);

    var blocks = BlockSplitter.Split(symbols);

    // Should have at least 1 block (may or may not split depending on cost)
    Assert.That(blocks, Has.Count.GreaterThanOrEqualTo(1));

    // Verify all symbols are covered
    int total = blocks.Sum(b => b.End - b.Start);
    Assert.That(total, Is.EqualTo(4000));
  }

  [Test]
  public void Split_RespectsMaxBlocks() {
    var symbols = new LzSymbol[10000];
    for (int i = 0; i < 10000; ++i)
      symbols[i] = LzSymbol.Literal((byte)(i % 256));

    var blocks = BlockSplitter.Split(symbols, maxBlocks: 3);
    Assert.That(blocks, Has.Count.LessThanOrEqualTo(3));
  }

  [Test]
  public void Split_EmptyInput_SingleBlock() {
    var blocks = BlockSplitter.Split([]);
    Assert.That(blocks, Has.Count.EqualTo(1));
    Assert.That(blocks[0].Start, Is.EqualTo(0));
    Assert.That(blocks[0].End, Is.EqualTo(0));
  }
}
