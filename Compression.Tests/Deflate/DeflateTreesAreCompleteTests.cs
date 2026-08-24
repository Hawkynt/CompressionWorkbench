#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Tests.Support;

namespace Compression.Tests.Deflate;

/// <summary>
/// Every Huffman tree in a dynamic block has to describe the whole code space,
/// not merely enough of it for a forgiving decoder.
/// </summary>
/// <remarks>
/// <para>A tree with one symbol in it — one distance code, say, because every
/// back-reference in the block happened to fall in the same range — gives that
/// symbol a one-bit code and leaves the other half of the code space describing
/// nothing. zlib takes it: the single-code case is written into its inflater as
/// a special case. Stricter decoders add the fractions up and refuse the block,
/// and <c>libmspack</c> is one of them, so every cabinet we wrote whose payload
/// ran past a couple of kilobytes came back from <c>cabextract</c> as
/// "decompression error" while 7-Zip read it perfectly.</para>
///
/// <para>That made it a difference no test here could see. The check below is
/// arithmetic on the bits we emit rather than the opinion of whichever decoder
/// happens to be installed: for each tree, the sum of 2^-length over the symbols
/// that have a code must come to exactly one.</para>
///
/// <para>The payloads are chosen for the shape of their trees rather than their
/// size — one that produces a single distance code, one with no back-references
/// at all, one whose lengths are all equal so the code-length tree degenerates
/// too.</para>
/// </remarks>
[TestFixture]
public class DeflateTreesAreCompleteTests {

  private static IEnumerable<TestCaseData> Payloads() {
    // Matches only ever a few bytes back: one distance code and no other.
    var oneDistance = new byte[40_000];
    for (var i = 0; i < oneDistance.Length; ++i) oneDistance[i] = (byte)('A' + i % 26);
    yield return new TestCaseData(oneDistance).SetName("every match at the same distance");

    // One byte over and over: one literal, one length, one distance.
    var single = new byte[50_000];
    Array.Fill(single, (byte)0x5A);
    yield return new TestCaseData(single).SetName("one byte repeated");

    // Nothing repeats: literals only, no distance code earned at all.
    var noMatches = new byte[20_000];
    var rng = new Random(11);
    rng.NextBytes(noMatches);
    yield return new TestCaseData(noMatches).SetName("nothing worth referencing");

    // Two bytes alternating: a tiny alphabet with long runs.
    var alternating = new byte[30_000];
    for (var i = 0; i < alternating.Length; ++i) alternating[i] = (byte)(i % 2 == 0 ? 'x' : 'y');
    yield return new TestCaseData(alternating).SetName("two bytes alternating");

    var phrase = System.Text.Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("the same sentence, over and over again. ", 2_000)));
    yield return new TestCaseData(phrase).SetName("one sentence repeated");
  }

  [TestCaseSource(nameof(Payloads)), Category("Regression")]
  public void EveryTreeInEveryDynamicBlock_CoversItsWholeCodeSpace(byte[] payload) {
    foreach (var level in Enum.GetValues<DeflateCompressionLevel>()) {
      var compressed = DeflateCompressor.Compress(payload, level);
      var blocks = DeflateBlockTrees.Read(compressed);

      for (var i = 0; i < blocks.Count; ++i) {
        var block = blocks[i];
        if (!block.IsDynamic) continue;

        AssertComplete(block.CodeLengthLengths, $"code-length tree of block {i} at {level}");
        AssertComplete(block.LiteralLengths, $"literal/length tree of block {i} at {level}");
        AssertComplete(block.DistanceLengths, $"distance tree of block {i} at {level}");
      }
    }
  }

  private static void AssertComplete(IReadOnlyList<int> lengths, string what) {
    var used = lengths.Count(l => l > 0);
    if (used == 0) return;                        // a tree nobody described

    // Kraft's sum, in halves rather than in floating point so that "exactly one"
    // means exactly one.
    var total = 0L;
    const long whole = 1L << 32;
    foreach (var length in lengths)
      if (length > 0)
        total += whole >> length;

    Assert.That(used, Is.GreaterThan(1),
      $"the {what} has a single symbol, which leaves half the code space unspoken for");
    Assert.That(total, Is.EqualTo(whole),
      $"the {what} covers {(double)total / whole:0.####} of the code space, not all of it");
  }
}
