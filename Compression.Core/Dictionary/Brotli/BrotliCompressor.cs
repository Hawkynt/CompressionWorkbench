using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Compresses data in the Brotli format (RFC 7932).
/// </summary>
/// <remarks>
/// Supports two modes:
/// <list type="bullet">
///   <item>Compress: Uses uncompressed meta-blocks (fast, no compression ratio).</item>
///   <item><see cref="CompressLz77"/>: Uses LZ77 + Huffman compressed meta-blocks (actual compression).</item>
/// </list>
/// </remarks>
public static class BrotliCompressor {
  /// <summary>
  /// Compresses data to the Brotli format at the specified compression level.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, BrotliCompressionLevel level) {
    if (level == BrotliCompressionLevel.Uncompressed || data.Length < 16)
      return Compress(data);

    var lz77 = CompressLz77(data);
    var uncomp = Compress(data);
    return lz77.Length < uncomp.Length ? lz77 : uncomp;
  }

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
        // Insert skipped positions into the hash chain so future matches
        // can reference nearby positions instead of distant ones
        var end = Math.Min(pos + bestMatch.Length - 1, data.Length - 4);
        for (var j = pos + 1; j <= end; ++j)
          matchFinder.InsertPosition(data, j);
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
  /// <summary>
  /// Checks whether a command fits in an IAC range of the specified type.
  /// </summary>
  private static bool FitsRange(int insertLength, int copyLength, bool wantImplicit) {
    var insertCode = FindTableCode(BrotliConstants.InsertLengthTable, insertLength);
    var copyCode = copyLength >= 2 ? FindTableCode(BrotliConstants.CopyLengthTable, copyLength) : 0;
    foreach (var (insBase, cpBase, _, isImplicit) in IacRanges) {
      if (isImplicit != wantImplicit) continue;
      if (insertCode - insBase is >= 0 and <= 7 && copyCode - cpBase is >= 0 and <= 7) return true;
    }
    return false;
  }

  /// <summary>
  /// Preprocesses commands to resolve implicit vs explicit distance encoding.
  /// Distance = -1 in the output means "use implicit distance" (last distance from ring).
  /// Commands that can't be encoded in any available range are converted to literals.
  /// </summary>
  private static List<LzCommand> ResolveDistanceEncoding(List<LzCommand> commands) {
    int[] distRing = [16, 15, 11, 4];
    var distRingIdx = 3;
    var resolved = new List<LzCommand>(commands.Count);

    foreach (var cmd in commands) {
      if (cmd.CopyLength <= 0) {
        resolved.Add(cmd);
        continue;
      }

      var lastDist = distRing[distRingIdx & 3];
      var canImplicit = cmd.Distance == lastDist && FitsRange(cmd.InsertLength, cmd.CopyLength, true);
      var canExplicit = FitsRange(cmd.InsertLength, cmd.CopyLength, false);

      if (canImplicit) {
        resolved.Add(cmd with { Distance = -1 });
      } else if (canExplicit) {
        resolved.Add(cmd);
      } else {
        // Try adjusting insert/copy split to fit an explicit range.
        // Moving bytes from copy to insert is safe: those bytes exist in the data
        // array at the correct position, and the remaining copy references the
        // correct ring buffer offset.
        var total = cmd.InsertLength + cmd.CopyLength;
        var adjusted = false;
        for (var ni = cmd.InsertLength + 1; ni <= total - 2; ++ni) {
          if (!FitsRange(ni, total - ni, false)) continue;
          resolved.Add(new LzCommand(ni, total - ni, cmd.Distance));
          adjusted = true;
          break;
        }

        if (!adjusted) {
          // Can't encode as match — convert to literals
          resolved.Add(new LzCommand(total, 0, 0));
          continue;
        }
      }

      distRingIdx = (distRingIdx + 1) & 3;
      distRing[distRingIdx] = cmd.Distance > 0 ? cmd.Distance : lastDist;
    }

    // Merge non-final literal-only commands into the next command's insert.
    // Literal-only commands in non-final position corrupt the stream because the
    // decoder always decodes a copy length and may attempt to copy bytes.
    for (var i = resolved.Count - 2; i >= 0; --i) {
      if (resolved[i].CopyLength != 0) continue;
      var next = resolved[i + 1];
      resolved[i + 1] = new LzCommand(resolved[i].InsertLength + next.InsertLength,
        next.CopyLength, next.Distance);
      resolved.RemoveAt(i);
    }

    return resolved;
  }

  private static void EmitCompressedMetaBlock(BrotliBitWriter writer, byte[] data,
    List<LzCommand> commands, bool isLast) {
    var totalBytes = data.Length;
    commands = ResolveDistanceEncoding(commands);
    // ISLAST
    writer.WriteBits(1, isLast ? 1u : 0u);
    if (isLast)
      writer.WriteBits(1, 0); // ISEMPTY = 0

    // MLEN: MNIBBLES nibbles (4, 5, or 6), encoded as MNIBBLES-4 in 2 bits
    var mlen = totalBytes - 1;
    var mNibbles = mlen <= 0xFFFF ? 4 : mlen <= 0xFFFFF ? 5 : 6;
    writer.WriteBits(2, (uint)(mNibbles - 4));
    for (var n = 0; n < mNibbles; ++n)
      writer.WriteBits(4, (uint)((mlen >> (n * 4)) & 0xF));

    if (!isLast)
      writer.WriteBits(1, 0); // ISUNCOMPRESSED = 0

    // Block type counts: 1 each
    WriteBlockTypeCount(writer, 1); // literal
    WriteBlockTypeCount(writer, 1); // insert&copy
    WriteBlockTypeCount(writer, 1); // distance

    // NPOSTFIX = 0, NDIRECT = 0
    writer.WriteBits(2, 0);
    writer.WriteBits(4, 0);

    // Context mode: LSB6
    writer.WriteBits(2, 0);

    // Context maps: 1 tree each
    WriteBlockTypeCount(writer, 1); // num literal trees
    WriteBlockTypeCount(writer, 1); // num distance trees

    // Build frequency tables
    var litFreq = new int[256];
    var iacFreq = new int[BrotliConstants.NumInsertAndCopyLengthCodes];
    var distAlphabetSize = 16 + 0 + (48 << 0); // 64
    var distFreq = new int[distAlphabetSize];

    foreach (var cmd in commands) {
      var useImplicit = cmd is { CopyLength: > 0, Distance: -1 };
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength, useImplicit);
      if (iacCode < iacFreq.Length)
        ++iacFreq[iacCode];
    }

    var litPos = 0;
    foreach (var cmd in commands) {
      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i)
        ++litFreq[data[litPos++]];
      litPos += cmd.CopyLength;
    }

    foreach (var cmd in commands) {
      if (cmd.CopyLength <= 0 || cmd.Distance <= 0) continue;
      var distCode = EncodeDistanceCode(cmd.Distance);
      if (distCode < distFreq.Length) ++distFreq[distCode];
    }

    // Build and write Huffman trees
    var litLengths = BuildCodeLengths(litFreq, 256);
    WriteSimplePrefixCode(writer, litLengths, 256);

    var iacLengths = BuildCodeLengths(iacFreq, BrotliConstants.NumInsertAndCopyLengthCodes);
    WriteSimplePrefixCode(writer, iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);

    var distLengths = BuildCodeLengths(distFreq, distAlphabetSize);
    WriteSimplePrefixCode(writer, distLengths, distAlphabetSize);

    var litCodes = BuildCanonicalCodes(litLengths, 256);
    var iacCodes = BuildCanonicalCodes(iacLengths, BrotliConstants.NumInsertAndCopyLengthCodes);
    var distCodes = BuildCanonicalCodes(distLengths, distAlphabetSize);

    // Detect single-symbol trees — RFC 7932: single-symbol prefix codes consume 0 bits
    var singleLit = litLengths.Count(l => l > 0) <= 1;
    var singleIac = iacLengths.Count(l => l > 0) <= 1;
    var singleDist = distLengths.Count(l => l > 0) <= 1;

    // Encode commands
    litPos = 0;
    foreach (var cmd in commands) {
      var useImplicit = cmd is { CopyLength: > 0, Distance: -1 };
      var iacCode = EncodeInsertAndCopyCode(cmd.InsertLength, cmd.CopyLength, useImplicit);
      if (!singleIac) WriteCode(writer, iacCodes, iacLengths, iacCode);

      WriteInsertLengthExtra(writer, cmd.InsertLength);

      if (cmd.CopyLength > 0)
        WriteCopyLengthExtra(writer, cmd.CopyLength);

      for (var i = 0; i < cmd.InsertLength && litPos < data.Length; ++i) {
        if (!singleLit) WriteCode(writer, litCodes, litLengths, data[litPos]);
        ++litPos;
      }

      litPos += cmd.CopyLength;

      // Write distance only for explicit-distance commands
      if (cmd.CopyLength <= 0 || cmd.Distance <= 0) continue;

      var distCode = EncodeDistanceCode(cmd.Distance);
      if (!singleDist) WriteCode(writer, distCodes, distLengths, distCode);
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
  /// RFC 7932 Table 8 range definitions: (insertCodeBase, copyCodeBase, iacCodeBase, implicit).
  /// Ranges 0-1 use implicit distance (codes 0-127).
  /// Ranges 2-8 use explicit distance (codes 128-575).
  /// </summary>
  private static readonly (int InsBase, int CpBase, int CodeBase, bool Implicit)[] IacRanges = [
    (0, 0, 0, true),      // range 0:  insert 0-7,   copy 0-7,   implicit
    (0, 8, 64, true),     // range 1:  insert 0-7,   copy 8-15,  implicit
    (0, 0, 128, false),   // range 2:  insert 0-7,   copy 0-7,   explicit
    (0, 8, 192, false),   // range 3:  insert 0-7,   copy 8-15,  explicit
    (8, 0, 256, false),   // range 4:  insert 8-15,  copy 0-7,   explicit
    (8, 8, 320, false),   // range 5:  insert 8-15,  copy 8-15,  explicit
    (0, 16, 384, false),  // range 6:  insert 0-7,   copy 16-23, explicit
    (16, 0, 448, false),  // range 7:  insert 16-23, copy 0-7,   explicit
    (8, 16, 512, false),  // range 8:  insert 8-15,  copy 16-23, explicit
    (16, 8, 576, false),  // range 9:  insert 16-23, copy 8-15,  explicit
    (16, 16, 640, false)  // range 10: insert 16-23, copy 16-23, explicit
  ];

  /// <summary>
  /// Encodes insert and copy lengths into a combined insert-and-copy code (RFC 7932 Table 8).
  /// </summary>
  /// <param name="insertLength">Number of literal bytes to insert.</param>
  /// <param name="copyLength">Number of bytes to copy (0 for literal-only).</param>
  /// <param name="useImplicitDistance">When true, uses implicit distance ranges (codes 0-127).</param>
  private static int EncodeInsertAndCopyCode(int insertLength, int copyLength, bool useImplicitDistance) {
    var insertCode = FindTableCode(BrotliConstants.InsertLengthTable, insertLength);
    var copyCode = copyLength >= 2
      ? FindTableCode(BrotliConstants.CopyLengthTable, copyLength)
      : 0;

    if (useImplicitDistance) {
      // Find matching implicit range (verified to fit by ResolveDistanceEncoding)
      foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
        if (!isImplicit) continue;
        var insOff = insertCode - insBase;
        var cpOff = copyCode - cpBase;
        if (insOff is >= 0 and <= 7 && cpOff is >= 0 and <= 7)
          return codeBase + insOff * 8 + cpOff;
      }
    }

    // Literal-only: must use implicit ranges (codes 0-127) so decoder doesn't expect a distance.
    // The decoder stops before copying when metaBytesRemaining hits zero.
    if (copyLength == 0) {
      foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
        if (!isImplicit) continue;
        var insOff = insertCode - insBase;
        if (insOff is >= 0 and <= 7 && cpBase == 0)
          return codeBase + insOff * 8;
      }
    }

    // Find a valid explicit-distance range
    foreach (var (insBase, cpBase, codeBase, isImplicit) in IacRanges) {
      if (isImplicit) continue;
      var insOff = insertCode - insBase;
      var cpOff = copyCode - cpBase;
      if (insOff is < 0 or > 7 || cpOff is < 0 or > 7) continue;
      return codeBase + (insOff << 3) + cpOff;
    }

    // Fallback: clamp to range 2 (insert 0-7, copy 0-7, explicit)
    return 128 + (Math.Clamp(insertCode, 0, 7)) * 8 + Math.Min(copyCode, 7);
  }

  private static int FindTableCode((int BaseValue, int ExtraBits)[] table, int value) {
    for (var i = table.Length - 1; i >= 0; --i)
      if (value >= table[i].BaseValue)
        return i;
    return 0;
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
  /// Encodes a distance value into a distance code (NPOSTFIX=0, NDIRECT=0).
  /// Uses complex distance codes 16+ with extra bits.
  /// dcode = code - 16; nBits = 1 + (dcode >> 1); base = ((2 + (dcode &amp; 1)) &lt;&lt; nBits) - 4;
  /// distance = base + extra + 1.
  /// </summary>
  private static int EncodeDistanceCode(int distance) {
    if (distance <= 0) return 16;

    var d = distance - 1;
    for (var dcode = 0; dcode < 48; ++dcode) {
      var nBits = 1 + (dcode >> 1);
      var baseDist = ((2 + (dcode & 1)) << nBits) - 4;
      if (d >= baseDist && d < baseDist + (1 << nBits))
        return 16 + dcode;
    }

    return 16 + 47;
  }

  private static void WriteDistanceExtra(BrotliBitWriter writer, int distance, int distCode) {
    if (distCode < 16) return;

    var dcode = distCode - 16;
    var nBits = 1 + (dcode >> 1);
    var baseDist = ((2 + (dcode & 1)) << nBits) - 4;
    var extra = (distance - 1) - baseDist;
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
        // No symbols used; assign length 1 to two symbols so the Kraft
        // inequality holds (sum(2^-len) = 1).  This is required when the
        // code lengths are used inside a complex prefix code's CL tree,
        // because the decoder reads CL entries until its space counter
        // reaches zero.
        lengths[0] = 1;
        lengths[numSymbols > 1 ? 1 : 0] = 1;
        return lengths;

      case 1:
        // Single symbol: pair it with a dummy so sum(2^-1 + 2^-1) = 1.
        lengths[lastNonZero] = 1;
        lengths[lastNonZero == 0 ? 1 : 0] = 1;
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

    // RFC 7932: symbol values use max(1, ceil(log2(ALPHABET_SIZE))) bits
    var numSymBits = 1;
    while ((1 << numSymBits) < numSymbols)
      ++numSymBits;

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
  /// The CL tree must satisfy the Kraft inequality so the decompressor's
  /// space counter reaches exactly zero.
  /// </summary>
  private static void WriteComplexPrefixCode(BrotliBitWriter writer, int[] codeLengths, int numSymbols) {
    // Find last nonzero code length position — we only need to write up to here
    var lastNonZero = 0;
    for (var i = numSymbols - 1; i >= 0; --i)
      if (codeLengths[i] > 0) {
        lastNonZero = i;
        break;
      }

    // Count frequencies of each code length value (0-15) for the CL tree
    var clFreq = new int[BrotliConstants.NumCodeLengthCodes];
    for (var i = 0; i <= lastNonZero; ++i) {
      var cl = codeLengths[i];
      if (cl >= 0 && cl < BrotliConstants.NumCodeLengthCodes)
        clFreq[cl]++;
    }

    // Build proper CL code lengths that satisfy Kraft inequality
    var clLengths = BuildCodeLengths(clFreq, BrotliConstants.NumCodeLengthCodes);

    // HSKIP = 0 (start from position 0 in the code length code order)
    writer.WriteBits(2, 0);

    // Determine how many CL code lengths to write (up to last nonzero in order)
    var clCount = BrotliConstants.NumCodeLengthCodes;
    while (clCount > 0 && clLengths[BrotliConstants.CodeLengthCodeOrder[clCount - 1]] == 0)
      --clCount;

    // Write each CL code length using variable-length encoding
    for (var i = 0; i < clCount; ++i) {
      int idx = BrotliConstants.CodeLengthCodeOrder[i];
      WriteSmallCodeLength(writer, clLengths[idx]);
    }

    // Write actual code lengths using the CL tree (only up to lastNonZero)
    var clCodes = BuildCanonicalCodes(clLengths, BrotliConstants.NumCodeLengthCodes);

    for (var i = 0; i <= lastNonZero; ++i)
      WriteCode(writer, clCodes, clLengths, codeLengths[i]);
  }

  /// <summary>
  /// Writes a small code length value (0-5) using the fixed prefix code
  /// from RFC 7932 Section 3.5, matching the decoder's 4-bit peek table.
  /// </summary>
  private static void WriteSmallCodeLength(BrotliBitWriter writer, int len) {
    switch (len) {
      case 0: writer.WriteBits(2, 0);  break; // 00
      case 1: writer.WriteBits(4, 7);  break; // 0111
      case 2: writer.WriteBits(3, 3);  break; // 011
      case 3: writer.WriteBits(2, 2);  break; // 10
      case 4: writer.WriteBits(2, 1);  break; // 01
      case 5: writer.WriteBits(4, 15); break; // 1111
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
