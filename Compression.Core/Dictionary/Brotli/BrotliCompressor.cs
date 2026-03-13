using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Compresses data in the Brotli format (RFC 7932).
/// </summary>
/// <remarks>
/// Supports two modes:
/// <list type="bullet">
///   <item><see cref="Compress"/>: Uses uncompressed meta-blocks (fast, no compression ratio).</item>
///   <item><see cref="CompressLz77"/>: Uses LZ77 + Huffman compressed meta-blocks (actual compression).</item>
/// </list>
/// </remarks>
public static class BrotliCompressor {
  /// <summary>
  /// Compresses data to the Brotli format using uncompressed meta-blocks.
  /// Fast encoding with no compression ratio improvement.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    var writer = new BrotliBitWriter(output);

    // Encode window bits (WBITS = 16 → single 0 bit)
    writer.WriteBits(1, 0); // WBITS = 16

    if (data.Length == 0) {
      // Empty stream: WBITS then ISLAST=1, ISEMPTY=1
      writer.WriteBits(1, 1); // ISLAST
      writer.WriteBits(1, 1); // ISEMPTY
      writer.AlignToByte();
      writer.Flush();
      return output.ToArray();
    }

    // Split into uncompressed meta-blocks of up to 65536 bytes.
    // Per RFC 7932, ISUNCOMPRESSED is only present when ISLAST=0,
    // so all data blocks use ISLAST=0, followed by a final empty last block.
    var offset = 0;
    while (offset < data.Length) {
      var blockSize = Math.Min(data.Length - offset, 65536);

      // ISLAST = 0 (uncompressed blocks cannot be last)
      writer.WriteBits(1, 0);

      // MLEN: encode as MNIBBLES=4 (16-bit length)
      var mlen = blockSize - 1;
      writer.WriteBits(2, 0); // MNIBBLES - 4 = 0 → 4 nibbles
      writer.WriteBits(4, (uint)(mlen & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 4) & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 8) & 0xF));
      writer.WriteBits(4, (uint)((mlen >> 12) & 0xF));

      // ISUNCOMPRESSED = 1
      writer.WriteBits(1, 1);

      // Align to byte boundary before uncompressed data
      writer.AlignToByte();

      // Write raw bytes
      for (var i = 0; i < blockSize; ++i)
        writer.WriteByte(data[offset + i]);

      offset += blockSize;
    }

    // Final empty last meta-block: ISLAST=1, ISEMPTY=1
    writer.WriteBits(1, 1); // ISLAST
    writer.WriteBits(1, 1); // ISEMPTY
    writer.AlignToByte();
    writer.Flush();
    return output.ToArray();
  }

  /// <summary>
  /// Compresses data to the Brotli format using LZ77 + Huffman compressed meta-blocks.
  /// Produces actual compression by finding repeated sequences.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] CompressLz77(ReadOnlySpan<byte> data) {
    switch (data.Length) {
      // For very short data, uncompressed is more efficient
      case 0:
      case < 16: return Compress(data);
    }

    using var output = new MemoryStream();
    var writer = new BrotliBitWriter(output);

    // Determine window size
    var windowBits = ComputeWindowBits(data.Length);
    var windowSize = 1 << windowBits;

    // Encode window bits (WBITS)
    WriteWindowBits(writer, windowBits);

    // Find LZ77 matches using hash chain
    var dataArray = data.ToArray();
    var matchFinder = new HashChainMatchFinder(windowSize);
    var commands = FindMatches(dataArray, matchFinder, windowSize);

    // Emit compressed meta-blocks
    EmitCompressedMetaBlock(writer, dataArray, commands, isLast: true);

    writer.AlignToByte();
    writer.Flush();

    var compressed = output.ToArray();

    // If compressed is larger, fall back to uncompressed
    if (compressed.Length >= data.Length + 10)
      return Compress(data);

    return compressed;
  }

  /// <summary>
  /// Computes appropriate window bits for the given data length.
  /// </summary>
  private static int ComputeWindowBits(int dataLength) {
    var bits = BrotliConstants.MinWindowBits;
    while ((1 << bits) < dataLength && bits < BrotliConstants.MaxWindowBits)
      ++bits;

    return bits;
  }

  /// <summary>
  /// Writes the WBITS field to the stream header.
  /// </summary>
  private static void WriteWindowBits(BrotliBitWriter writer, int windowBits) {
    switch (windowBits) {
      case 16: writer.WriteBits(1, 0); // single 0 bit
        break;

      case >= 17 and <= 24:
        writer.WriteBits(1, 1); // first bit = 1
        writer.WriteBits(3, (uint)(windowBits - 17)); // next 3 bits
        break;

      case >= 10 and <= 14:
        writer.WriteBits(1, 1); // first bit = 1
        writer.WriteBits(3, 0); // next 3 bits = 0
        writer.WriteBits(3, (uint)(windowBits - 8)); // 3 more bits
        break;

      default:
        // windowBits == 15 or 16 (already handled), fallback to 17
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 0); // 17
        break;
    }
  }

  /// <summary>
  /// An LZ77 command: either a literal run or a match (distance + length).
  /// </summary>
  private readonly record struct LzCommand(int InsertLength, int CopyLength, int Distance);

  /// <summary>
  /// Finds LZ77 matches in the input data and returns a sequence of commands.
  /// </summary>
  private static List<LzCommand> FindMatches(byte[] data, HashChainMatchFinder matchFinder, int windowSize) {
    var commands = new List<LzCommand>();
    var pos = 0;
    var literalStart = 0;

    while (pos < data.Length) {
      // Try to find a match
      Match bestMatch = default;
      if (pos + 4 <= data.Length) {
        var maxDist = Math.Min(pos, windowSize);
        var maxLen = Math.Min(258, data.Length - pos);
        bestMatch = matchFinder.FindMatch(data, pos, maxDist, maxLen, 4);
      }

      if (bestMatch.Length >= 4) {
        // Emit command: insert literals, then copy match
        var insertLen = pos - literalStart;
        commands.Add(new(insertLen, bestMatch.Length, bestMatch.Distance));
        pos += bestMatch.Length;
        literalStart = pos;
      } else
        ++pos;
    }

    // Trailing literals
    if (literalStart < data.Length)
      commands.Add(new(data.Length - literalStart, 0, 0));

    return commands;
  }

  /// <summary>
  /// Emits a compressed meta-block containing the given LZ77 commands.
  /// Uses a simple Huffman coding scheme with a single block type per category.
  /// </summary>
  private static void EmitCompressedMetaBlock(BrotliBitWriter writer, byte[] data,
    List<LzCommand> commands, bool isLast) {
    var totalBytes = data.Length;

    // ISLAST
    writer.WriteBits(1, isLast ? 1u : 0u);

    // MLEN
    var mlen = totalBytes - 1;
    writer.WriteBits(2, 0); // MNIBBLES - 4 = 0 → 4 nibbles
    writer.WriteBits(4, (uint)(mlen & 0xF));
    writer.WriteBits(4, (uint)((mlen >> 4) & 0xF));
    writer.WriteBits(4, (uint)((mlen >> 8) & 0xF));
    writer.WriteBits(4, (uint)((mlen >> 12) & 0xF));

    // For ISLAST blocks, ISUNCOMPRESSED bit is not present
    // For non-last blocks, ISUNCOMPRESSED = 0 (compressed)
    if (!isLast)
      writer.WriteBits(1, 0);

    // Block type counts: 1 for each category (literal, insert&copy, distance)
    WriteBlockTypeCount(writer, 1); // literal
    WriteBlockTypeCount(writer, 1); // insert&copy
    WriteBlockTypeCount(writer, 1); // distance

    // NPOSTFIX = 0, NDIRECT = 0
    writer.WriteBits(2, 0); // NPOSTFIX
    writer.WriteBits(4, 0); // NDIRECT >> NPOSTFIX

    // Context mode for literal block type 0: LSB6 (mode 0)
    writer.WriteBits(2, 0);

    // Context map for literals: 1 tree, all zeros
    WriteBlockTypeCount(writer, 1); // num literal trees = 1

    // Context map for distances: 1 tree, all zeros
    WriteBlockTypeCount(writer, 1); // num distance trees = 1

    // Build frequency tables and write Huffman codes
    // Collect symbol frequencies
    var litFreq = new int[256];
    var iacFreq = new int[BrotliConstants.NumInsertAndCopyLengthCodes];
    var distAlphabetSize2 = 16 + 0 + (48 << 0); // = 64 with NPOSTFIX=0, NDIRECT=0
    var distFreq = new int[distAlphabetSize2];

    foreach (var cmd in commands) {
      // Count literal bytes
      // Count insert&copy code usage
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength);
      if (iacCode < iacFreq.Length)
        ++iacFreq[iacCode];
    }

    // Count literals
    var litPos = 0;
    foreach (var cmd in commands)
      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i)
        ++litFreq[data[litPos++]];

    // Count distances
    foreach (var cmd in commands) {
      if (cmd is not { CopyLength: > 0, Distance: > 0 })
        continue;

      var distCode = EncodeDistanceCode(cmd.Distance);
      if (distCode < distFreq.Length)
        ++distFreq[distCode];
    }

    // Build and write Huffman trees
    // Literal tree (256 symbols)
    var litLengths = BuildCodeLengths(litFreq, 256);
    WriteSimplePrefixCode(writer, litLengths, 256);

    // Insert-and-copy tree (704 symbols, but most are unused)
    var iacLengths = BuildCodeLengths(iacFreq, BrotliConstants.NumInsertAndCopyLengthCodes);
    WriteSimplePrefixCode(writer, iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);

    // Distance tree
    var distAlphabetSize = distAlphabetSize2;
    var distLengths = BuildCodeLengths(distFreq, distAlphabetSize);
    WriteSimplePrefixCode(writer, distLengths, distAlphabetSize);

    // Build canonical code tables for encoding
    var litCodes = BuildCanonicalCodes(litLengths, 256);
    var iacCodes = BuildCanonicalCodes(iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);
    var distCodes = BuildCanonicalCodes(distLengths, distAlphabetSize);

    // Encode commands
    litPos = 0;
    foreach (var cmd in commands) {
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength);
      WriteCode(writer, iacCodes, iacLengths, iacCode);

      // Write insert length extra bits
      WriteInsertLengthExtra(writer, cmd.InsertLength);

      // Write copy length extra bits
      if (cmd.CopyLength > 0)
        WriteCopyLengthExtra(writer, cmd.CopyLength);

      // Write literal bytes
      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i)
        WriteCode(writer, litCodes, litLengths, data[litPos++]);

      // Write distance
      if (cmd is not { CopyLength: > 0, Distance: > 0 })
        continue;

      var distCode = EncodeDistanceCode(cmd.Distance);
      WriteCode(writer, distCodes, distLengths, distCode);
      WriteDistanceExtra(writer, cmd.Distance, distCode);
    }
  }

  private static void WriteBlockTypeCount(BrotliBitWriter writer, int count) {
    if (count == 1)
      writer.WriteBits(1, 0); // single block type
    else {
      writer.WriteBits(1, 1);
      // Encode count - 1 in variable bits
      var n = count - 1;
      if (n <= 1)
        writer.WriteBits(3, 0);
      else {
        var bits = 0;
        var v = n - 1;
        while (v > 0) { v >>= 1; ++bits; }
        writer.WriteBits(3, (uint)bits);
        writer.WriteBits(bits, (uint)(n - 1 - ((1 << bits) - 1)));
      }
    }
  }

  /// <summary>
  /// Encodes insert and copy lengths into a combined code.
  /// Simple encoding: use code ranges from RFC 7932 Table 8.
  /// </summary>
  private static int EncodeInsertAndCopyCode(int insertLength, int copyLength) {
    // Find insert code
    var insertCode = 0;
    for (var i = BrotliConstants.InsertLengthTable.Length - 1; i >= 0; --i) {
      if (insertLength < BrotliConstants.InsertLengthTable[i].BaseValue)
        continue;

      insertCode = i;
      break;
    }

    // Find copy code
    var copyCode = 0;
    if (copyLength >= 2)
      for (var i = BrotliConstants.CopyLengthTable.Length - 1; i >= 0; --i) {
        if (copyLength < BrotliConstants.CopyLengthTable[i].BaseValue)
          continue;

        copyCode = i;
        break;
      }

    // Combined code: simple mapping
    // For insert codes 0-7 and copy codes 0-7: code = insertCode * 8 + copyCode
    if (insertCode < 8 && copyCode < 8)
      return insertCode * 8 + copyCode;

    // Extended range
    return Math.Min(insertCode * 8 + copyCode, BrotliConstants.NumInsertAndCopyLengthCodes - 1);
  }

  private static void WriteInsertLengthExtra(BrotliBitWriter writer, int insertLength) {
    for (var i = BrotliConstants.InsertLengthTable.Length - 1; i >= 0; --i) {
      var (baseVal, extraBits) = BrotliConstants.InsertLengthTable[i];
      if (insertLength < baseVal)
        continue;

      if (extraBits > 0)
        writer.WriteBits(extraBits, (uint)(insertLength - baseVal));
      return;
    }
  }

  private static void WriteCopyLengthExtra(BrotliBitWriter writer, int copyLength) {
    for (var i = BrotliConstants.CopyLengthTable.Length - 1; i >= 0; --i) {
      var (baseVal, extraBits) = BrotliConstants.CopyLengthTable[i];
      if (copyLength < baseVal)
        continue;

      if (extraBits > 0)
        writer.WriteBits(extraBits, (uint)(copyLength - baseVal));
      return;
    }
  }

  /// <summary>
  /// Encodes a distance value into a distance code.
  /// Uses codes 0-15 for simple distances.
  /// </summary>
  private static int EncodeDistanceCode(int distance) {
    // Distance code 1 = distance 1, etc (with NPOSTFIX=0, NDIRECT=0)
    // Codes 16+: complex encoding with extra bits
    if (distance <= 0) return 0;

    // For simplicity, use complex distance codes
    // code = 16 + (hcode << nPostfix) + postfix
    // With nPostfix=0: code = 16 + hcode
    // hcode encodes: nBits-1 = (hcode >> 1) + 1, base = (2 + (hcode & 1)) << nBits - 4
    // We need: distance = base + extra + 1

    var d = distance - 1;
    if (d < 4)
      return 16 + d; // codes 16-19: 0 extra bits each

    // Find the right hcode
    var nBits = 1;
    while ((1 << (nBits + 1)) <= d)
      ++nBits;

    var hcode = ((nBits - 1) << 1) | ((d >> (nBits - 1)) & 1);
    if (hcode > 2) hcode = ((nBits - 1) << 1) | ((d >> nBits) & 1);

    return Math.Min(16 + hcode, 63);
  }

  private static void WriteDistanceExtra(BrotliBitWriter writer, int distance, int distCode) {
    if (distCode < 16) return; // no extra bits for codes 0-15

    var d = distance - 1;
    var hcode = distCode - 16;
    var nBits = (hcode >> 1) + 1;
    var baseDist = ((2 + (hcode & 1)) << nBits) - 4;
    var extra = d - baseDist;
    if (extra >= 0 && nBits > 0)
      writer.WriteBits(nBits, (uint)extra);
  }

  /// <summary>
  /// Builds Huffman code lengths from symbol frequencies using a simple algorithm.
  /// </summary>
  private static int[] BuildCodeLengths(int[] freq, int numSymbols) {
    var lengths = new int[numSymbols];

    // Count non-zero symbols
    var nonZero = 0;
    var lastNonZero = 0;
    for (var i = 0; i < numSymbols; ++i) {
      if (freq[i] <= 0)
        continue;

      ++nonZero;
      lastNonZero = i;
    }

    switch (nonZero) {
      case 0:
        // No symbols used; assign length 1 to symbol 0
        lengths[0] = 1;
        return lengths;

      case 1:
        lengths[lastNonZero] = 1;
        return lengths;
    }

    // Simple length assignment: all used symbols get the same length
    // This produces valid but suboptimal codes
    var maxLen = 1;
    while ((1 << maxLen) < nonZero)
      ++maxLen;

    // Assign lengths: first (2^maxLen - nonZero) symbols get maxLen-1,
    // rest get maxLen (to satisfy Kraft inequality)
    var shortCount = (1 << maxLen) - nonZero;
    var assigned = 0;
    for (var i = 0; i < numSymbols; ++i) {
      if (freq[i] <= 0)
        continue;

      lengths[i] = assigned < shortCount ? maxLen - 1 : maxLen;
      ++assigned;
      if (maxLen == 1)
        lengths[i] = 1; // force length 1 for 2-symbol case
    }

    // Ensure Kraft inequality holds: sum(2^-len) == 1
    // For the simple case, this is guaranteed by the assignment above

    return lengths;
  }

  /// <summary>
  /// Builds canonical code values from code lengths.
  /// Returns an array where codes[symbol] is the canonical code for that symbol.
  /// </summary>
  private static int[] BuildCanonicalCodes(int[] codeLengths, int numSymbols) {
    var codes = new int[numSymbols];

    var maxLen = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxLen = Math.Max(maxLen, codeLengths[i]);

    if (maxLen == 0) return codes;

    var blCount = new int[maxLen + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        blCount[codeLengths[i]]++;

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      if (len > 0)
        codes[sym] = nextCode[len]++;
    }

    return codes;
  }

  /// <summary>
  /// Writes a Huffman code to the bit stream (LSB-first, reversed).
  /// </summary>
  private static void WriteCode(BrotliBitWriter writer, int[] codes, int[] lengths, int symbol) {
    if (symbol >= lengths.Length || lengths[symbol] == 0) {
      // Symbol not in code table — write 0 code for symbol 0
      writer.WriteBits(lengths[0] > 0 ? lengths[0] : 1, 0);
      return;
    }

    var code = codes[symbol];
    var len = lengths[symbol];

    // Reverse bits for LSB-first writing
    var reversed = 0;
    for (var i = 0; i < len; ++i) {
      reversed = (reversed << 1) | (code & 1);
      code >>= 1;
    }

    writer.WriteBits(len, (uint)reversed);
  }

  /// <summary>
  /// Writes a prefix code definition to the stream using simple format
  /// (RFC 7932 Section 3.5).
  /// </summary>
  private static void WriteSimplePrefixCode(BrotliBitWriter writer, int[] codeLengths, int numSymbols) {
    // Count used symbols
    var usedSymbols = new List<int>();
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        usedSymbols.Add(i);

    var numSymBits = 0;
    var a = numSymbols;
    while (a > 1) { a >>= 1; ++numSymBits; }
    if (numSymBits == 0) numSymBits = 1;

    if (usedSymbols.Count <= 4) {
      // Use simple prefix code format (HSKIP=1)
      writer.WriteBits(2, 1); // HSKIP = 1

      var count = usedSymbols.Count;
      writer.WriteBits(2, (uint)(count - 1)); // NSYM - 1

      for (var i = 0; i < count; ++i)
        writer.WriteBits(numSymBits, (uint)usedSymbols[i]);

      // For 4 symbols with equal lengths, write tree_select bit
      if (count != 4)
        return;

      var allEqual = codeLengths[usedSymbols[0]] == codeLengths[usedSymbols[1]] &&
        codeLengths[usedSymbols[1]] == codeLengths[usedSymbols[2]] &&
        codeLengths[usedSymbols[2]] == codeLengths[usedSymbols[3]];
      writer.WriteBits(1, allEqual ? 1u : 0u);
    } else
      // Use complex prefix code format
      WriteComplexPrefixCode(writer, codeLengths, numSymbols);
  }

  /// <summary>
  /// Writes a complex prefix code to the stream (RFC 7932 Section 3.5).
  /// </summary>
  private static void WriteComplexPrefixCode(BrotliBitWriter writer, int[] codeLengths, int numSymbols) {
    // HSKIP = 0 (start from position 0 in the code length code order)
    writer.WriteBits(2, 0);

    // Build code-length code lengths
    // First, determine which code length values are used
    var clFreq = new int[BrotliConstants.NumCodeLengthCodes];
    var lastNonZero = -1;

    for (var i = 0; i < numSymbols; ++i) {
      if (codeLengths[i] <= 0 || codeLengths[i] >= BrotliConstants.NumCodeLengthCodes)
        continue;

      clFreq[codeLengths[i]]++;
      lastNonZero = i;
    }

    // Count zeros for run-length encoding
    var zeroRuns = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] == 0) 
        ++zeroRuns;

    if (zeroRuns > 2) 
      ++clFreq[17]; // repeat zero

    // Assign code lengths to code length symbols
    var clLengths = new int[BrotliConstants.NumCodeLengthCodes];
    for (var i = 0; i < BrotliConstants.NumCodeLengthCodes; ++i)
      if (clFreq[i] > 0)
        clLengths[i] = 3; // Simple: all used CL symbols get length 3

    // Ensure at least 2 symbols have non-zero lengths
    var clUsed = 0;
    for (var i = 0; i < BrotliConstants.NumCodeLengthCodes; ++i)
      if (clLengths[i] > 0) 
        ++clUsed;

    if (clUsed < 2) {
      // Add symbol 0 if needed
      if (clLengths[0] == 0) 
        clLengths[0] = 3;

      ++clUsed;
    }

    // Write code-length code lengths in the specified order
    // Determine how many we need to write
    var clCount = BrotliConstants.NumCodeLengthCodes;
    while (clCount > 0 && clLengths[BrotliConstants.CodeLengthCodeOrder[clCount - 1]] == 0)
      --clCount;

    // Write each code-length code length using variable-length encoding
    for (var i = 0; i < clCount; ++i) {
      int idx = BrotliConstants.CodeLengthCodeOrder[i];
      var len = clLengths[idx];
      WriteSmallCodeLength(writer, len);
    }

    // Now write the actual code lengths using the CL tree
    var clCodes = BuildCanonicalCodes(clLengths, BrotliConstants.NumCodeLengthCodes);

    for (var i = 0; i < numSymbols; ++i) {
      var cl = codeLengths[i];
      if (cl is > 0 and < BrotliConstants.NumCodeLengthCodes) {
        // Write code length symbol directly
        var code = clCodes[cl];
        var codeLen = clLengths[cl];
        if (codeLen <= 0)
          continue;

        var reversed = 0;
        for (var b = 0; b < codeLen; ++b) {
          reversed = (reversed << 1) | (code & 1);
          code >>= 1;
        }
        writer.WriteBits(codeLen, (uint)reversed);
      } else {
        // Zero: write as code length 0
        if (clLengths[0] <= 0)
          continue;

        var code = clCodes[0];
        var codeLen = clLengths[0];
        var reversed = 0;
        for (var b = 0; b < codeLen; ++b) {
          reversed = (reversed << 1) | (code & 1);
          code >>= 1;
        }
        writer.WriteBits(codeLen, (uint)reversed);
      }
    }
  }

  /// <summary>
  /// Writes a small code length value (0-5) using the variable-length encoding.
  /// </summary>
  private static void WriteSmallCodeLength(BrotliBitWriter writer, int len) {
    switch (len) {
      case 0:
        writer.WriteBits(1, 0);
        break;
      case 1:
        writer.WriteBits(2, 0b10);
        break;
      case 2:
        writer.WriteBits(3, 0b110);
        break;
      case 3:
        writer.WriteBits(4, 0b1110);
        break;
      case 4:
        writer.WriteBits(5, 0b11110);
        break;
      case 5:
        writer.WriteBits(5, 0b11111);
        break;
    }
  }
}

