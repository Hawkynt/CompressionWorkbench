using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Compresses data using the LZX algorithm (as used in Microsoft CAB and WIM formats).
/// </summary>
/// <remarks>
/// Emits only verbatim blocks for simplicity. Aligned-offset blocks are a compression
/// optimization not required for correctness. The sliding window, repeated match
/// offsets (R0, R1, R2), and Huffman code-length delta coding are implemented
/// per the LZX specification so that the output is decodable by a compliant LZX decompressor.
///
/// <para>
/// LZX offset encoding uses a "formatted offset" convention:
/// <list type="bullet">
///   <item>Position slots 0, 1, 2 encode repeated matches using R0, R1, R2 respectively.</item>
///   <item>Position slots 3+ encode new offsets via formatted_offset = actual_distance − 2.</item>
///   <item>Slot 3 has base = 3, so minimum encodeable non-repeat distance = 3 + 0 + 2 = 5.</item>
///   <item>Distances 2–4 that are not a current R0/R1/R2 value cannot be encoded; the match
///         finder is restricted to avoid them.</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class LzxCompressor {
  private readonly int _windowBits;
  private readonly int _windowSize;
  private readonly int _numPositionSlots;
  private readonly int _numMainSymbols;
  private readonly int _blockSize;
  private readonly LzxCompressionLevel _level;

  // Repeated match offsets (1-based distances), initialised per LZX spec
  private int _r0 = 1;
  private int _r1 = 1;
  private int _r2 = 1;

  // Huffman code lengths persisted for delta coding across blocks
  private readonly int[] _prevMainLengths;
  private readonly int[] _prevLengthLengths;

  /// <summary>
  /// Initializes a new <see cref="LzxCompressor"/>.
  /// </summary>
  /// <param name="windowBits">
  /// The window size exponent (15–21). Window size = 2^<paramref name="windowBits"/>.
  /// </param>
  /// <param name="blockSize">
  /// The maximum uncompressed bytes per block. Defaults to 32 768.
  /// </param>
  /// <param name="level">The compression level.</param>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="windowBits"/> is outside [15, 21] or <paramref name="blockSize"/> ≤ 0.
  /// </exception>
  public LzxCompressor(int windowBits = 15, int blockSize = LzxConstants.DefaultBlockSize,
      LzxCompressionLevel level = LzxCompressionLevel.Normal) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, LzxConstants.MinWindowBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, LzxConstants.MaxWindowBits);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, 0);

    this._windowBits = windowBits;
    this._windowSize = 1 << windowBits;
    this._numPositionSlots = LzxConstants.GetPositionSlotCount(windowBits);
    this._numMainSymbols = LzxConstants.NumChars + this._numPositionSlots * LzxConstants.NumLengthHeaders;
    this._blockSize = blockSize;
    this._level = level;

    this._prevMainLengths = new int[this._numMainSymbols];
    this._prevLengthLengths = new int[LzxConstants.NumLengthSymbols];
  }

  /// <summary>
  /// Compresses the given data and returns the compressed bytes.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed LZX data.</returns>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    using var output = new MemoryStream();
    var writer = new LzxBitWriter(output);

    var tokens = this.Tokenise(data);

    var tokenStart = 0;
    while (tokenStart < tokens.Count) {
      // Collect tokens for this block (up to _blockSize uncompressed bytes)
      var blockBytes = 0;
      var blockTokenEnd = tokenStart;
      while (blockTokenEnd < tokens.Count) {
        var tok = tokens[blockTokenEnd];
        var tokBytes = tok.IsLiteral ? 1 : tok.Length;
        if (blockBytes + tokBytes > this._blockSize && blockBytes > 0)
          break;

        blockBytes += tokBytes;
        ++blockTokenEnd;
        if (blockBytes >= this._blockSize)
          break;
      }

      var blockTokens = tokens.GetRange(tokenStart, blockTokenEnd - tokenStart);

      this.EmitVerbatimBlock(writer, blockTokens, blockBytes);
      tokenStart = blockTokenEnd;
    }

    writer.Flush();
    return output.ToArray();
  }

  // -------------------------------------------------------------------------
  // Tokenisation
  // -------------------------------------------------------------------------

  private List<LzxToken> Tokenise(ReadOnlySpan<byte> data) {
    var tokens = new List<LzxToken>(data.Length / 2 + 16);
    var chainDepth = this._level switch {
      LzxCompressionLevel.Fast => 16,
      LzxCompressionLevel.Best => 256,
      _ => 64
    };
    var finder = new HashChainMatchFinder(this._windowSize, chainDepth);
    var pos = 0;
    int r0 = this._r0, r1 = this._r1, r2 = this._r2;

    while (pos < data.Length) {
      var (distance, length) = finder.FindMatch(data, pos, this._windowSize, LzxConstants.MaxMatch, LzxConstants.MinMatch);
      if (length >= LzxConstants.MinMatch) {

        // LZX can encode dist == r0/r1/r2 as repeat slots.
        // Non-repeat offsets require dist >= 5 (slot 3, base=3, +2 bias).
        var isRepeat = distance == r0 || distance == r1 || distance == r2;
        var canEncode = isRepeat || distance >= LzxConstants.MinNonRepeatDistance;

        if (canEncode) {
          tokens.Add(LzxToken.CreateMatch(length, distance));

          // Update R0/R1/R2 state
          if (!isRepeat)
            (r2, r1, r0) = (r1, r0, distance);
          else if (distance == r1)
            (r0, r1) = (r1, r0);
          else if (distance == r2)
            (r0, r2) = (r2, r0);

          // dist == r0: no change
          for (var i = 1; i < length && pos + i < data.Length; ++i)
            finder.InsertPosition(data, pos + i);

          pos += length;
          continue;
        }
      }

      tokens.Add(LzxToken.CreateLiteral(data[pos]));
      ++pos;
    }

    return tokens;
  }

  // -------------------------------------------------------------------------
  // Block emission
  // -------------------------------------------------------------------------

  private void EmitVerbatimBlock(LzxBitWriter writer, List<LzxToken> blockTokens, int blockUncompressedSize) {
    // Build frequency tables by simulating R0/R1/R2 state
    var mainFreq = new int[this._numMainSymbols];
    var lengthFreq = new int[LzxConstants.NumLengthSymbols];
    int r0 = this._r0, r1 = this._r1, r2 = this._r2;

    foreach (var tok in blockTokens)
      if (tok.IsLiteral)
        ++mainFreq[tok.Value];
      else {
        var slot = GetPositionSlot(tok.Offset, ref r0, ref r1, ref r2);
        var len = tok.Length;
        var lengthHeader = Math.Min(len - LzxConstants.MinMatch, LzxConstants.NumLengthHeaders - 1);
        ++mainFreq[LzxConstants.NumChars + slot * LzxConstants.NumLengthHeaders + lengthHeader];
        if (lengthHeader != LzxConstants.NumLengthHeaders - 1)
          continue;

        var extraLen = len - LzxConstants.MinMatch - (LzxConstants.NumLengthHeaders - 1);
        ++lengthFreq[Math.Clamp(extraLen, 0, LzxConstants.NumLengthSymbols - 1)];
      }

    // Build Huffman code lengths and canonical codes
    var mainLengths = BuildCodeLengths(mainFreq, this._numMainSymbols, LzxConstants.MaxHuffmanBits);
    var lengthLengths = BuildCodeLengths(lengthFreq, LzxConstants.NumLengthSymbols, LzxConstants.MaxHuffmanBits);
    var mainCodes = BuildCanonicalCodes(mainLengths);
    var lengthCodes = BuildCanonicalCodes(lengthLengths);

    // Write block type + size
    writer.WriteBits(LzxConstants.BlockTypeVerbatim, 3);
    if (blockUncompressedSize == LzxConstants.DefaultBlockSize)
      writer.WriteBits(1, 1);
    else {
      writer.WriteBits(0, 1);
      writer.WriteBits((uint)blockUncompressedSize, 16);
    }

    // Write pre-tree + main tree code lengths (two halves)
    WriteTreeWithPreTree(writer, mainLengths, 0, LzxConstants.NumChars, this._prevMainLengths);
    WriteTreeWithPreTree(writer, mainLengths, LzxConstants.NumChars, this._numMainSymbols - LzxConstants.NumChars, this._prevMainLengths);

    // Write pre-tree + length tree code lengths
    WriteTreeWithPreTree(writer, lengthLengths, 0, LzxConstants.NumLengthSymbols, this._prevLengthLengths);

    // Update persisted lengths for next block's delta coding
    mainLengths.AsSpan(0, this._numMainSymbols).CopyTo(this._prevMainLengths);
    lengthLengths.AsSpan(0, LzxConstants.NumLengthSymbols).CopyTo(this._prevLengthLengths);

    // Write token stream; restart from pre-block R0/R1/R2 state
    r0 = this._r0;
    r1 = this._r1;
    r2 = this._r2;
    foreach (var tok in blockTokens)
      if (tok.IsLiteral)
        writer.WriteBits(mainCodes[tok.Value], mainLengths[tok.Value]);
      else {
        var slot = GetPositionSlot(tok.Offset, ref r0, ref r1, ref r2);
        var len = tok.Length;
        var lengthHeader = Math.Min(len - LzxConstants.MinMatch, LzxConstants.NumLengthHeaders - 1);
        var mainSym = LzxConstants.NumChars + slot * LzxConstants.NumLengthHeaders + lengthHeader;
        writer.WriteBits(mainCodes[mainSym], mainLengths[mainSym]);

        if (lengthHeader == LzxConstants.NumLengthHeaders - 1) {
          var extraLen = Math.Clamp(len - LzxConstants.MinMatch - (LzxConstants.NumLengthHeaders - 1),
            0, LzxConstants.NumLengthSymbols - 1);
          writer.WriteBits(lengthCodes[extraLen], lengthLengths[extraLen]);
        }

        // Write position footer bits for non-repeat slots (slots 3+)
        if (slot < 3)
          continue;

        LzxConstants.GetSlotInfo(slot, out var baseOffset, out var footerBits);
        if (footerBits <= 0)
          continue;

        // Formatted offset = actual_distance - 2; footer = formatted - base
        var formattedOffset = tok.Offset - 2;
        var footer = formattedOffset - baseOffset;
        writer.WriteBits((uint)footer, footerBits);
      }

    // Update R0/R1/R2 for next block
    this._r0 = r0;
    this._r1 = r1;
    this._r2 = r2;
  }

  /// <summary>
  /// Returns the LZX position slot for a match with the given distance,
  /// updating the R0/R1/R2 repeat-offset registers as a side effect.
  /// </summary>
  private static int GetPositionSlot(int distance, ref int r0, ref int r1, ref int r2) {
    if (distance == r0)
      return 0;

    if (distance == r1) {
      (r0, r1) = (r1, r0);
      return 1;
    }

    if (distance == r2) {
      (r0, r2) = (r2, r0);
      return 2;
    }

    // Non-repeat: formatted_offset = distance - 2
    var formattedOffset = distance - 2;
    var slot = LzxConstants.OffsetToSlot(formattedOffset);
    r2 = r1;
    r1 = r0;
    r0 = distance;
    return slot;
  }

  // -------------------------------------------------------------------------
  // Pre-tree encoding of code lengths
  // -------------------------------------------------------------------------

  private static void WriteTreeWithPreTree(
    LzxBitWriter writer,
    int[] lengths,
    int start,
    int count,
    int[] prevLengths) {
    // Build delta sequence: delta[i] = (prevLen - curLen + 17) mod 17
    var deltas = new int[count];
    for (var i = 0; i < count; ++i)
      deltas[i] = (prevLengths[start + i] - lengths[start + i] + 17) % 17;

    // Run-length encode the delta sequence into pre-tree (sym, extra) pairs
    var preSymbols = new List<(int sym, int extra, int extraBits)>(count);
    var di = 0;
    while (di < count) {
      var sym = deltas[di];

      if (sym == 0) {
        // Count zero run
        var runLen = 0;
        while (di + runLen < count && deltas[di + runLen] == 0)
          ++runLen;

        while (runLen > 0)
          switch (runLen) {
            case >= 20: {
              var thisRun = Math.Min(runLen, 51); // 20 + (max 5-bit value=31)
              preSymbols.Add((18, thisRun - 20, 5));
              di += thisRun;
              runLen -= thisRun;
              break;
            }

            case >= 4: {
              var thisRun = Math.Min(runLen, 19); // 4 + (max 4-bit value=15)
              preSymbols.Add((17, thisRun - 4, 4));
              di += thisRun;
              runLen -= thisRun;
              break;
            }

            default:
              preSymbols.Add((0, 0, 0));
              ++di;
              --runLen;
              break;
          }
      } else {
        preSymbols.Add((sym, 0, 0));
        ++di;
      }
    }

    // Build pre-tree Huffman codes from symbol frequencies
    var preFreq = new int[LzxConstants.NumPreTreeSymbols];
    foreach (var (sym, _, _) in preSymbols)
      preFreq[sym]++;

    var preLengths = BuildCodeLengths(preFreq, LzxConstants.NumPreTreeSymbols, LzxConstants.MaxPreTreeBits);
    var preCodes = BuildCanonicalCodes(preLengths);

    // Write pre-tree: 20 symbols × 4 bits each
    for (var i = 0; i < LzxConstants.NumPreTreeSymbols; ++i)
      writer.WriteBits((uint)preLengths[i], LzxConstants.PreTreeBits);

    // Write pre-tree encoded symbol stream
    foreach (var (sym, extra, extraBits) in preSymbols) {
      var plen = preLengths[sym];
      // A zero-length Huffman code means the symbol never appears; emit a 1-bit dummy if needed
      if (plen == 0) 
        plen = 1;

      writer.WriteBits(preCodes[sym], plen);
      if (extraBits > 0)
        writer.WriteBits((uint)extra, extraBits);
    }
  }

  // -------------------------------------------------------------------------
  // Huffman building helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// Builds Huffman code lengths for the given symbol frequency table using
  /// a simple Huffman tree construction with max-bits clamping.
  /// </summary>
  internal static int[] BuildCodeLengths(int[] frequencies, int numSymbols, int maxBits) {
    var lengths = new int[numSymbols];
    var symbols = new List<(int symbol, int freq)>(numSymbols);

    for (var i = 0; i < numSymbols; ++i)
      if (frequencies[i] > 0)
        symbols.Add((i, frequencies[i]));

    switch (symbols.Count) {
      case 0: return lengths;
      case 1:
        lengths[symbols[0].symbol] = 1;
        return lengths;
    }

    var nodeCount = symbols.Count * 2 - 1;
    var leftChild = new int[nodeCount];
    var rightChild = new int[nodeCount];
    leftChild.AsSpan().Fill(-1);
    rightChild.AsSpan().Fill(-1);
    var nodeSym = new int[nodeCount];
    nodeSym.AsSpan().Fill(-1);

    var heap = new SortedList<long, int>(nodeCount);
    long tieBreaker = 0;
    for (var i = 0; i < symbols.Count; ++i) {
      nodeSym[i] = symbols[i].symbol;
      heap.Add(((long)symbols[i].freq << 32) | tieBreaker++, i);
    }

    var nextNode = symbols.Count;
    while (heap.Count > 1) {
      var key1 = heap.Keys[0];
      var node1 = heap.Values[0];
      heap.RemoveAt(0);
      var key2 = heap.Keys[0];
      var node2 = heap.Values[0];
      heap.RemoveAt(0);

      var parent = nextNode++;
      leftChild[parent] = node1;
      rightChild[parent] = node2;
      heap.Add((((key1 >> 32) + (key2 >> 32)) << 32) | tieBreaker++, parent);
    }

    var root = heap.Values[0];
    var stack = new Stack<(int node, int depth)>(nodeCount);
    stack.Push((root, 0));
    while (stack.Count > 0) {
      var (node, depth) = stack.Pop();
      if (leftChild[node] == -1)
        lengths[nodeSym[node]] = Math.Max(1, Math.Min(depth, maxBits));
      else {
        if (leftChild[node] >= 0) stack.Push((leftChild[node], depth + 1));
        if (rightChild[node] >= 0) stack.Push((rightChild[node], depth + 1));
      }
    }

    FixKraftInequality(lengths, maxBits);
    return lengths;
  }

  private static void FixKraftInequality(int[] lengths, int maxBits) {
    var kraftMax = 1L << maxBits;
    var kraftSum = lengths.Where(value => value > 0).Sum(value => kraftMax >> value);

    while (kraftSum > kraftMax)
      for (var i = lengths.Length - 1; i >= 0; --i) {
        if (lengths[i] <= 0 || lengths[i] >= maxBits)
          continue;

        kraftSum -= kraftMax >> lengths[i];
        ++lengths[i];
        kraftSum += kraftMax >> lengths[i];
        if (kraftSum <= kraftMax) 
          break;
      }
  }

  /// <summary>
  /// Assigns canonical Huffman codes (MSB-first) to the given code length array.
  /// </summary>
  internal static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = lengths.Length > 0 ? lengths.Max() : 0;
    if (maxLen == 0)
      return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (var value in lengths)
      if (value > 0) 
        ++blCount[value];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) 
        codes[i] = nextCode[lengths[i]]++;

    return codes;
  }

}
