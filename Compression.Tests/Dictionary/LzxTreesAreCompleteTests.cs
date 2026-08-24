#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzx;

namespace Compression.Tests.Dictionary;

/// <summary>
/// Every Huffman tree an LZX block header describes has to cover the whole code
/// space, including the ones the block never uses.
/// </summary>
/// <remarks>
/// <para>A block whose matches are all short uses no length symbol, and the
/// length tree was then written as nothing at all: 249 code lengths of zero. Our
/// own decoder never noticed, because it never had a length symbol to look up.
/// The reference decoder refuses the block outright — "the WIM contains invalid
/// compressed data" — and so every image holding a chunk of that shape was ours
/// alone.</para>
///
/// <para>A tree of exactly one symbol is the same fault with a smaller footprint:
/// one code of one bit, and the other half of the space unaccounted for.</para>
///
/// <para>The remedy costs two code lengths in a header and nothing in a block.
/// What is checked here is the arithmetic rather than any decoder's tolerance:
/// the sum of 2^-length over the coded symbols must be exactly one.</para>
/// </remarks>
[TestFixture]
public class LzxTreesAreCompleteTests {

  private static IEnumerable<TestCaseData> Trees() {
    yield return new TestCaseData(new int[8], 8).SetName("no symbol occurs at all");

    var one = new int[249];
    one[7] = 5;
    yield return new TestCaseData(one, 249).SetName("one symbol occurs");

    var oneAtZero = new int[249];
    oneAtZero[0] = 9;
    yield return new TestCaseData(oneAtZero, 249).SetName("one symbol, and it is the first");

    var two = new int[512];
    two[3] = 4; two[9] = 2;
    yield return new TestCaseData(two, 512).SetName("two symbols");

    var many = new int[512];
    for (var i = 0; i < 40; ++i) many[i * 7] = i + 1;
    yield return new TestCaseData(many, 512).SetName("a spread of symbols");
  }

  [TestCaseSource(nameof(Trees)), Category("Regression")]
  public void ATreeWeWrite_CoversItsWholeCodeSpace(int[] frequencies, int symbolCount) {
    var lengths = LzxCompressor.BuildCodeLengths(frequencies, symbolCount, 16);

    var used = lengths.Count(l => l > 0);
    Assert.That(used, Is.GreaterThan(1),
      "a tree of one symbol or none leaves half the code space or all of it unspoken for");

    var total = 0L;
    const long whole = 1L << 32;
    foreach (var length in lengths)
      if (length > 0)
        total += whole >> length;

    Assert.That(total, Is.EqualTo(whole),
      $"the tree covers {(double)total / whole:0.####} of the code space, not all of it");
  }

  [Test, Category("Regression")]
  public void ABlockOfShortMatchesOnly_StillDescribesALengthTree() {
    // Two-byte matches at a repeating distance need no length symbol, which is
    // the input that produced an empty length tree and an image only we could
    // read.
    var data = new byte[8_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 3);

    var compressed = new LzxCompressor(15).Compress(data);

    using var input = new MemoryStream(compressed);
    var back = new LzxDecompressor(input, 15).Decompress(data.Length);
    Assert.That(back, Is.EqualTo(data).AsCollection, "the block did not round-trip");
  }
}
