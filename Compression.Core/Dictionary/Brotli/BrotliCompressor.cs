namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Compresses data in the Brotli format (RFC 7932).
/// </summary>
/// <remarks>
/// <para>
/// Supports two modes:
/// <list type="bullet">
///   <item><see cref="Compress(ReadOnlySpan{byte})"/>: uncompressed meta-blocks only (fast, no ratio).</item>
///   <item><see cref="CompressLz77"/>: LZ77 plus entropy-coded meta-blocks (actual compression).</item>
/// </list>
/// </para>
/// <para>
/// The entropy-coding encoder implements the following parts of RFC 7932:
/// literal context modelling (Section 7.1, all four context modes, with the 64
/// context values clustered into several literal prefix codes and transmitted as
/// a context map per Section 7.3), the distance ring buffer including the
/// implicit-distance insert-and-copy ranges (Section 4 and Section 5), run-length
/// coded complex prefix code descriptors (Section 3.5), cost-driven meta-block
/// splitting where each meta-block independently falls back to the uncompressed
/// form (Section 9.2), and static dictionary references with their word
/// transforms (Section 8).
/// </para>
/// <para>
/// Deliberately NOT implemented: the OmitFirst1-9 word transforms (8 of the 121
/// in Appendix B, all of which carry no prefix or suffix), several block types
/// per category with block-switch commands (Section 6), non-zero NPOSTFIX/NDIRECT
/// distance parameters (Section 4), distance context modelling (Section 7.2), and
/// an optimal parse. Those are all optional encoder features; the emitted streams
/// stay fully conformant without them.
/// </para>
/// <para>
/// Every encoding decision in this class is made with integer arithmetic only, so
/// that the JavaScript port in the Cipher project produces byte-identical output.
/// </para>
/// </remarks>
public static class BrotliCompressor {
  // ---------------------------------------------------------------------------
  // Tunables. Any change here must be mirrored in Cipher's brotli.js, otherwise
  // the two implementations stop producing identical bytes.
  // ---------------------------------------------------------------------------

  /// <summary>Shortest backward reference the match finder will emit.</summary>
  private const int MinMatch = 4;

  /// <summary>Number of bits in the match finder's hash table index.</summary>
  private const int HashBits = 17;

  /// <summary>Maximum number of hash chain candidates inspected per position.</summary>
  private const int MaxChain = 256;

  /// <summary>Copy length code 23 has base 2118 and 24 extra bits.</summary>
  private const int MaxCopyLength = 8388608;

  /// <summary>
  /// Longest literal run the parse will accumulate. A run this long closes its
  /// meta-block, which keeps MLEN inside the six nibbles RFC 7932 Section 9.2
  /// allows even for input that never matches anything.
  /// </summary>
  private const int MaxLiteralRun = 4194304;

  /// <summary>Granularity at which meta-block split points may be placed.</summary>
  private const int SegmentBytes = 32768;

  /// <summary>Largest MLEN expressible with MNIBBLES=6 (RFC 7932 Section 9.2).</summary>
  private const int MaxMetaBlockBytes = 16777216;

  /// <summary>
  /// Merging two adjacent segments into one meta-block is rejected when it costs
  /// more than this many 1/256-bit units, which is roughly the size of a second
  /// meta-block header.
  /// </summary>
  private const long SplitThresholdUnits = 262144;

  /// <summary>Rough bit cost of one insert-and-copy symbol plus its length codes.</summary>
  private const int EstimatedCommandBits = 12;

  /// <summary>Rough bit cost of one explicit distance symbol, before its extra bits.</summary>
  private const int EstimatedDistanceBits = 12;

  /// <summary>
  /// Value, in bits, of one extra matched byte when two candidate references are
  /// ranked against each other. It is well below the eight bits a literal would
  /// cost, because bytes a longer reference covers would otherwise be covered by
  /// another reference rather than by literals.
  /// </summary>
  internal const int MatchRankLiteralBits = 5;

  /// <summary>
  /// Assumed cost of one literal, in bits, when deciding whether a static dictionary
  /// reference repays the command that carries it.
  /// </summary>
  internal const int DictionaryLiteralBits = 8;

  /// <summary>How much better a reference one position later has to score to be preferred.</summary>
  private const int LazyMatchMargin = 8;

  /// <summary>Candidate literal prefix code counts (NTREESL) evaluated per meta-block.</summary>
  private static readonly int[] LiteralTreeCandidates = [1, 2, 4, 8, 16];

  /// <summary>Initial contents of the distance ring buffer (RFC 7932 Section 4).</summary>
  private static readonly int[] InitialDistanceRing = [4, 11, 15, 16];

  /// <summary>
  /// RFC 7932 Table 8: (insertCodeBase, copyCodeBase, firstCode, usesImplicitDistance).
  /// Ranges 0-1 (codes 0-127) take the distance from the ring buffer without
  /// reading a distance symbol; ranges 2-10 (codes 128-703) read one.
  /// </summary>
  private static readonly (int InsertBase, int CopyBase, int CodeBase, bool Implicit)[] IacRanges = [
    (0, 0, 0, true),
    (0, 8, 64, true),
    (0, 0, 128, false),
    (0, 8, 192, false),
    (8, 0, 256, false),
    (8, 8, 320, false),
    (0, 16, 384, false),
    (16, 0, 448, false),
    (8, 16, 512, false),
    (16, 8, 576, false),
    (16, 16, 640, false)
  ];

  /// <summary>Size of the distance alphabet for NPOSTFIX=0, NDIRECT=0.</summary>
  private const int DistanceAlphabetSize = 64;

  // ---------------------------------------------------------------------------
  // Public API
  // ---------------------------------------------------------------------------

  /// <summary>
  /// Compresses data to the Brotli format at the specified compression level.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, BrotliCompressionLevel level) =>
    level == BrotliCompressionLevel.Uncompressed ? Compress(data) : CompressLz77(data);

  /// <summary>
  /// Compresses data to the Brotli format using uncompressed meta-blocks.
  /// Fast encoding with no compression ratio improvement.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var writer = new BrotliBitWriter();

    // WBITS = 16 is a single zero bit (RFC 7932 Section 9.1).
    writer.WriteBits(1, 0);

    // Per RFC 7932 Section 9.2 ISUNCOMPRESSED only exists when ISLAST=0, so every
    // data-carrying meta-block is non-last and a true empty last one terminates.
    var offset = 0;
    while (offset < data.Length) {
      var blockSize = Math.Min(data.Length - offset, 65536);
      writer.WriteBits(1, 0); // ISLAST = 0
      WriteMetaBlockLength(writer, blockSize);
      writer.WriteBits(1, 1); // ISUNCOMPRESSED = 1
      writer.AlignToByte();
      for (var i = 0; i < blockSize; ++i)
        writer.WriteBits(8, data[offset + i]);

      offset += blockSize;
    }

