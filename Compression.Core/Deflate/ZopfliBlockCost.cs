using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Deflate;

/// <summary>
/// Works out what a block of LZ symbols actually costs in each of the three block types
/// RFC 1951 offers, so that both the block splitter and the emitter decide from the same
/// numbers.
/// </summary>
/// <remarks>
/// The cost is derived from symbol histograms rather than from the symbols themselves, so
/// the cost of any range is available in time proportional to the alphabet instead of to
/// the range. That is what makes an exhaustive search over split points affordable. For a
/// dynamic block the figure is exact, code-length table and all — approximating that table
/// (for instance at three bits per symbol) inflates it several-fold and biases the splitter
/// towards blocks that are far too large.
/// </remarks>
internal static class ZopfliBlockCost {
  /// <summary>
  /// Builds length-limited Huffman code lengths for the given symbol counts.
  /// </summary>
  /// <param name="counts">Counts per symbol.</param>
  /// <param name="maxBits">The longest code permitted.</param>
  /// <returns>One code length per symbol, zero for symbols that do not occur.</returns>
  public static int[] BuildCodeLengths(ReadOnlySpan<long> counts, int maxBits) {
    var weights = new int[counts.Length];
    for (var i = 0; i < counts.Length; ++i)
      weights[i] = counts[i] > int.MaxValue ? int.MaxValue : (int)counts[i];

    var lengths = DeterministicHuffman.BuildCodeLengths(weights);
    HuffmanTree.LimitCodeLengths(lengths, maxBits);
    return lengths;
  }

  /// <summary>
  /// Ensures the distance alphabet has at least one code, as a dynamic block header must
  /// describe a non-empty distance tree even when the block holds no back-reference.
  /// </summary>
  /// <param name="distCounts">Counts per distance symbol; modified in place.</param>
  public static void EnsureDistanceCode(Span<long> distCounts) {
    foreach (var count in distCounts)
      if (count > 0)
        return;

    distCounts[0] = 1;
  }

  /// <summary>
  /// Number of symbols of each tree the header must describe, after trailing unused
  /// symbols have been trimmed.
  /// </summary>
  /// <param name="litLenLengths">Literal/length code lengths.</param>
  /// <param name="distLengths">Distance code lengths.</param>
  /// <returns>The HLIT and HDIST counts.</returns>
  public static (int Hlit, int Hdist) TrimTrees(ReadOnlySpan<int> litLenLengths, ReadOnlySpan<int> distLengths) {
    var hlit = litLenLengths.Length;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0)
      --hlit;

    var hdist = distLengths.Length;
    while (hdist > 1 && distLengths[hdist - 1] == 0)
      --hdist;

