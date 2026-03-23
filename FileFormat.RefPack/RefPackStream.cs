using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.RefPack;

/// <summary>
/// Compressor and decompressor for the RefPack (EA/DBPF) compression format.
/// RefPack is a multi-width opcode LZ77 variant used by Electronic Arts in
/// various game file formats (SimCity 4, The Sims 2, etc.).
/// </summary>
public static class RefPackStream {

  /// <summary>The signature byte that identifies a RefPack stream.</summary>
  private const byte Signature = 0xFB;

  /// <summary>Default flags byte for a standard 5-byte header (no compressed size field).</summary>
  private const byte DefaultFlags = 0x10;

  /// <summary>Maximum uncompressed size encodable in a 3-byte BE field (16 MB - 1).</summary>
  private const int MaxUncompressedSize = 0xFFFFFF;

  /// <summary>Window size for the LZ77 match finder (131072 = 128 KB, must be power of 2).</summary>
  private const int WindowSize = 131072;

  /// <summary>Maximum literals that can be encoded in a single 1-byte literal-run opcode.</summary>
  private const int MaxLiteralRun = 112;

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decompresses a RefPack-compressed stream and writes the original data to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream positioned at the start of a RefPack header.</param>
  /// <param name="output">Stream that receives the decompressed data.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the header is invalid or the data is corrupted.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var decompressed = DecompressCore(data);
    output.Write(decompressed);
  }

  /// <summary>
  /// Compresses raw data into the RefPack format and writes the result to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream containing the raw data to compress.</param>
  /// <param name="output">Stream that receives the RefPack-encoded output.</param>
  /// <exception cref="ArgumentException">
  /// Thrown when the input data exceeds the maximum encodable size (16 MB - 1).
  /// </exception>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var compressed = CompressCore(data);
    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses a RefPack-compressed byte array and returns the original data.
  /// </summary>
  /// <param name="data">The complete RefPack file contents.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the header is invalid or the data is corrupted.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) => DecompressCore(data);

  /// <summary>
  /// Compresses raw data into the RefPack format and returns the result as a new byte array.
  /// </summary>
  /// <param name="data">The raw bytes to compress.</param>
  /// <exception cref="ArgumentException">
  /// Thrown when the input data exceeds the maximum encodable size (16 MB - 1).
  /// </exception>
  public static byte[] Compress(ReadOnlySpan<byte> data) => CompressCore(data);

  // ── Decompression core ────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < 5)
      throw new InvalidDataException("Input is too short to contain a valid RefPack header.");

    var pos = 0;
    var flags = data[pos++];

    // If bit 0 is set, a 4-byte BE compressed size follows before the signature
    if ((flags & 0x01) != 0) {
      if (data.Length < 9)
        throw new InvalidDataException("Input is too short for a 9-byte RefPack header.");

      // Skip 4-byte compressed size
      pos += 4;
    }

    // Validate signature byte
    if (data[pos] != Signature)
      throw new InvalidDataException($"Invalid RefPack signature: expected 0x{Signature:X2}, got 0x{data[pos]:X2}.");

    ++pos;

    // Read 3-byte BE uncompressed size
    var uncompressedSize = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
    pos += 3;

    var output = new byte[uncompressedSize];
    var outputPos = 0;

    // Process opcodes
    while (outputPos < uncompressedSize) {
      if (pos >= data.Length)
        throw new InvalidDataException("Unexpected end of RefPack data.");

      var b0 = data[pos++];

      int numLiterals;
      int copyLength;
      int copyOffset;

      if (b0 is >= 0x00 and <= 0x7F) {
        // 2-byte opcode: short copy
        if (pos >= data.Length)
          throw new InvalidDataException("Unexpected end of RefPack data in 2-byte opcode.");

        var b1 = data[pos++];
        numLiterals = b0 & 0x03;
        copyLength = ((b0 & 0x1C) >> 2) + 3;
        copyOffset = ((b0 & 0x60) << 3) + b1 + 1;
      } else if (b0 is >= 0x80 and <= 0xBF) {
        // 3-byte opcode: medium copy
        if (pos + 1 >= data.Length)
          throw new InvalidDataException("Unexpected end of RefPack data in 3-byte opcode.");

        var b1 = data[pos++];
        var b2 = data[pos++];
        numLiterals = ((b1 & 0xC0) >> 6) & 0x03;
        copyLength = (b0 & 0x3F) + 4;
        copyOffset = ((b1 & 0x3F) << 8) + b2 + 1;
      } else if (b0 is >= 0xC0 and <= 0xDF) {
        // 4-byte opcode: long copy
        if (pos + 2 >= data.Length)
          throw new InvalidDataException("Unexpected end of RefPack data in 4-byte opcode.");

        var b1 = data[pos++];
        var b2 = data[pos++];
        var b3 = data[pos++];
        numLiterals = b0 & 0x03;
        copyLength = ((b0 & 0x0C) << 6) + b3 + 5;
        copyOffset = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
      } else if (b0 is >= 0xE0 and <= 0xFB) {
        // 1-byte opcode: literal run
        numLiterals = ((b0 & 0x1F) << 2) + 4;
        copyLength = 0;
        copyOffset = 0;
      } else {
        // 0xFC-0xFF: stop codes
        numLiterals = b0 & 0x03;

        // Copy the final literals and stop
        for (var i = 0; i < numLiterals; ++i) {
          if (pos >= data.Length)
            throw new InvalidDataException("Unexpected end of RefPack data in stop code literals.");

          output[outputPos++] = data[pos++];
        }

        break;
      }

      // Copy literals from input
      for (var i = 0; i < numLiterals; ++i) {
        if (pos >= data.Length)
          throw new InvalidDataException("Unexpected end of RefPack data while reading literals.");

        output[outputPos++] = data[pos++];
      }

      // Copy from back-reference
      if (copyLength > 0) {
        var copyFrom = outputPos - copyOffset;
        if (copyFrom < 0)
          throw new InvalidDataException($"Invalid back-reference: offset {copyOffset} exceeds available output at position {outputPos}.");

        for (var i = 0; i < copyLength; ++i)
          output[outputPos++] = output[copyFrom + i];
      }
    }

    return output;
  }

  // ── Compression core ──────────────────────────────────────────────────────

  private static byte[] CompressCore(ReadOnlySpan<byte> input) {
    if (input.Length > MaxUncompressedSize)
      throw new ArgumentException($"Input size {input.Length} exceeds the maximum encodable size of {MaxUncompressedSize} bytes.", nameof(input));

    using var ms = new MemoryStream();

    // Write 5-byte header: flags(1) + 0xFB(1) + uncompressedSize(3)
    ms.WriteByte(DefaultFlags);
    ms.WriteByte(Signature);
    ms.WriteByte((byte)((input.Length >> 16) & 0xFF));
    ms.WriteByte((byte)((input.Length >> 8) & 0xFF));
    ms.WriteByte((byte)(input.Length & 0xFF));

    if (input.Length == 0) {
      // Empty input: write stop code with 0 trailing literals
      ms.WriteByte(0xFC);
      return ms.ToArray();
    }

    var matchFinder = new HashChainMatchFinder(WindowSize);
    var pendingLiterals = new List<byte>();
    var pos = 0;

    while (pos < input.Length) {
      // Find longest match (minimum 3 bytes)
      var maxLen = Math.Min(input.Length - pos, 1028);
      var match = pos >= 3
        ? matchFinder.FindMatch(input, pos, WindowSize - 1, maxLen, 3)
        : default;

      // Clamp match to encodable length given the distance
      var usableLength = ClampMatchLength(match.Distance, match.Length);

      if (usableLength >= 3) {
        // Flush pending literals before writing match
        FlushLiterals(ms, pendingLiterals, input);

        // Encode match with the narrowest opcode that fits
        EmitMatch(ms, match.Distance, usableLength, pendingLiterals, input, pos);

        // Insert skipped positions into hash chain
        for (var i = 1; i < usableLength; ++i)
          if (pos + i + 2 < input.Length)
            matchFinder.InsertPosition(input, pos + i);

        pos += usableLength;
      } else {
        pendingLiterals.Add(input[pos]);
        matchFinder.InsertPosition(input, pos);
        ++pos;
      }
    }

    // Flush any remaining literals and write stop code
    FlushLiteralsAndStop(ms, pendingLiterals, input);

    return ms.ToArray();
  }

  /// <summary>
  /// Returns the usable match length given a distance, or 0 if the match cannot be
  /// encoded in any opcode. Some distance ranges require minimum lengths:
  /// offset 1..1024 needs length 3+, offset 1..16384 needs length 4+,
  /// offset 1..131072 needs length 5+.
  /// </summary>
  private static int ClampMatchLength(int distance, int length) {
    if (length <= 0 || distance <= 0)
      return 0;

    if (distance <= 1024 && length >= 3)
      return Math.Min(length, 1028); // 2-byte opcode handles 3-10, but longer matches use wider opcodes
    if (distance <= 16384 && length >= 4)
      return Math.Min(length, 1028);
    if (distance <= 131072 && length >= 5)
      return Math.Min(length, 1028);

    return 0; // distance/length combination not encodable
  }

  /// <summary>
  /// Flushes accumulated literals using 1-byte literal-run opcodes (0xE0-0xFB).
  /// Each opcode encodes 4 to 112 literals: count = ((opcode &amp; 0x1F) &lt;&lt; 2) + 4.
  /// </summary>
  private static void FlushLiterals(MemoryStream ms, List<byte> literals, ReadOnlySpan<byte> input) {
    var offset = 0;
    while (offset + 4 <= literals.Count) {
      var remaining = literals.Count - offset;
      var count = Math.Min(remaining, MaxLiteralRun);

      // Round down to a multiple of 4, since count = (n << 2) + 4 means count is always 4+4k
      // Actually count = ((opcode & 0x1F) << 2) + 4, so valid counts are 4, 8, 12, ..., 112
      // Largest opcode & 0x1F = 0x1B = 27, so max = 27*4+4 = 112
      // We want the largest multiple of 4 that is >= 4 and <= min(remaining, 112)
      count = (count / 4) * 4;
      if (count < 4)
        break;

      var opcodeField = (count - 4) / 4;
      ms.WriteByte((byte)(0xE0 | opcodeField));

      for (var i = 0; i < count; ++i)
        ms.WriteByte(literals[offset + i]);

      offset += count;
    }

    // Keep any remaining 0-3 literals for the next match opcode to encode
    if (offset > 0)
      literals.RemoveRange(0, offset);
  }

  /// <summary>
  /// Emits a match opcode, encoding any remaining pending literals (0-3) into the opcode.
  /// </summary>
  private static void EmitMatch(MemoryStream ms, int distance, int length, List<byte> pendingLiterals, ReadOnlySpan<byte> input, int pos) {
    var numLiterals = pendingLiterals.Count; // 0-3 after FlushLiterals
    var offset = distance; // 1-based distance from FindMatch

    if (length >= 3 && length <= 10 && offset >= 1 && offset <= 1024) {
      // 2-byte opcode: short copy
      var b0 = ((numLiterals & 0x03))
             | (((length - 3) & 0x07) << 2)
             | (((offset - 1) >> 3) & 0x60);
      var b1 = (offset - 1) & 0xFF;

      ms.WriteByte((byte)b0);
      ms.WriteByte((byte)b1);
      WritePendingLiteralBytes(ms, pendingLiterals);
    } else if (length >= 4 && length <= 67 && offset >= 1 && offset <= 16384) {
      // 3-byte opcode: medium copy
      var b0 = 0x80 | ((length - 4) & 0x3F);
      var b1 = ((numLiterals & 0x03) << 6) | (((offset - 1) >> 8) & 0x3F);
      var b2 = (offset - 1) & 0xFF;

      ms.WriteByte((byte)b0);
      ms.WriteByte((byte)b1);
      ms.WriteByte((byte)b2);
      WritePendingLiteralBytes(ms, pendingLiterals);
    } else if (length >= 5 && length <= 1028 && offset >= 1 && offset <= 131072) {
      // 4-byte opcode: long copy
      var b0 = 0xC0
             | (numLiterals & 0x03)
             | (((length - 5) >> 6) & 0x0C)
             | (((offset - 1) >> 12) & 0x10);
      var b1 = ((offset - 1) >> 8) & 0xFF;
      var b2 = (offset - 1) & 0xFF;
      var b3 = (length - 5) & 0xFF;

      ms.WriteByte((byte)b0);
      ms.WriteByte((byte)b1);
      ms.WriteByte((byte)b2);
      ms.WriteByte((byte)b3);
      WritePendingLiteralBytes(ms, pendingLiterals);
    } else {
      // Match does not fit any opcode — treat as literals
      // This shouldn't happen with our match finder constraints, but handle gracefully
      return;
    }
  }

  /// <summary>
  /// Writes the raw bytes of pending literals (0-3) to the stream, then clears the list.
  /// These are the literals encoded in the preceding literal count of the match opcode.
  /// </summary>
  private static void WritePendingLiteralBytes(MemoryStream ms, List<byte> literals) {
    for (var i = 0; i < literals.Count; ++i)
      ms.WriteByte(literals[i]);

    literals.Clear();
  }

  /// <summary>
  /// Flushes all remaining literals and writes the stop code.
  /// </summary>
  private static void FlushLiteralsAndStop(MemoryStream ms, List<byte> literals, ReadOnlySpan<byte> input) {
    // Flush complete blocks of 4+ literals
    FlushLiterals(ms, literals, input);

    // Write stop code with 0-3 trailing literals
    var trailingCount = literals.Count;
    ms.WriteByte((byte)(0xFC | (trailingCount & 0x03)));

    for (var i = 0; i < trailingCount; ++i)
      ms.WriteByte(literals[i]);

    literals.Clear();
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