/// <summary>
/// Bit writer for Brotli streams. Writes bits LSB-first.
/// </summary>
internal sealed class BrotliBitWriter {
  private readonly Stream _output;
  private uint _bitBuffer;
  private int _bitsUsed;

  /// <summary>
  /// Initializes a new <see cref="BrotliBitWriter"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public BrotliBitWriter(Stream output) => this._output = output;

  /// <summary>
  /// Writes <paramref name="count"/> bits (LSB-first) to the stream.
  /// </summary>
  /// <param name="count">Number of bits to write (1-24).</param>
  /// <param name="value">The value whose low <paramref name="count"/> bits are written.</param>
  public void WriteBits(int count, uint value) {
    this._bitBuffer |= (value & ((1u << count) - 1)) << this._bitsUsed;
    this._bitsUsed += count;
    while (this._bitsUsed >= 8) {
      this._output.WriteByte((byte)(this._bitBuffer & 0xFF));
      this._bitBuffer >>= 8;
      this._bitsUsed -= 8;
    }
  }

  /// <summary>
  /// Writes a single raw byte (must be byte-aligned).
  /// </summary>
  /// <param name="value">The byte to write.</param>
  public void WriteByte(byte value) {
    if (this._bitsUsed == 0)
      this._output.WriteByte(value);
    else
      this.WriteBits(8, value);
  }

  /// <summary>
  /// Aligns to the next byte boundary by writing zero padding bits.
  /// </summary>
  public void AlignToByte() {
    if (this._bitsUsed <= 0)
      return;

    var padding = 8 - this._bitsUsed;
    this.WriteBits(padding, 0);
  }

  /// <summary>
  /// Flushes any remaining partial byte to the output stream.
  /// </summary>
  public void Flush() {
    if (this._bitsUsed <= 0)
      return;

    this._output.WriteByte((byte)(this._bitBuffer & 0xFF));
    this._bitBuffer = 0;
    this._bitsUsed = 0;
  }
}