    return (hlit, hdist);
  }

  /// <summary>
  /// The trees a dynamic block would use, and what that block would cost in bits.
  /// </summary>
  /// <param name="LitLenLengths">Literal/length code lengths.</param>
  /// <param name="DistLengths">Distance code lengths.</param>
  /// <param name="Bits">Total block size, header and all.</param>
  internal readonly record struct DynamicBlock(int[] LitLenLengths, int[] DistLengths, long Bits);

  /// <summary>
  /// Chooses the trees a dynamic block should use and reports what the block costs with
  /// them, header and code-length table included.
  /// </summary>
  /// <param name="litLenCounts">Counts per literal/length symbol, end-of-block included.</param>
  /// <param name="distCounts">Counts per distance symbol.</param>
  /// <returns>The chosen trees and the exact block size.</returns>
  /// <remarks>
  /// Two candidate tree pairs are costed and the cheaper wins: the one the counts imply
  /// directly, and the one implied by a histogram smoothed as described under
  /// <see cref="SmoothForRuns"/>. Both are measured against the real symbol counts, since
  /// smoothing changes only how the trees are shaped and described, never what the block
  /// actually contains.
  /// </remarks>
  public static DynamicBlock BuildDynamicBlock(ReadOnlySpan<long> litLenCounts, ReadOnlySpan<long> distCounts) {
    // The header must describe a distance tree even for a block that holds no
    // back-reference, so one is invented for the tree; it is not counted as an emitted
    // symbol, because it is not one.
    var distForTree = distCounts.ToArray();
    EnsureDistanceCode(distForTree);

    var plainLitLen = BuildCodeLengths(litLenCounts, DeflateConstants.MaxBits);
    var plainDist = BuildCodeLengths(distForTree, DeflateConstants.MaxBits);
    var plainBits = HeaderBits(plainLitLen, plainDist)
                    + TokenBits(litLenCounts, distCounts, plainLitLen, plainDist);

    var smoothLitLen = BuildCodeLengths(SmoothForRuns(litLenCounts), DeflateConstants.MaxBits);
    var smoothDist = BuildCodeLengths(SmoothForRuns(distForTree), DeflateConstants.MaxBits);
    var smoothBits = HeaderBits(smoothLitLen, smoothDist)
                     + TokenBits(litLenCounts, distCounts, smoothLitLen, smoothDist);

    return smoothBits < plainBits
      ? new(smoothLitLen, smoothDist, smoothBits)
      : new(plainLitLen, plainDist, plainBits);
  }

  /// <summary>
  /// Exact size in bits of a dynamic-Huffman block with the given symbol counts, including
  /// the block header and the code-length table.
  /// </summary>
  /// <param name="litLenCounts">Counts per literal/length symbol, end-of-block included.</param>
  /// <param name="distCounts">Counts per distance symbol.</param>
  /// <returns>The size in bits.</returns>
  public static long Dynamic(ReadOnlySpan<long> litLenCounts, ReadOnlySpan<long> distCounts)
    => BuildDynamicBlock(litLenCounts, distCounts).Bits;

  /// <summary>
  /// Size in bits of a dynamic block's header, including the run-length-coded description
  /// of both trees.
  /// </summary>
  /// <param name="litLenLengths">Literal/length code lengths.</param>
  /// <param name="distLengths">Distance code lengths.</param>
  /// <returns>The header size in bits.</returns>
  public static long HeaderBits(ReadOnlySpan<int> litLenLengths, ReadOnlySpan<int> distLengths) {
    var (hlit, hdist) = TrimTrees(litLenLengths, distLengths);

    var combined = new int[hlit + hdist];
    litLenLengths[..hlit].CopyTo(combined);
    distLengths[..hdist].CopyTo(combined.AsSpan(hlit));

    var runs = DeflateCodeLengthRuns.Encode(combined);
    var clCounts = new long[DeflateConstants.CodeLengthAlphabetSize];
    foreach (var run in runs)
      ++clCounts[run.Symbol];

    var clLengths = BuildCodeLengths(clCounts, DeflateConstants.MaxCodeLengthBits);

    var hclen = DeflateConstants.CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[DeflateConstants.CodeLengthOrder[hclen - 1]] == 0)
      --hclen;

    long bits = 3 + 5 + 5 + 4 + hclen * 3;
    foreach (var run in runs)
      bits += clLengths[run.Symbol] + run.ExtraBits;

    return bits;
  }

  /// <summary>
  /// Flattens stretches of nearly equal counts so that the code lengths they produce come
  /// out exactly equal.
  /// </summary>
  /// <param name="counts">The histogram to smooth.</param>
  /// <returns>A smoothed copy.</returns>
  /// <remarks>
  /// <para>
  /// A dynamic block spends real bits describing its trees, and RFC 1951 describes them
  /// with a run-length alphabet whose symbol 16 repeats the previous code length. Two
  /// symbols whose counts differ by one may land on different code lengths and break a run
  /// that would otherwise have been free; giving them the same count costs a fraction of a
  /// bit in the data and can save several in the header. The published Zopfli method does
  /// the same and keeps whichever of the two tables comes out smaller, which is why this
  /// only ever produces a candidate, never a decision.
  /// </para>
  /// <para>
  /// A stretch is flattened only when it is at least four symbols long, every count in it
  /// is non-zero, and the largest and smallest differ by at most three. Excluding zeros
  /// matters: a long run of unused symbols is already described in a handful of bits by
  /// symbols 17 and 18, and raising those counts to one would be a large loss.
  /// </para>
  /// </remarks>
  public static long[] SmoothForRuns(ReadOnlySpan<long> counts) {
    var result = counts.ToArray();

    var end = counts.Length;
    while (end > 0 && counts[end - 1] == 0)
      --end;

    var i = 0;
    while (i < end) {
      if (counts[i] == 0) {
        ++i;
        continue;
      }

      var low = counts[i];
      var high = counts[i];
      var j = i + 1;
      while (j < end && counts[j] != 0) {
        var nextLow = Math.Min(low, counts[j]);
        var nextHigh = Math.Max(high, counts[j]);
        if (nextHigh - nextLow > 3)
          break;

        low = nextLow;
        high = nextHigh;
        ++j;
      }

      var run = j - i;
      if (run >= 4 && high != low) {
        var sum = 0L;
        for (var k = i; k < j; ++k)
          sum += counts[k];

        var mean = (sum + run / 2) / run;
        if (mean < 1)
          mean = 1;

        for (var k = i; k < j; ++k)
          result[k] = mean;
      }

      i = j;
    }

    return result;
  }

  /// <summary>
  /// Exact size in bits of a static-Huffman block with the given symbol counts.
  /// </summary>
  /// <param name="litLenCounts">Counts per literal/length symbol, end-of-block included.</param>
  /// <param name="distCounts">Counts per distance symbol.</param>
  /// <returns>The size in bits.</returns>
  public static long Static(ReadOnlySpan<long> litLenCounts, ReadOnlySpan<long> distCounts)
    => 3 + TokenBits(litLenCounts, distCounts,
      DeflateConstants.GetStaticLiteralLengths(), DeflateConstants.GetStaticDistanceLengths());

  /// <summary>
  /// Size in bits of storing <paramref name="byteCount"/> bytes verbatim.
  /// </summary>
  /// <param name="byteCount">The number of bytes to store.</param>
  /// <returns>The size in bits.</returns>
  /// <remarks>
  /// A stored block is byte-aligned, so its true cost depends on where in the byte the
  /// preceding block ended. The worst case of seven padding bits is charged here rather
  /// than tracking the writer's position, because the choice this figure feeds into is
  /// never that close and a cost that does not depend on emission order is far easier to
  /// keep identical across implementations.
  /// </remarks>
  public static long Stored(long byteCount) {
    // Each stored block carries a 16-bit length and its complement, and RFC 1951 caps one
    // at 65535 bytes, so a long run of raw data needs several.
    var chunks = Math.Max(1, (byteCount + 65534) / 65535);
    return chunks * (3 + 7 + 32) + byteCount * 8;
  }

  /// <summary>
  /// The cheapest of the three block types for the given block.
  /// </summary>
  /// <param name="litLenCounts">Counts per literal/length symbol, end-of-block included.</param>
  /// <param name="distCounts">Counts per distance symbol.</param>
  /// <param name="byteCount">The number of input bytes the block covers.</param>
  /// <returns>The winning block type and its size in bits.</returns>
  public static (int BlockType, long Bits) Cheapest(
    ReadOnlySpan<long> litLenCounts, ReadOnlySpan<long> distCounts, long byteCount) {
    var stored = Stored(byteCount);
    var fixedHuffman = Static(litLenCounts, distCounts);
    var dynamicHuffman = Dynamic(litLenCounts, distCounts);

    // Ties go to the simpler type, which keeps the choice stable and the output smaller
    // to describe.
    if (stored <= fixedHuffman && stored <= dynamicHuffman)
      return (DeflateConstants.BlockTypeUncompressed, stored);

    return fixedHuffman <= dynamicHuffman
      ? (DeflateConstants.BlockTypeStaticHuffman, fixedHuffman)
      : (DeflateConstants.BlockTypeDynamicHuffman, dynamicHuffman);
  }

  private static long TokenBits(
    ReadOnlySpan<long> litLenCounts,
    ReadOnlySpan<long> distCounts,
    ReadOnlySpan<int> litLenLengths,
    ReadOnlySpan<int> distLengths) {
    var bits = 0L;

    for (var symbol = 0; symbol < litLenCounts.Length; ++symbol) {
      var count = litLenCounts[symbol];
      if (count == 0)
        continue;

      bits += count * litLenLengths[symbol];
      if (symbol > DeflateConstants.EndOfBlock)
        bits += count * DeflateConstants.LengthExtraBits[symbol - 257];
    }

    for (var symbol = 0; symbol < distCounts.Length; ++symbol) {
      var count = distCounts[symbol];
      if (count == 0)
        continue;

      bits += count * (distLengths[symbol] + DeflateConstants.DistanceExtraBits[symbol]);
    }

    return bits;
  }
}
