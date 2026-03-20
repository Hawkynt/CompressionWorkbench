using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class ZopfliHashChainTests {
  [Category("HappyPath")]
  [Test]
  public void FindAllMatches_ReturnsMultipleLengths() {
    // "ABCABCABCABC" — position 3 should match position 0 at lengths 3,4,5,6,7,8,9
    byte[] data = "ABCABCABCABC"u8.ToArray();
    var chain = new ZopfliHashChain();

    // Insert positions 0,1,2
    for (int i = 0; i < 3; ++i)
      chain.Insert(data, i);

    var matches = chain.FindAllMatches(data, 3, 32768, 258);
    Assert.That(matches, Has.Count.GreaterThan(1));
  }

  [Category("HappyPath")]
  [Test]
  public void FindAllMatches_AscendingLengthOrder() {
    byte[] data = "ABCABCABCABC"u8.ToArray();
    var chain = new ZopfliHashChain();

    for (int i = 0; i < 3; ++i)
      chain.Insert(data, i);

    var matches = chain.FindAllMatches(data, 3, 32768, 258);

    for (int i = 1; i < matches.Count; ++i) {
      Assert.That(matches[i].Length, Is.GreaterThan(matches[i - 1].Length),
        "Matches must be sorted by ascending length");
    }
  }

  [Category("HappyPath")]
  [Test]
  public void FindAllMatches_DeduplicatesByLength() {
    // Multiple candidates at the same length — only one per length (shortest distance)
    byte[] data = "ABABABABAB"u8.ToArray();
    var chain = new ZopfliHashChain();

    for (int i = 0; i < 6; ++i)
      chain.Insert(data, i);

    var matches = chain.FindAllMatches(data, 6, 32768, 258);

    // Should have no duplicate lengths
    var lengths = matches.Select(m => m.Length).ToList();
    Assert.That(lengths, Is.EqualTo(lengths.Distinct().ToList()));
  }

  [Category("Boundary")]
  [Test]
  public void FindAllMatches_RespectsMaxDistance() {
    byte[] data = new byte[100];
    Array.Fill(data, (byte)'A');
    var chain = new ZopfliHashChain();

    // Insert position 0
    chain.Insert(data, 0);

    // Skip far ahead — insert positions 1-49
    for (int i = 1; i < 50; ++i)
      chain.Insert(data, i);

    // Position 50 with maxDistance=10 should only find matches within 10 bytes
    var matches = chain.FindAllMatches(data, 50, 10, 258);
    foreach (var m in matches)
      Assert.That(m.Distance, Is.LessThanOrEqualTo(10));
  }

  [Category("EdgeCase")]
  [Test]
  public void FindAllMatches_NoMatchForShortData() {
    byte[] data = "AB"u8.ToArray();
    var chain = new ZopfliHashChain();

    var matches = chain.FindAllMatches(data, 0, 32768, 258);
    Assert.That(matches, Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void FindAllMatches_NoMatchForUniqueData() {
    byte[] data = new byte[256];
    for (int i = 0; i < 256; ++i)
      data[i] = (byte)i;

    var chain = new ZopfliHashChain();
    for (int i = 0; i < 200; ++i)
      chain.Insert(data, i);

    var matches = chain.FindAllMatches(data, 200, 32768, 258);
    Assert.That(matches, Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void Insert_DoesNotReturnMatches() {
    byte[] data = "ABCABC"u8.ToArray();
    var chain = new ZopfliHashChain();

    // Insert should not throw and not return anything
    chain.Insert(data, 0);
    chain.Insert(data, 1);
    chain.Insert(data, 2);

    // Verify that FindAllMatches works after inserts
    var matches = chain.FindAllMatches(data, 3, 32768, 258);
    Assert.That(matches, Has.Count.GreaterThanOrEqualTo(1));
  }
}