    writer.WriteBits(1, 1); // ISLAST = 1
    writer.WriteBits(1, 1); // ISLASTEMPTY = 1
    writer.Flush();
    return writer.ToArray();
  }

  /// <summary>
  /// Compresses data to the Brotli format using entropy-coded meta-blocks.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] CompressLz77(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return Compress(data);

    var input = data.ToArray();
    var windowBits = ComputeWindowBits(input.Length);
    var maxDistance = (1 << windowBits) - 16;

    var commands = FindCommands(input, maxDistance);
    var blocks = SplitMetaBlocks(input, commands);

    var writer = new BrotliBitWriter();
    WriteWindowBits(writer, windowBits);

    var ring = (int[])InitialDistanceRing.Clone();
    for (var bi = 0; bi < blocks.Count; ++bi) {
      var block = blocks[bi];
      var isLastBlock = bi == blocks.Count - 1;

      var candidateRing = (int[])ring.Clone();
      var compressed = BuildCompressedMetaBlock(input, commands, block, isLastBlock, candidateRing);
      var stored = BuildUncompressedMetaBlock(input, block, writer.BitLength());

      bool useCompressed;
      if (isLastBlock) {
        // The stream ends here: the compressed form can carry ISLAST=1 directly,
        // while the uncompressed form needs a trailing empty last meta-block.
        var withCompressed = ByteLength(writer.BitLength() + compressed.BitLength());
        var withStored = ByteLength(writer.BitLength() + stored.BitLength() + 2);
        useCompressed = withCompressed <= withStored;
      } else
        useCompressed = compressed.BitLength() < stored.BitLength();

      if (useCompressed) {
        writer.Append(compressed);
        ring = candidateRing;
      } else
        writer.Append(stored);

      if (!isLastBlock || useCompressed)
        continue;

      writer.WriteBits(1, 1); // ISLAST = 1
      writer.WriteBits(1, 1); // ISLASTEMPTY = 1
    }

    writer.Flush();
    return writer.ToArray();
  }

  /// <summary>Number of whole bytes needed to hold <paramref name="bits"/> bits.</summary>
  private static int ByteLength(int bits) => (bits + 7) / 8;

  // ---------------------------------------------------------------------------
  // Stream header
  // ---------------------------------------------------------------------------

  /// <summary>Picks the smallest window that can express every backward distance.</summary>
  private static int ComputeWindowBits(int dataLength) {
    for (var bits = BrotliConstants.MinWindowBits; bits < BrotliConstants.MaxWindowBits; ++bits)
      if ((1 << bits) - 16 >= dataLength)
        return bits;

    return BrotliConstants.MaxWindowBits;
  }

  /// <summary>Writes the WBITS field of the stream header (RFC 7932 Section 9.1).</summary>
  private static void WriteWindowBits(BrotliBitWriter writer, int windowBits) {
    switch (windowBits) {
      case 16:
        writer.WriteBits(1, 0);
        return;
      case >= 18 and <= 24:
        writer.WriteBits(1, 1);
        writer.WriteBits(3, (uint)(windowBits - 17));
        return;
      case 17:
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 0);
        writer.WriteBits(3, 0);
        return;
      case >= 10 and <= 15:
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 0);
        writer.WriteBits(3, (uint)(windowBits - 8));
        return;
      default:
        throw new ArgumentOutOfRangeException(nameof(windowBits), windowBits, "Unsupported Brotli window size.");
    }
  }

  /// <summary>
  /// Writes MNIBBLES and MLEN (RFC 7932 Section 9.2). MNIBBLES must be the
  /// smallest nibble count whose most significant nibble is non-zero, because a
  /// conformant decoder rejects a stream whose last nibble is all zeros.
  /// </summary>
  private static void WriteMetaBlockLength(BrotliBitWriter writer, int byteLength) {
    var mlen = byteLength - 1;
    var nibbles = mlen <= 0xFFFF ? 4 : mlen <= 0xFFFFF ? 5 : 6;
    writer.WriteBits(2, (uint)(nibbles - 4));
    for (var i = 0; i < nibbles; ++i)
      writer.WriteBits(4, (uint)((mlen >> (i * 4)) & 0xF));
  }

  // ---------------------------------------------------------------------------
  // Deterministic integer cost model
  //
  // Every size comparison the encoder makes has to be reproduced bit-for-bit by
  // the JavaScript port. Floating point logarithms are not safe for that (the
  // last unit in the last place may differ between runtimes), so all costs are
  // computed from an exact fixed-point base-2 logarithm and carried in units of
  // 1/256 bit.
  // ---------------------------------------------------------------------------

  private static readonly int[] Log2Table = BuildLog2Table();

  private static int[] BuildLog2Table() {
    var table = new int[65536];
    for (var i = 1; i < 65536; ++i)
      table[i] = (int)ComputeLog2Fixed(i);
    return table;
  }

  /// <summary>
  /// Returns floor(log2(<paramref name="x"/>) * 65536) using integer arithmetic
  /// only, for <paramref name="x"/> greater than or equal to 1.
  /// </summary>
  private static long ComputeLog2Fixed(long x) {
    long exponent = 0;
    var v = x;
    while (v >= 2) {
      v /= 2;
      ++exponent;
    }

    // Mantissa in [1, 2) held as a fixed point number with 20 fractional bits.
    var mantissa = x * 1048576L / (1L << (int)exponent);
    var result = exponent * 65536L;
    var bit = 32768L;
    for (var i = 0; i < 16; ++i) {
      mantissa = mantissa * mantissa / 1048576L;
      if (mantissa >= 2097152L) {
        result += bit;
        mantissa /= 2;
      }

      bit /= 2;
    }

    return result;
  }

  /// <summary>Table-accelerated <see cref="ComputeLog2Fixed"/>.</summary>
  private static long Log2Fixed(long x) => x < 65536 ? Log2Table[(int)x] : ComputeLog2Fixed(x);

  /// <summary>
  /// Ideal cost, in 1/256-bit units, of coding <paramref name="count"/>
  /// occurrences of one symbol inside an alphabet seen <paramref name="total"/> times.
  /// </summary>
  private static long BitCostUnits(long count, long total) {
    if (count <= 0)
      return 0;

    // Clamped so the division below can never see a negative numerator: integer
    // division truncates towards zero in C# but towards minus infinity in the
    // JavaScript port, and the two must not be able to disagree.
    var delta = Log2Fixed(total) - Log2Fixed(count);
    return delta <= 0 ? 0 : count * delta / 256;
  }

  /// <summary>Ideal cost, in 1/256-bit units, of coding a whole histogram.</summary>
  private static long HistogramCostUnits(int[] histogram) {
    long total = 0;
    for (var i = 0; i < histogram.Length; ++i)
      total += histogram[i];

    if (total == 0)
      return 0;

    long cost = 0;
    for (var i = 0; i < histogram.Length; ++i)
      cost += BitCostUnits(histogram[i], total);

    return cost;
  }

  /// <summary>
  /// Extra cost, in 1/256-bit units, of coding two histograms with one shared
  /// distribution instead of two separate ones. Never negative.
  /// </summary>
  private static long MergeCostUnits(int[] a, int[] b) {
    long totalA = 0, totalB = 0;
    for (var i = 0; i < a.Length; ++i) {
      totalA += a[i];
      totalB += b[i];
    }

    var totalMerged = totalA + totalB;
    if (totalMerged == 0)
      return 0;

    long cost = 0;
    for (var i = 0; i < a.Length; ++i) {
      var fa = a[i];
      var fb = b[i];
      cost += BitCostUnits(fa + fb, totalMerged) - BitCostUnits(fa, totalA) - BitCostUnits(fb, totalB);
    }

    return cost;
  }

  // ---------------------------------------------------------------------------
  // Prefix codes (RFC 7932 Section 3)
  // ---------------------------------------------------------------------------

  /// <summary>A canonical prefix code together with its transmitted code lengths.</summary>
  private sealed class PrefixCode {
    /// <summary>Code length per symbol; zero means the symbol is not coded.</summary>
    public int[] Lengths = [];

    /// <summary>Canonical code value per symbol, valid where the length is non-zero.</summary>
    public int[] Codes = [];

    /// <summary>The only coded symbol, or -1 when the code has two or more symbols.</summary>
    public int SingleSymbol = -1;
  }

  /// <summary>
  /// Builds optimal length-limited code lengths with the package-merge algorithm
  /// (Larmore and Hirschberg, 1990). RFC 7932 Section 3.2 caps code lengths at 15
  /// bits for the data alphabets and Section 3.5 at 5 bits for the code-length
  /// alphabet, which an unrestricted Huffman build can exceed.
  /// </summary>
  private static int[] BuildCodeLengths(int[] frequencies, int alphabetSize, int maxLength) {
    var lengths = new int[alphabetSize];

    var used = new List<int>();
    for (var symbol = 0; symbol < alphabetSize; ++symbol)
      if (frequencies[symbol] > 0)
        used.Add(symbol);

    switch (used.Count) {
      case 0: return lengths;
      case 1:
        lengths[used[0]] = 1;
        return lengths;
    }

    var basis = new List<(long Weight, List<int> Symbols)>(used.Count);
    foreach (var symbol in used)
      basis.Add((frequencies[symbol], [symbol]));

    // Ascending by weight, ties broken by the smaller symbol so the ordering is
    // reproducible in any language.
    basis.Sort((x, y) => x.Weight != y.Weight ? x.Weight.CompareTo(y.Weight) : x.Symbols[0].CompareTo(y.Symbols[0]));

    var list = basis;
    for (var level = 2; level <= maxLength; ++level) {
      var packaged = new List<(long Weight, List<int> Symbols)>(list.Count / 2);
      for (var i = 0; i + 1 < list.Count; i += 2) {
        var combined = new List<int>(list[i].Symbols.Count + list[i + 1].Symbols.Count);
        combined.AddRange(list[i].Symbols);
        combined.AddRange(list[i + 1].Symbols);
        packaged.Add((list[i].Weight + list[i + 1].Weight, combined));
      }

      list = MergeAscending(packaged, basis);
    }

    var take = Math.Min(2 * used.Count - 2, list.Count);
    for (var i = 0; i < take; ++i)
      foreach (var symbol in list[i].Symbols)
        ++lengths[symbol];

    return lengths;
  }

  /// <summary>
  /// Merges two weight-ascending lists into one, preferring the first list on
  /// ties so the result is independent of any sort implementation.
  /// </summary>
  private static List<(long Weight, List<int> Symbols)> MergeAscending(
    List<(long Weight, List<int> Symbols)> first,
    List<(long Weight, List<int> Symbols)> second) {
    var merged = new List<(long Weight, List<int> Symbols)>(first.Count + second.Count);
    int i = 0, j = 0;
    while (i < first.Count && j < second.Count)
      if (first[i].Weight <= second[j].Weight)
        merged.Add(first[i++]);
      else
        merged.Add(second[j++]);

    while (i < first.Count)
      merged.Add(first[i++]);
    while (j < second.Count)
      merged.Add(second[j++]);

    return merged;
  }

  /// <summary>
  /// Rewrites code lengths that will be transmitted as a simple prefix code
  /// (RFC 7932 Section 3.4). The implied lengths are positional, and the symbols
  /// are always written in ascending order, so the shortest code goes to the
  /// smallest symbol - exactly what a canonical code does for equal lengths.
  /// </summary>
  private static void NormalizeSimpleCode(int[] lengths, int alphabetSize) {
    var used = new List<int>();
    for (var symbol = 0; symbol < alphabetSize; ++symbol)
      if (lengths[symbol] > 0)
        used.Add(symbol);

    switch (used.Count) {
      case 2:
        lengths[used[0]] = 1;
        lengths[used[1]] = 1;
        return;
      case 3:
        lengths[used[0]] = 1;
        lengths[used[1]] = 2;
        lengths[used[2]] = 2;
        return;
      case 4:
        foreach (var symbol in used)
          lengths[symbol] = 2;
        return;
    }
  }

  /// <summary>Assigns canonical code values to a set of code lengths.</summary>
  private static PrefixCode MakePrefixCode(int[] lengths, int alphabetSize) {
    var code = new PrefixCode { Lengths = lengths, Codes = new int[alphabetSize] };

    int usedCount = 0, lastSymbol = 0, maxLength = 0;
    for (var symbol = 0; symbol < alphabetSize; ++symbol) {
      if (lengths[symbol] <= 0)
        continue;

      ++usedCount;
      lastSymbol = symbol;
      maxLength = Math.Max(maxLength, lengths[symbol]);
    }

    if (usedCount <= 1) {
      code.SingleSymbol = lastSymbol;
      return code;
    }

    var lengthCounts = new int[maxLength + 1];
    for (var symbol = 0; symbol < alphabetSize; ++symbol)
      if (lengths[symbol] > 0)
        ++lengthCounts[lengths[symbol]];

    var nextCode = new int[maxLength + 1];
    var value = 0;
    for (var bits = 1; bits <= maxLength; ++bits) {
      value = (value + lengthCounts[bits - 1]) << 1;
      nextCode[bits] = value;
    }

    for (var symbol = 0; symbol < alphabetSize; ++symbol) {
      var length = lengths[symbol];
      if (length > 0)
        code.Codes[symbol] = nextCode[length]++;
    }

    return code;
  }

  /// <summary>Builds the prefix code the encoder will actually use for a histogram.</summary>
  private static PrefixCode BuildPrefixCode(int[] frequencies, int alphabetSize, int maxLength) {
    var lengths = BuildCodeLengths(frequencies, alphabetSize, maxLength);

    var usedCount = 0;
    for (var symbol = 0; symbol < alphabetSize; ++symbol)
      if (lengths[symbol] > 0)
        ++usedCount;

    // An alphabet nothing was coded from still needs a descriptor; the NSYM=1
    // simple form costs the fewest bits and decodes to a zero-bit code.
    if (usedCount == 0)
      lengths[0] = 1;
    else if (usedCount <= 4)
      NormalizeSimpleCode(lengths, alphabetSize);

    return MakePrefixCode(lengths, alphabetSize);
  }

  /// <summary>
  /// Writes one symbol, most significant bit of the canonical code first, which is
  /// how a decoder reading bit by bit reconstructs the tree path.
  /// </summary>
  private static void WriteSymbol(BrotliBitWriter writer, PrefixCode code, int symbol) {
    if (code.SingleSymbol >= 0)
      return; // zero-bit code

    var length = code.Lengths[symbol];
    var value = code.Codes[symbol];
    for (var i = length - 1; i >= 0; --i)
      writer.WriteBits(1, (uint)((value >> i) & 1));
  }

  /// <summary>Bits used to code one symbol with the given code.</summary>
  private static int SymbolBits(PrefixCode code, int symbol) =>
    code.SingleSymbol >= 0 ? 0 : code.Lengths[symbol];

  /// <summary>Number of bits an alphabet index occupies in a simple prefix code.</summary>
  private static int AlphabetBits(int alphabetSize) {
    var bits = 1;
    while ((1 << bits) < alphabetSize)
      ++bits;
    return bits;
  }

  /// <summary>
  /// Writes a prefix code descriptor. Alphabets with at most four coded symbols
  /// use the simple form of RFC 7932 Section 3.4; everything else uses the
  /// complex form of Section 3.5.
  /// </summary>
  private static void WritePrefixCodeDescriptor(BrotliBitWriter writer, PrefixCode code, int alphabetSize) {
    var used = new List<int>();
    for (var symbol = 0; symbol < alphabetSize; ++symbol)
      if (code.Lengths[symbol] > 0)
        used.Add(symbol);

    if (used.Count > 4) {
      WriteComplexPrefixCodeDescriptor(writer, code.Lengths, alphabetSize);
      return;
    }

    writer.WriteBits(2, 1); // HSKIP = 1 selects the simple form
    writer.WriteBits(2, (uint)(used.Count - 1)); // NSYM - 1

    var symbolBits = AlphabetBits(alphabetSize);
    foreach (var symbol in used)
      writer.WriteBits(symbolBits, (uint)symbol);

    // tree-select 0 gives all four symbols length 2; the 1/2/3/3 shape is never
    // used because its lengths would then follow symbol order, not frequency.
    if (used.Count == 4)
      writer.WriteBits(1, 0);
  }

  /// <summary>Measures a descriptor without emitting it.</summary>
  private static int MeasureDescriptorBits(PrefixCode code, int alphabetSize) {
    var scratch = new BrotliBitWriter();
    WritePrefixCodeDescriptor(scratch, code, alphabetSize);
    return scratch.BitLength();
  }

  /// <summary>One entry of a complex prefix code descriptor's symbol stream.</summary>
  private readonly record struct CodeLengthEmission(int Symbol, int ExtraBits, int ExtraValue);

  /// <summary>
  /// Writes a complex prefix code descriptor (RFC 7932 Section 3.5). Two symbol
  /// streams are planned - one that spells out every code length and one that
  /// folds runs into the repeat codes 16 and 17 - and the cheaper one wins.
  /// </summary>
  private static void WriteComplexPrefixCodeDescriptor(BrotliBitWriter writer, int[] lengths, int alphabetSize) {
    var lastNonZero = 0;
    for (var symbol = alphabetSize - 1; symbol >= 0; --symbol)
      if (lengths[symbol] > 0) {
        lastNonZero = symbol;
        break;
      }

    var plain = PlanPlainEmissions(lengths, lastNonZero);
    var runLength = PlanRunLengthEmissions(lengths, lastNonZero);

    var plainBits = MeasureEmissions(plain);
    var runLengthBits = MeasureEmissions(runLength);
    var chosen = plainBits >= 0 && (runLengthBits < 0 || plainBits <= runLengthBits) ? plain : runLength;

    EmitComplexPrefixCodeDescriptor(writer, chosen);
  }

  /// <summary>
  /// Bits a planned emission stream occupies, or -1 when the plan is unusable
  /// because its code-length alphabet would hold a single symbol (an incomplete
  /// code that decoders are not required to accept in this position).
  /// </summary>
  private static int MeasureEmissions(List<CodeLengthEmission> emissions) {
    var distinct = 0;
    var seen = new bool[BrotliConstants.NumCodeLengthCodes];
    foreach (var emission in emissions)
      if (!seen[emission.Symbol]) {
        seen[emission.Symbol] = true;
        ++distinct;
      }

    if (distinct < 2)
      return -1;

    var scratch = new BrotliBitWriter();
    EmitComplexPrefixCodeDescriptor(scratch, emissions);
    return scratch.BitLength();
  }

  /// <summary>Emits a planned complex prefix code descriptor.</summary>
  private static void EmitComplexPrefixCodeDescriptor(BrotliBitWriter writer, List<CodeLengthEmission> emissions) {
    var frequencies = new int[BrotliConstants.NumCodeLengthCodes];
    foreach (var emission in emissions)
      ++frequencies[emission.Symbol];

    var clLengths = BuildCodeLengths(frequencies, BrotliConstants.NumCodeLengthCodes, 5);
    var clCode = MakePrefixCode(clLengths, BrotliConstants.NumCodeLengthCodes);

    writer.WriteBits(2, 0); // HSKIP = 0

    // The decoder stops reading code-length code lengths once the Kraft sum
    // (tracked as `space`, scaled by 32) is exhausted, so the writer stops at
    // exactly the same point.
    var space = 32;
    for (var i = 0; i < BrotliConstants.NumCodeLengthCodes && space > 0; ++i) {
      var index = BrotliConstants.CodeLengthCodeOrder[i];
      var length = clLengths[index];
      WriteCodeLengthCodeLength(writer, length);
      if (length != 0)
        space -= 32 >> length;
    }

    foreach (var emission in emissions) {
      WriteSymbol(writer, clCode, emission.Symbol);
      if (emission.ExtraBits > 0)
        writer.WriteBits(emission.ExtraBits, (uint)emission.ExtraValue);
    }
  }

  /// <summary>Plans a descriptor that spells out every code length individually.</summary>
  private static List<CodeLengthEmission> PlanPlainEmissions(int[] lengths, int lastNonZero) {
    var emissions = new List<CodeLengthEmission>(lastNonZero + 1);
    for (var symbol = 0; symbol <= lastNonZero; ++symbol)
      emissions.Add(new CodeLengthEmission(lengths[symbol], 0, 0));
    return emissions;
  }

  /// <summary>Plans a descriptor that folds runs into the repeat codes 16 and 17.</summary>
  private static List<CodeLengthEmission> PlanRunLengthEmissions(int[] lengths, int lastNonZero) {
    var emissions = new List<CodeLengthEmission>();
    var i = 0;
    while (i <= lastNonZero) {
      var length = lengths[i];
      var runEnd = i;
      while (runEnd + 1 <= lastNonZero && lengths[runEnd + 1] == length)
        ++runEnd;

      var runLength = runEnd - i + 1;
      if (length == 0 && runLength >= 3)
        PlanZeroRun(emissions, runLength);
      else if (length > 0 && runLength >= 4) {
        emissions.Add(new CodeLengthEmission(length, 0, 0));
        PlanRepeatRun(emissions, runLength - 1);
      } else
        for (var j = 0; j < runLength; ++j)
          emissions.Add(new CodeLengthEmission(length, 0, 0));

      i = runEnd + 1;
    }

    return emissions;
  }

  /// <summary>
  /// Plans a run of at least three zeros with code 17. Consecutive 17s chain in
  /// the decoder as run = ((run - 2) * 8) + delta + 3, so N emissions with deltas
  /// d(1)..d(N) produce sum(8^(N-i) * d(i)) + (8^N + 13) / 7 zeros.
  /// </summary>
  private static void PlanZeroRun(List<CodeLengthEmission> emissions, int count) {
    var n = 1;
    while (((1L << (3 * (n + 1))) + 6) / 7 < count)
      ++n;

    var remaining = count - ((1L << (3 * n)) + 13) / 7;
    for (var i = 0; i < n; ++i) {
      var weight = 1L << (3 * (n - 1 - i));
      var delta = (int)Math.Min(remaining / weight, 7);
      emissions.Add(new CodeLengthEmission(17, 3, delta));
      remaining -= delta * weight;
    }
  }

  /// <summary>
  /// Plans a repeat of the previous non-zero length with code 16. Consecutive 16s
  /// chain as run = ((run - 2) * 4) + delta + 3, so N emissions with deltas
  /// d(1)..d(N) produce sum(4^(N-i) * d(i)) + (4^N + 5) / 3 repeats.
  /// </summary>
  private static void PlanRepeatRun(List<CodeLengthEmission> emissions, int count) {
    var n = 1;
    while (((1L << (2 * (n + 1))) + 2) / 3 < count)
      ++n;

    var remaining = count - ((1L << (2 * n)) + 5) / 3;
    for (var i = 0; i < n; ++i) {
      var weight = 1L << (2 * (n - 1 - i));
      var delta = (int)Math.Min(remaining / weight, 3);
      emissions.Add(new CodeLengthEmission(16, 2, delta));
      remaining -= delta * weight;
    }
  }

  /// <summary>
  /// Writes one code length of the code-length alphabet using the fixed prefix
  /// code of RFC 7932 Section 3.5.
  /// </summary>
  private static void WriteCodeLengthCodeLength(BrotliBitWriter writer, int length) {
    switch (length) {
      case 0: writer.WriteBits(2, 0); return;  // 00
      case 3: writer.WriteBits(2, 2); return;  // 10
      case 4: writer.WriteBits(2, 1); return;  // 01
      case 2: writer.WriteBits(3, 3); return;  // 011
      case 1: writer.WriteBits(4, 7); return;  // 0111
      case 5: writer.WriteBits(4, 15); return; // 1111
      default: throw new ArgumentOutOfRangeException(nameof(length), length, "Code length code length must be 0-5.");
    }
  }

  /// <summary>
  /// Writes a block-type or tree count using the variable-length code of
  /// RFC 7932 Section 9.2: 1 is a single zero bit, 2 is "1" plus three zero bits,
  /// and any larger N is "1", three bits of nbits, then nbits of N - 1 - 2^nbits.
  /// </summary>
  private static void WriteCount(BrotliBitWriter writer, int count) {
    if (count == 1) {
      writer.WriteBits(1, 0);
      return;
    }

    writer.WriteBits(1, 1);
    var value = count - 1;
    if (value == 1) {
      writer.WriteBits(3, 0);
      return;
    }

    var bits = 0;
    var v = value;
    while (v > 1) {
      v /= 2;
      ++bits;
    }

    writer.WriteBits(3, (uint)bits);
    writer.WriteBits(bits, (uint)(value - (1 << bits)));
  }

  // ---------------------------------------------------------------------------
  // Insert-and-copy and distance codes (RFC 7932 Sections 4 and 5)
  // ---------------------------------------------------------------------------

  /// <summary>Finds the length code bucket holding <paramref name="value"/>.</summary>
  private static int FindLengthCode((int BaseValue, int ExtraBits)[] table, int value) {
    for (var i = table.Length - 1; i >= 0; --i)
      if (value >= table[i].BaseValue)
        return i;

    return 0;
  }

  /// <summary>Combines an insert length code and a copy length code (RFC 7932 Table 8).</summary>
  private static int EncodeInsertAndCopyCode(int insertCode, int copyCode, bool implicitDistance) {
    foreach (var (insertBase, copyBase, codeBase, isImplicit) in IacRanges) {
      if (isImplicit != implicitDistance)
        continue;

      var insertOffset = insertCode - insertBase;
      var copyOffset = copyCode - copyBase;
      if (insertOffset is >= 0 and <= 7 && copyOffset is >= 0 and <= 7)
        return codeBase + insertOffset * 8 + copyOffset;
    }

    return -1;
  }

  /// <summary>
  /// Returns the ring buffer distance code 0-15 that reproduces
  /// <paramref name="distance"/>, or -1 when none does (RFC 7932 Section 4).
  /// </summary>
  private static int FindRingDistanceCode(int distance, int[] ring) {
    for (var i = 0; i < 4; ++i)
      if (distance == ring[i])
        return i;

    if (distance == ring[0] - 1) return 4;
    if (distance == ring[0] + 1) return 5;
    if (distance == ring[0] - 2) return 6;
    if (distance == ring[0] + 2) return 7;
    if (distance == ring[0] - 3) return 8;
    if (distance == ring[0] + 3) return 9;
    if (distance == ring[1] - 1) return 10;
    if (distance == ring[1] + 1) return 11;
    if (distance == ring[1] - 2) return 12;
    if (distance == ring[1] + 2) return 13;
    if (distance == ring[1] - 3) return 14;
    if (distance == ring[1] + 3) return 15;
    return -1;
  }

  /// <summary>
  /// Inverts the NPOSTFIX=0, NDIRECT=0 distance formula of RFC 7932 Section 4:
  /// for code 16 + b the decoder reads nbits = 1 + b / 2 extra bits and forms
  /// ((2 + (b mod 2)) * 2^nbits) - 4 + extra + 1.
  /// </summary>
  private static (int Code, int ExtraBits, int ExtraValue) EncodeDistance(int distance) {
    for (var b = 0; b < 48; ++b) {
      var extraBits = 1 + b / 2;
      var offset = ((2 + b % 2) << extraBits) - 4;
      var first = offset + 1;
      var last = offset + (1 << extraBits);
      if (distance >= first && distance <= last)
        return (16 + b, extraBits, distance - first);
    }

    throw new ArgumentOutOfRangeException(nameof(distance), distance, "Distance is outside the representable range.");
  }

  /// <summary>Number of extra distance bits a distance would need as a complex code.</summary>
  private static int DistanceExtraBits(int distance) {
    for (var b = 0; b < 48; ++b) {
      var extraBits = 1 + b / 2;
      var offset = ((2 + b % 2) << extraBits) - 4;
      if (distance >= offset + 1 && distance <= offset + (1 << extraBits))
        return extraBits;
    }

    return 24;
  }

  // ---------------------------------------------------------------------------
  // Match finding (LZ77 parse)
  // ---------------------------------------------------------------------------

  /// <summary>
  /// A parsed command: a literal run followed by an optional reference. <c>CopyLength</c> is
  /// what the copy length code carries, <c>OutputLength</c> is how many input bytes the
  /// reference covers; the two differ only for static dictionary references whose transform
  /// changes the word length.
  /// </summary>
  private readonly record struct Command(
    int InsertStart, int InsertLength, int CopyLength, int OutputLength, int Distance, bool IsDictionary) {
    /// <summary>First byte position after the command.</summary>
    public int End => this.InsertStart + this.InsertLength + this.OutputLength;
  }

  /// <summary>Best reference of either kind at one position.</summary>
  private readonly record struct Reference(
    int CopyLength, int OutputLength, int Distance, int Score, bool IsDictionary);

  /// <summary>The absence of any usable reference.</summary>
  private static readonly Reference NoReference = new(0, 0, 0, int.MinValue, false);

  /// <summary>
  /// Best reference at one position: either an in-window backward match or a static
  /// dictionary word, whichever the cost model ranks higher.
  /// </summary>
  private static Reference FindBestReference(
    byte[] data, int position, int maxDistance, int[] head, int[] chain, int[] parseRing) {
    var window = FindBestMatch(data, position, maxDistance, head, chain, parseRing);
    var hasDictionary = BrotliDictionaryMatcher.TryFindMatch(
      data, position, Math.Min(maxDistance, position), out var dictionary);

    if (hasDictionary && (window.Length < MinMatch || dictionary.Score > window.Score))
      return new Reference(dictionary.CopyLength, dictionary.OutputLength,
        dictionary.Distance, dictionary.Score, true);

    return window.Length < MinMatch
      ? NoReference
      : new Reference(window.Length, window.Length, window.Distance, window.Score, false);
  }

  /// <summary>Hash of the four bytes at <paramref name="position"/>.</summary>
  private static int HashAt(byte[] data, int position) {
    var word = (uint)((data[position] << 24) | (data[position + 1] << 16) |
                      (data[position + 2] << 8) | data[position + 3]);
    return (int)(word * 2654435761u >> (32 - HashBits));
  }

  /// <summary>Length of the common prefix of two positions, capped at <paramref name="maxLength"/>.</summary>
  private static int MatchLength(byte[] data, int a, int b, int maxLength) {
    var length = 0;
    while (length < maxLength && data[a + length] == data[b + length])
      ++length;
    return length;
  }

  /// <summary>
  /// Approximate cost in bits of a backward reference, used only to steer the
  /// parse. A ring buffer distance is assumed to cost three bits, an explicit one
  /// twelve plus its extra bits.
  /// </summary>
  private static int MatchCost(int length, int distance, bool inRing) {
    var copyCode = FindLengthCode(BrotliConstants.CopyLengthTable, length);
    var cost = EstimatedCommandBits + BrotliConstants.CopyLengthTable[copyCode].ExtraBits;
    cost += inRing ? 3 : EstimatedDistanceBits + DistanceExtraBits(distance);
    return cost;
  }

  /// <summary>
  /// Ranks two candidate references against each other. Extra matched bytes are
  /// only worth what the command that would otherwise cover them costs, not a
  /// full literal each, so long far references do not automatically beat short
  /// near ones.
  /// </summary>
  private static int MatchScore(int length, int distance, bool inRing) =>
    length * MatchRankLiteralBits - MatchCost(length, distance, inRing);

  /// <summary>Whether coding a reference beats coding the same bytes as literals.</summary>
  private static bool MatchPaysOff(int length, int distance, bool inRing) =>
    length * 8 > MatchCost(length, distance, inRing);

  /// <summary>
  /// Approximate bit cost of a static dictionary reference. The copy length code carries
  /// the base word length; the distance is always explicit, because the ring buffer never
  /// holds a dictionary distance (RFC 7932 Section 4).
  /// </summary>
  /// <param name="copyLength">The base word length.</param>
  /// <param name="distance">The distance value addressing the word and transform.</param>
  /// <returns>The estimated cost in bits.</returns>
  internal static int DictionaryMatchCost(int copyLength, int distance) {
    var copyCode = FindLengthCode(BrotliConstants.CopyLengthTable, copyLength);
    return EstimatedCommandBits + BrotliConstants.CopyLengthTable[copyCode].ExtraBits +
      EstimatedDistanceBits + DistanceExtraBits(distance);
  }

  /// <summary>Best backward reference at one position, or a zero length when there is none.</summary>
  private static (int Length, int Distance, int Score) FindBestMatch(
    byte[] data, int position, int maxDistance, int[] head, int[] chain, int[] parseRing) {
    var maxLength = Math.Min(MaxCopyLength, data.Length - position);
    if (maxLength < MinMatch || position + MinMatch > data.Length)
      return (0, 0, int.MinValue);

    int bestLength = 0, bestDistance = 0, bestScore = int.MinValue;

    // Distances already in the ring buffer code for almost nothing, so they are
    // worth trying even when the hash chain offers a longer match elsewhere.
    for (var i = 0; i < 4; ++i) {
      var distance = parseRing[i];
      if (distance > position || distance > maxDistance)
        continue;

      var length = MatchLength(data, position, position - distance, maxLength);
      if (length < MinMatch || !MatchPaysOff(length, distance, true))
        continue;

      var score = MatchScore(length, distance, true);
      if (score <= bestScore)
        continue;

      bestScore = score;
      bestLength = length;
      bestDistance = distance;
    }

    var candidate = head[HashAt(data, position)];
    var depth = 0;
    while (candidate >= 0 && depth < MaxChain) {
      var distance = position - candidate;
      if (distance > maxDistance)
        break;

      if (distance > 0) {
        var length = MatchLength(data, position, candidate, maxLength);
        if (length >= MinMatch && MatchPaysOff(length, distance, false)) {
          var score = MatchScore(length, distance, false);
          if (score > bestScore) {
            bestScore = score;
            bestLength = length;
            bestDistance = distance;
          }
        }
      }

      candidate = chain[candidate];
      ++depth;
    }

    return (bestLength, bestDistance, bestScore);
  }

  /// <summary>
  /// Splits the input into insert-and-copy commands using a hash chain match
  /// finder with one step of lazy matching driven by the approximate bit cost.
  /// </summary>
  private static List<Command> FindCommands(byte[] data, int maxDistance) {
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var chain = new int[Math.Max(1, data.Length)];
    Array.Fill(chain, -1);

    // Mirrors the real distance ring buffer closely enough to steer the parse;
    // the codes actually emitted are resolved later against the true ring.
    var parseRing = (int[])InitialDistanceRing.Clone();

    var commands = new List<Command>();
    var literalStart = 0;
    var position = 0;

    void Insert(int at) {
      if (at + MinMatch > data.Length)
        return;

      var h = HashAt(data, at);
      chain[at] = head[h];
      head[h] = at;
    }

    while (position < data.Length) {
      // A literal-only command is only legal as the last command of a meta-block,
      // and SplitMetaBlocks closes a meta-block right after one, so capping the
      // run here bounds MLEN without making the stream illegal.
      if (position - literalStart >= MaxLiteralRun) {
        commands.Add(new Command(literalStart, position - literalStart, 0, 0, 0, false));
        literalStart = position;
      }

      var best = FindBestReference(data, position, maxDistance, head, chain, parseRing);
      if (best.OutputLength < MinMatch) {
        Insert(position);
        ++position;
        continue;
      }

      Insert(position);
      if (position + 1 < data.Length) {
        var later = FindBestReference(data, position + 1, maxDistance, head, chain, parseRing);
        if (later.OutputLength >= MinMatch && later.Score > best.Score + LazyMatchMargin) {
          ++position;
          continue;
        }
      }

      commands.Add(new Command(literalStart, position - literalStart,
        best.CopyLength, best.OutputLength, best.Distance, best.IsDictionary));

      // A dictionary distance never enters the ring buffer (RFC 7932 Section 4).
      if (!best.IsDictionary && best.Distance != parseRing[0]) {
        parseRing[3] = parseRing[2];
        parseRing[2] = parseRing[1];
        parseRing[1] = parseRing[0];
        parseRing[0] = best.Distance;
      }

      var matchEnd = position + best.OutputLength;
      for (var i = position + 1; i < matchEnd; ++i)
        Insert(i);

      position = matchEnd;
      literalStart = position;
    }

    if (literalStart < data.Length)
      commands.Add(new Command(literalStart, data.Length - literalStart, 0, 0, 0, false));

    return commands;
  }

  // ---------------------------------------------------------------------------
  // Meta-block layout
  // ---------------------------------------------------------------------------

  /// <summary>A contiguous run of commands forming one meta-block.</summary>
  private readonly record struct MetaBlockRange(int CommandStart, int CommandEnd, int ByteStart, int ByteEnd);

  /// <summary>
  /// Groups commands into meta-blocks. Adjacent segments are merged while their
  /// literal distributions are similar enough that one shared set of prefix codes
  /// stays cheaper than a second meta-block header.
  /// </summary>
  private static List<MetaBlockRange> SplitMetaBlocks(byte[] data, List<Command> commands) {
    var blocks = new List<MetaBlockRange>();
    if (commands.Count == 0)
      return blocks;

    // Cut the command stream into fixed-size segments first; split points may
    // only fall on those boundaries.
    var segmentStarts = new List<int> { 0 };
    var forcedEnd = new List<bool> { false };
    var carried = 0;
    for (var i = 0; i < commands.Count; ++i) {
      carried += commands[i].InsertLength + commands[i].OutputLength;
      var literalOnly = commands[i].CopyLength == 0;
      if (carried < SegmentBytes && !literalOnly || i + 1 >= commands.Count)
        continue;

      segmentStarts.Add(i + 1);
      forcedEnd.Add(literalOnly);
      carried = 0;
    }

    var segmentCount = segmentStarts.Count;
    var histograms = new int[segmentCount][];
    for (var s = 0; s < segmentCount; ++s) {
      var histogram = new int[256];
      var from = segmentStarts[s];
      var to = s + 1 < segmentCount ? segmentStarts[s + 1] : commands.Count;
      for (var i = from; i < to; ++i) {
        var command = commands[i];
        for (var k = 0; k < command.InsertLength; ++k)
          ++histogram[data[command.InsertStart + k]];
      }

      histograms[s] = histogram;
    }

    var openStart = 0;
    var openHistogram = (int[])histograms[0].Clone();
    var openBytes = SegmentByteCount(commands, segmentStarts, 0);

    for (var s = 1; s < segmentCount; ++s) {
      var segmentBytes = SegmentByteCount(commands, segmentStarts, s);
      var startNewBlock = forcedEnd[s] ||
                          openBytes + segmentBytes > MaxMetaBlockBytes ||
                          MergeCostUnits(openHistogram, histograms[s]) > SplitThresholdUnits;

      if (startNewBlock) {
        blocks.Add(MakeRange(commands, segmentStarts, openStart, s));
        openStart = s;
        openHistogram = (int[])histograms[s].Clone();
        openBytes = segmentBytes;
        continue;
      }

      for (var b = 0; b < 256; ++b)
        openHistogram[b] += histograms[s][b];
      openBytes += segmentBytes;
    }

    blocks.Add(MakeRange(commands, segmentStarts, openStart, segmentCount));
    return blocks;
  }

  /// <summary>Number of output bytes produced by one segment.</summary>
  private static int SegmentByteCount(List<Command> commands, List<int> segmentStarts, int segment) {
    var from = segmentStarts[segment];
    var to = segment + 1 < segmentStarts.Count ? segmentStarts[segment + 1] : commands.Count;
    var bytes = 0;
    for (var i = from; i < to; ++i)
      bytes += commands[i].InsertLength + commands[i].OutputLength;
    return bytes;
  }

  /// <summary>Builds the command and byte range covering segments [from, to).</summary>
  private static MetaBlockRange MakeRange(List<Command> commands, List<int> segmentStarts, int from, int to) {
    var commandStart = segmentStarts[from];
    var commandEnd = to < segmentStarts.Count ? segmentStarts[to] : commands.Count;
    var byteStart = commands[commandStart].InsertStart;
    var byteEnd = commands[commandEnd - 1].End;
    return new MetaBlockRange(commandStart, commandEnd, byteStart, byteEnd);
  }

  // ---------------------------------------------------------------------------
  // Literal context modelling (RFC 7932 Section 7.1)
  // ---------------------------------------------------------------------------

  /// <summary>Signed context mode lookup (RFC 7932 Section 7.1).</summary>
  private static readonly byte[] SignedContextLut = BuildSignedContextLut();

  private static byte[] BuildSignedContextLut() {
    var lut = new byte[256];
    for (var i = 0; i < 256; ++i)
      lut[i] = i switch {
        0 => 0,
        < 16 => 1,
        < 64 => 2,
        < 128 => 3,
        < 192 => 4,
        < 240 => 5,
        < 255 => 6,
        _ => 7
      };
    return lut;
  }

  /// <summary>Computes the literal context value 0-63 for the two preceding bytes.</summary>
  private static int LiteralContext(byte p1, byte p2, int contextMode) => contextMode switch {
    0 => p1 & 0x3F,
    1 => p1 >> 2,
    2 => BrotliConstants.Utf8ContextLut0[p1] | BrotliConstants.Utf8ContextLut1[p2],
    _ => (SignedContextLut[p1] << 3) | SignedContextLut[p2]
  };

  // ---------------------------------------------------------------------------
  // Compressed meta-block emission
  // ---------------------------------------------------------------------------

  /// <summary>A command with its insert-and-copy and distance codes resolved.</summary>
  private readonly record struct ResolvedCommand(
    int InsertStart,
    int InsertLength,
    int CopyLength,
    int Distance,
    int IacCode,
    int InsertCode,
    int CopyCode,
    int DistanceCode);

  /// <summary>
  /// Resolves the distance encoding of every command in a meta-block, advancing
  /// the distance ring buffer exactly as the decoder will. A distance code of -1
  /// means the command uses an implicit-distance insert-and-copy range and no
  /// distance symbol is written; the ring is left untouched for code 0 and for
  /// implicit distances, per RFC 7932 Section 4.
  /// </summary>
  private static ResolvedCommand[] ResolveCommands(List<Command> commands, MetaBlockRange range, int[] ring) {
    var resolved = new ResolvedCommand[range.CommandEnd - range.CommandStart];
    for (var i = range.CommandStart; i < range.CommandEnd; ++i) {
      var command = commands[i];
      var insertCode = FindLengthCode(BrotliConstants.InsertLengthTable, command.InsertLength);

      if (command.CopyLength == 0) {
        // A trailing literal-only command: the decoder finishes the meta-block
        // before it would read a distance, so the copy code only has to exist.
        var literalIac = EncodeInsertAndCopyCode(insertCode, 0, insertCode <= 7);
        resolved[i - range.CommandStart] = new ResolvedCommand(
          command.InsertStart, command.InsertLength, 0, 0, literalIac, insertCode, 0, -1);
        continue;
      }

      var copyCode = FindLengthCode(BrotliConstants.CopyLengthTable, command.CopyLength);
      var canUseImplicit = !command.IsDictionary &&
                           insertCode <= 7 && copyCode <= 15 && command.Distance == ring[0];

      int iacCode;
      int distanceCode;
      if (canUseImplicit) {
        iacCode = EncodeInsertAndCopyCode(insertCode, copyCode, true);
        distanceCode = -1;
      } else if (command.IsDictionary) {
        // A dictionary reference always spells its distance out and never enters
        // the ring buffer (RFC 7932 Sections 4 and 8).
        iacCode = EncodeInsertAndCopyCode(insertCode, copyCode, false);
        distanceCode = EncodeDistance(command.Distance).Code;
      } else {
        iacCode = EncodeInsertAndCopyCode(insertCode, copyCode, false);
        distanceCode = FindRingDistanceCode(command.Distance, ring);
        if (distanceCode < 0)
          distanceCode = EncodeDistance(command.Distance).Code;

        if (distanceCode != 0) {
          ring[3] = ring[2];
          ring[2] = ring[1];
          ring[1] = ring[0];
          ring[0] = command.Distance;
        }
      }

      resolved[i - range.CommandStart] = new ResolvedCommand(
        command.InsertStart, command.InsertLength, command.CopyLength, command.Distance,
        iacCode, insertCode, copyCode, distanceCode);
    }

    return resolved;
  }

  /// <summary>
  /// Builds one entropy-coded meta-block into its own writer so its size can be
  /// compared against the uncompressed alternative before it is spliced into the
  /// stream.
  /// </summary>
  private static BrotliBitWriter BuildCompressedMetaBlock(
    byte[] data, List<Command> commands, MetaBlockRange range, bool isLast, int[] ring) {
    var resolved = ResolveCommands(commands, range, ring);

    // Literal frequencies per context, for every context mode, so the cheapest
    // mode can be picked before the contexts are clustered.
    var perMode = new int[BrotliConstants.NumLiteralContextModes][][];
    for (var mode = 0; mode < BrotliConstants.NumLiteralContextModes; ++mode) {
      var byContext = new int[64][];
      for (var c = 0; c < 64; ++c)
        byContext[c] = new int[256];
      perMode[mode] = byContext;
    }

    var iacFrequencies = new int[BrotliConstants.NumInsertAndCopyLengthCodes];
    var distanceFrequencies = new int[DistanceAlphabetSize];

    foreach (var command in resolved) {
      ++iacFrequencies[command.IacCode];
      if (command.DistanceCode >= 0)
        ++distanceFrequencies[command.DistanceCode];

      for (var k = 0; k < command.InsertLength; ++k) {
        var position = command.InsertStart + k;
        var p1 = position > 0 ? data[position - 1] : (byte)0;
        var p2 = position > 1 ? data[position - 2] : (byte)0;
        var literal = data[position];
        for (var mode = 0; mode < BrotliConstants.NumLiteralContextModes; ++mode)
          ++perMode[mode][LiteralContext(p1, p2, mode)][literal];
      }
    }

    var contextMode = ChooseContextMode(perMode);
    var contextFrequencies = perMode[contextMode];
    var (contextMap, literalCodes) = ChooseLiteralTrees(contextFrequencies);

    var iacCode = BuildPrefixCode(iacFrequencies, BrotliConstants.NumInsertAndCopyLengthCodes,
      BrotliConstants.MaxHuffmanCodeLength);
    var distanceCode = BuildPrefixCode(distanceFrequencies, DistanceAlphabetSize,
      BrotliConstants.MaxHuffmanCodeLength);

    var writer = new BrotliBitWriter();

    writer.WriteBits(1, isLast ? 1u : 0u);
    if (isLast)
      writer.WriteBits(1, 0); // ISLASTEMPTY = 0

    WriteMetaBlockLength(writer, range.ByteEnd - range.ByteStart);

    if (!isLast)
      writer.WriteBits(1, 0); // ISUNCOMPRESSED = 0

    WriteCount(writer, 1); // NBLTYPESL
    WriteCount(writer, 1); // NBLTYPESI
    WriteCount(writer, 1); // NBLTYPESD

    writer.WriteBits(2, 0); // NPOSTFIX = 0
    writer.WriteBits(4, 0); // NDIRECT = 0

    writer.WriteBits(2, (uint)contextMode);

    WriteCount(writer, literalCodes.Length); // NTREESL
    if (literalCodes.Length > 1)
      WriteContextMap(writer, contextMap, literalCodes.Length);

    WriteCount(writer, 1); // NTREESD

    foreach (var code in literalCodes)
      WritePrefixCodeDescriptor(writer, code, BrotliConstants.LiteralAlphabetSize);

    WritePrefixCodeDescriptor(writer, iacCode, BrotliConstants.NumInsertAndCopyLengthCodes);
    WritePrefixCodeDescriptor(writer, distanceCode, DistanceAlphabetSize);

    foreach (var command in resolved) {
      WriteSymbol(writer, iacCode, command.IacCode);

      var insertExtra = BrotliConstants.InsertLengthTable[command.InsertCode].ExtraBits;
      if (insertExtra > 0)
        writer.WriteBits(insertExtra,
          (uint)(command.InsertLength - BrotliConstants.InsertLengthTable[command.InsertCode].BaseValue));

      var copyExtra = BrotliConstants.CopyLengthTable[command.CopyCode].ExtraBits;
      if (copyExtra > 0)
        writer.WriteBits(copyExtra,
          (uint)(command.CopyLength - BrotliConstants.CopyLengthTable[command.CopyCode].BaseValue));

      for (var k = 0; k < command.InsertLength; ++k) {
        var position = command.InsertStart + k;
        var p1 = position > 0 ? data[position - 1] : (byte)0;
        var p2 = position > 1 ? data[position - 2] : (byte)0;
        var tree = contextMap[LiteralContext(p1, p2, contextMode)];
        WriteSymbol(writer, literalCodes[tree], data[position]);
      }

      if (command.DistanceCode < 0)
        continue;

      WriteSymbol(writer, distanceCode, command.DistanceCode);
      if (command.DistanceCode < 16)
        continue;

      var (_, extraBits, extraValue) = EncodeDistance(command.Distance);
      if (extraBits > 0)
        writer.WriteBits(extraBits, (uint)extraValue);
    }

    return writer;
  }

  /// <summary>
  /// Picks the literal context mode whose per-context distributions are cheapest
  /// to code before any clustering is applied.
  /// </summary>
  private static int ChooseContextMode(int[][][] perMode) {
    var best = 0;
    var bestCost = long.MaxValue;
    for (var mode = 0; mode < perMode.Length; ++mode) {
      long cost = 0;
      for (var c = 0; c < 64; ++c)
        cost += HistogramCostUnits(perMode[mode][c]);

      if (cost >= bestCost)
        continue;

      bestCost = cost;
      best = mode;
    }

    return best;
  }

  /// <summary>
  /// Clusters the 64 literal contexts into prefix codes. Contexts are merged
  /// greedily by the extra cost of sharing one distribution, and the tree count
  /// that minimises the measured total of descriptors, context map and literal
  /// data wins.
  /// </summary>
  private static (int[] ContextMap, PrefixCode[] Codes) ChooseLiteralTrees(int[][] contextFrequencies) {
    var members = new List<List<int>>();
    var clusters = new List<int[]>();
    for (var c = 0; c < 64; ++c) {
      var total = 0;
      for (var b = 0; b < 256; ++b)
        total += contextFrequencies[c][b];

      if (total == 0)
        continue;

      members.Add([c]);
      clusters.Add((int[])contextFrequencies[c].Clone());
    }

    // Nothing was coded from this alphabet at all.
    if (clusters.Count == 0)
      return (new int[64], [BuildPrefixCode(new int[256], 256, BrotliConstants.MaxHuffmanCodeLength)]);

    // Pairwise merge costs are cached; a merge only invalidates one row.
    var pairCost = new List<List<long>>(clusters.Count);
    for (var i = 0; i < clusters.Count; ++i) {
      var row = new List<long>(clusters.Count);
      for (var j = 0; j < clusters.Count; ++j)
        row.Add(j <= i ? 0 : MergeCostUnits(clusters[i], clusters[j]));
      pairCost.Add(row);
    }

    var bestCost = long.MaxValue;
    int[] bestMap = null!;
    PrefixCode[] bestCodes = null!;

    while (true) {
      if (Array.IndexOf(LiteralTreeCandidates, clusters.Count) >= 0) {
        var (map, codes, cost) = EvaluateClustering(members, clusters);
        if (cost < bestCost) {
          bestCost = cost;
          bestMap = map;
          bestCodes = codes;
        }
      }

      if (clusters.Count <= 1)
        break;

      var mergeI = 0;
      var mergeJ = 1;
      var mergeCost = long.MaxValue;
      for (var i = 0; i < clusters.Count; ++i)
        for (var j = i + 1; j < clusters.Count; ++j) {
          if (pairCost[i][j] >= mergeCost)
            continue;

          mergeCost = pairCost[i][j];
          mergeI = i;
          mergeJ = j;
        }

      for (var b = 0; b < 256; ++b)
        clusters[mergeI][b] += clusters[mergeJ][b];
      members[mergeI].AddRange(members[mergeJ]);
      clusters.RemoveAt(mergeJ);
      members.RemoveAt(mergeJ);

      pairCost.RemoveAt(mergeJ);
      foreach (var row in pairCost)
        row.RemoveAt(mergeJ);

      for (var k = 0; k < clusters.Count; ++k) {
        if (k == mergeI)
          continue;

        var cost = MergeCostUnits(clusters[mergeI], clusters[k]);
        if (k > mergeI)
          pairCost[mergeI][k] = cost;
        else
          pairCost[k][mergeI] = cost;
      }
    }

    return (bestMap, bestCodes);
  }

  /// <summary>
  /// Measures one clustering: descriptor bits for every literal code, the context
  /// map, the NTREESL field and the literal payload itself.
  /// </summary>
  private static (int[] Map, PrefixCode[] Codes, long Cost) EvaluateClustering(
    List<List<int>> members, List<int[]> clusters) {
    var map = new int[64];
    for (var t = 0; t < members.Count; ++t)
      foreach (var context in members[t])
        map[context] = t;

    var codes = new PrefixCode[clusters.Count];
    long cost = 0;
    for (var t = 0; t < clusters.Count; ++t) {
      codes[t] = BuildPrefixCode(clusters[t], 256, BrotliConstants.MaxHuffmanCodeLength);
      cost += (long)MeasureDescriptorBits(codes[t], 256) * 256;
      for (var b = 0; b < 256; ++b)
        cost += (long)clusters[t][b] * SymbolBits(codes[t], b) * 256;
    }

    var scratch = new BrotliBitWriter();
    WriteCount(scratch, clusters.Count);
    if (clusters.Count > 1)
      WriteContextMap(scratch, map, clusters.Count);
    cost += (long)scratch.BitLength() * 256;

    return (map, codes, cost);
  }

  /// <summary>
  /// Writes a literal context map (RFC 7932 Section 7.3) with RLEMAX = 0 and no
  /// move-to-front transform: only 64 entries are involved, so neither pays off.
  /// </summary>
  private static void WriteContextMap(BrotliBitWriter writer, int[] contextMap, int treeCount) {
    writer.WriteBits(1, 0); // RLEMAX = 0

    var frequencies = new int[treeCount];
    foreach (var tree in contextMap)
      ++frequencies[tree];

    var code = BuildPrefixCode(frequencies, treeCount, BrotliConstants.MaxHuffmanCodeLength);
    WritePrefixCodeDescriptor(writer, code, treeCount);
    foreach (var tree in contextMap)
      WriteSymbol(writer, code, tree);

    writer.WriteBits(1, 0); // IMTF = 0
  }

  /// <summary>
  /// Builds one uncompressed meta-block into its own writer. The payload is
  /// byte-aligned against the whole stream, so the number of padding bits depends
  /// on how many bits already precede this meta-block.
  /// </summary>
  private static BrotliBitWriter BuildUncompressedMetaBlock(byte[] data, MetaBlockRange range, int startBitOffset) {
    var writer = new BrotliBitWriter();
    writer.WriteBits(1, 0); // ISLAST = 0
    WriteMetaBlockLength(writer, range.ByteEnd - range.ByteStart);
    writer.WriteBits(1, 1); // ISUNCOMPRESSED = 1

    var padding = (8 - (startBitOffset + writer.BitLength()) % 8) % 8;
    if (padding > 0)
      writer.WriteBits(padding, 0);

    for (var i = range.ByteStart; i < range.ByteEnd; ++i)
      writer.WriteBits(8, data[i]);

    return writer;
  }
}

/// <summary>
/// Bit writer for Brotli streams. Writes bits least significant bit first
/// (RFC 7932 Section 1.5.1).
/// </summary>
internal sealed class BrotliBitWriter {
  private readonly List<byte> _bytes = [];
  private uint _bitBuffer;
  private int _bitCount;

  /// <summary>
  /// Writes the low <paramref name="count"/> bits of <paramref name="value"/>.
  /// </summary>
  /// <param name="count">Number of bits to write (0-24).</param>
  /// <param name="value">The value whose low bits are written.</param>
  public void WriteBits(int count, uint value) {
    if (count <= 0)
      return;

    this._bitBuffer |= (value & ((1u << count) - 1)) << this._bitCount;
    this._bitCount += count;
    while (this._bitCount >= 8) {
      this._bytes.Add((byte)(this._bitBuffer & 0xFF));
      this._bitBuffer >>= 8;
      this._bitCount -= 8;
    }
  }

  /// <summary>Pads with zero bits up to the next byte boundary.</summary>
  public void AlignToByte() {
    if (this._bitCount <= 0)
      return;

    this._bytes.Add((byte)(this._bitBuffer & 0xFF));
    this._bitBuffer = 0;
    this._bitCount = 0;
  }

  /// <summary>
  /// Appends another writer's exact bit sequence without introducing padding at
  /// the join. Brotli meta-blocks are not individually byte-aligned, so a
  /// candidate built in its own writer has to be re-threaded bit for bit.
  /// </summary>
  /// <param name="other">The writer whose bits are appended.</param>
  public void Append(BrotliBitWriter other) {
    foreach (var value in other._bytes)
      this.WriteBits(8, value);

    if (other._bitCount > 0)
      this.WriteBits(other._bitCount, other._bitBuffer);
  }

  /// <summary>Total number of bits written so far.</summary>
  public int BitLength() => this._bytes.Count * 8 + this._bitCount;

  /// <summary>Emits any pending partial byte.</summary>
  public void Flush() {
    if (this._bitCount <= 0)
      return;

    this._bytes.Add((byte)(this._bitBuffer & 0xFF));
    this._bitBuffer = 0;
    this._bitCount = 0;
  }

  /// <summary>Returns everything written so far as a byte array.</summary>
  public byte[] ToArray() => this._bytes.ToArray();
}
