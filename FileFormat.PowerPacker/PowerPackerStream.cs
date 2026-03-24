using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.PowerPacker;

/// <summary>
/// Compressor and decompressor for the Amiga PowerPacker (PP20) crunched file format.
/// PP20 is a backward-decoding LZ77 variant: both the bit stream and the output buffer
/// are consumed from end to start.
/// </summary>
public static class PowerPackerStream {

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decompresses a PP20-crunched stream and writes the original data to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream positioned at the start of a PP20 file.</param>
  /// <param name="output">Stream that receives the decompressed data.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the file is truncated.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var decompressed = DecompressCore(data);
    output.Write(decompressed);
  }

  /// <summary>
  /// Compresses raw data into the PP20 format and writes the result to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream containing the raw data to compress.</param>
  /// <param name="output">Stream that receives the PP20-encoded output.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var compressed = CompressCore(data);
    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses a PP20-crunched byte array and returns the original data.
  /// </summary>
  /// <param name="data">The complete PP20 file contents.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the file is truncated.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) => DecompressCore(data);

  /// <summary>
  /// Compresses raw data into the PP20 format and returns the result as a new byte array.
  /// </summary>
  /// <param name="data">The raw bytes to compress.</param>
  public static byte[] Compress(ReadOnlySpan<byte> data) => CompressCore(data);

  // ── Decompression core ────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < PowerPackerConstants.MinFileSize)
      throw new InvalidDataException("Input is shorter than the minimum PP20 file size.");

    // Validate magic
    if (!data[..PowerPackerConstants.MagicLength].SequenceEqual(PowerPackerConstants.Magic)) {
      if (data[..PowerPackerConstants.MagicLength].SequenceEqual(PowerPackerConstants.PX20Magic))
        throw new InvalidDataException("Encrypted PowerPacker (PX20) files are not supported.");

      throw new InvalidDataException("Invalid PowerPacker magic bytes.");
    }

    // Read efficiency table (4 bytes at offset 4)
    var efficiency = new int[PowerPackerConstants.OffsetClasses];
    for (var i = 0; i < PowerPackerConstants.OffsetClasses; ++i)
      efficiency[i] = data[PowerPackerConstants.EfficiencyTableOffset + i];

    // Read decrunch info from last 4 bytes
    var infoOffset = data.Length - PowerPackerConstants.DecrunchInfoSize;
    var originalSize =
      (data[infoOffset] << 16) |
      (data[infoOffset + 1] << 8) |
      data[infoOffset + 2];
    var skipBits = data[infoOffset + 3];

    // Packed data sits between offset 8 and (fileEnd - 4)
    var packedStart = PowerPackerConstants.MagicLength + PowerPackerConstants.EfficiencyTableSize;
    var packedEnd = data.Length - PowerPackerConstants.DecrunchInfoSize;
    var packed = data[packedStart..packedEnd].ToArray();

    // Set up reverse bit reader: start at the last byte, MSB first
    var output = new byte[originalSize];
    var outPos = originalSize;

    var bitState = new ReverseBitReader(packed);

    // Skip the initial padding bits
    if (skipBits > 0)
      bitState.ReadBits(skipBits);

    // Main decompression loop
    while (outPos > 0) {
      // First check for a match (1) or literal run (0)
      if (bitState.ReadBit() == 1) {
        // Match: read offset class (2 bits)
        var offsetClass = bitState.ReadBits(2);
        var offset = bitState.ReadBits(efficiency[offsetClass]);

        // Extended offset for class 3
        if (offsetClass == 3 && offset == (1 << efficiency[3]) - 1)
          offset += bitState.ReadBits(7);

        // Read length based on class
        int length;
        if (offsetClass < 3) {
          length = offsetClass + 2; // class 0→2, 1→3, 2→4
        } else {
          // Class 3: base length 5, read 3-bit extensions
          var extra = bitState.ReadBits(3);
          length = 5 + extra;
          while (extra == 7) {
            extra = bitState.ReadBits(3);
            length += extra;
          }
        }

        // Copy length bytes from (outPos + offset + 1) backward
        for (var i = 0; i < length && outPos > 0; ++i) {
          --outPos;
          var srcIndex = outPos + offset + 1;
          output[outPos] = srcIndex < originalSize ? output[srcIndex] : (byte)0;
        }
      }

      // After a match (or if bit was 0), read literal count
      if (outPos <= 0)
        break;

      var litCount = bitState.ReadBits(2);
      if (litCount == 0 && outPos > 0) {
        // Extended literal count: keep reading until we get a non-zero 2-bit value,
        // or use the PP20 extended scheme
        // PP20: if litCount == 0 after the first read, it means there are no
        // literals to copy. But only at end-of-stream; during normal flow there
        // are always 0-3 literals.
        // Actually in PP20: lit_count 0 means 0 literals except at stream start
        // (which is the logical end since we decompress backwards).
        // The standard behavior: 0 = no literals.
      }

      for (var i = 0; i < litCount && outPos > 0; ++i) {
        --outPos;
        output[outPos] = (byte)bitState.ReadBits(8);
      }
    }

    return output;
  }

  // ── Compression core ──────────────────────────────────────────────────────

  /// <summary>
  /// Represents a single token in the PP20 encoding: either a match or a literal run.
  /// </summary>
  private readonly struct Token {
    public readonly bool IsMatch;
    public readonly int OffsetClass;
    public readonly int Offset;
    public readonly int Length;
    public readonly byte[] Literals;

    private Token(int offsetClass, int offset, int length) {
      this.IsMatch = true;
      this.OffsetClass = offsetClass;
      this.Offset = offset;
      this.Length = length;
      this.Literals = [];
    }

    private Token(byte[] literals) {
      this.IsMatch = false;
      this.Literals = literals;
    }

    public static Token CreateMatch(int offsetClass, int offset, int length) => new(offsetClass, offset, length);
    public static Token CreateLiterals(byte[] literals) => new(literals);
  }

  private static byte[] CompressCore(ReadOnlySpan<byte> input) {
    if (input.Length == 0)
      return BuildPp20File([], [], 0);

    var efficiency = new int[] { 9, 10, 11, 12 };
    var maxDistances = new int[PowerPackerConstants.OffsetClasses];
    for (var i = 0; i < PowerPackerConstants.OffsetClasses; ++i)
      maxDistances[i] = (1 << efficiency[i]) - 1;

    // Class 3 has extended offsets: max = (1 << eff[3]) - 1 + 127
    var maxDistance = maxDistances[3] + 127;
    var windowSize = maxDistance + 1;

    // PP20 decompresses backward (output filled from end to start), so matches
    // reference data at higher indices. Reverse the input so that a forward LZ77
    // scan produces tokens in the correct decode order with valid match distances.
    var reversed = input.ToArray();
    Array.Reverse(reversed);
    ReadOnlySpan<byte> scanInput = reversed;

    // Use hash chain match finder for LZ77
    var matchFinder = new HashChainMatchFinder(windowSize);

    // Collect tokens forward through reversed input
    var tokens = new List<Token>();
    var pendingLiterals = new List<byte>();
    var pos = 0;

    while (pos < scanInput.Length) {
      // Find the best match
      var maxLen = Math.Min(scanInput.Length - pos, 256); // reasonable max
      var match = pos >= 2
        ? matchFinder.FindMatch(scanInput, pos, maxDistance, maxLen, 2)
        : default;

      if (match.Length >= 2) {
        // Flush any pending literals first
        if (pendingLiterals.Count > 0) {
          tokens.Add(Token.CreateLiterals(pendingLiterals.ToArray()));
          pendingLiterals.Clear();
        }

        // Determine offset class
        var dist = match.Distance;
        var offsetClass = -1;
        var encodedOffset = 0;

        for (var c = 0; c < PowerPackerConstants.OffsetClasses; ++c) {
          if (dist - 1 <= maxDistances[c]) {
            offsetClass = c;
            encodedOffset = dist - 1;
            break;
          }
        }

        if (offsetClass < 0) {
          // Should not happen given our maxDistance constraint, treat as literal
          pendingLiterals.Add(scanInput[pos]);
          matchFinder.InsertPosition(scanInput, pos);
          ++pos;
          continue;
        }

        // Check if the length is compatible with the offset class
        var baseLength = offsetClass + 2; // 2,3,4,5
        var matchLen = match.Length;

        // For classes 0-2, length is fixed at baseLength
        if (offsetClass < 3) {
          matchLen = Math.Min(matchLen, baseLength);
          // Need at least baseLength to use this class
          if (matchLen < baseLength) {
            // Try a lower class or emit literal
            var found = false;
            for (var c = offsetClass - 1; c >= 0; --c) {
              if (matchLen >= c + 2) {
                offsetClass = c;
                matchLen = c + 2;
                found = true;
                break;
              }
            }

            if (!found) {
              pendingLiterals.Add(scanInput[pos]);
              matchFinder.InsertPosition(scanInput, pos);
              ++pos;
              continue;
            }
          } else {
            matchLen = baseLength;
          }
        } else {
          // Class 3: length >= 5, encoded via 3-bit extensions
          if (matchLen < 5) {
            // Try a lower class
            var found = false;
            for (var c = 2; c >= 0; --c) {
              if (matchLen >= c + 2 && encodedOffset <= maxDistances[c]) {
                offsetClass = c;
                matchLen = c + 2;
                found = true;
                break;
              }
            }

            if (!found) {
              pendingLiterals.Add(scanInput[pos]);
              matchFinder.InsertPosition(scanInput, pos);
              ++pos;
              continue;
            }
          }
        }

        tokens.Add(Token.CreateMatch(offsetClass, encodedOffset, matchLen));

        // Insert skipped positions into hash chain
        for (var i = 1; i < matchLen; ++i)
          if (pos + i + 2 < scanInput.Length)
            matchFinder.InsertPosition(scanInput, pos + i);

        // Also insert the match start position (FindMatch already did this)
        pos += matchLen;
      } else {
        pendingLiterals.Add(scanInput[pos]);
        // Only insert manually for pos < 2 — FindMatch already inserted for pos >= 2
        if (pos < 2)
          matchFinder.InsertPosition(scanInput, pos);
        ++pos;
      }
    }

    // Flush remaining literals
    if (pendingLiterals.Count > 0)
      tokens.Add(Token.CreateLiterals(pendingLiterals.ToArray()));

    // Encode tokens into groups and emit as a reverse bit stream.
    // The decoder loop is: read 1 bit (match flag), optionally decode match,
    // then read literal count + literal bytes. We group tokens into
    // (optional_match, trailing_literals) pairs.
    //
    // The ReverseBitWriter packs entries[0] at the MSB of the last physical byte,
    // which is what the backward reader reads first. So entries must be added in
    // decode order: group[0] first (first decoded = last original bytes).
    var bitWriter = new ReverseBitWriter();
    var groups = BuildGroups(tokens, scanInput);

    // Write groups in forward order — group[0] is decoded first (fills end of output).
    for (var g = 0; g < groups.Count; ++g) {
      var (match, literals) = groups[g];

      // Match flag + data (decoder reads match flag first)
      if (match != null) {
        bitWriter.WriteBit(1);
        WriteMatchData(bitWriter, match.Value, efficiency);
      } else {
        bitWriter.WriteBit(0);
      }

      // Literal count + literal bytes
      WriteLiteralCount(bitWriter, literals.Length);
      WriteLiteralBytes(bitWriter, literals);
    }

    var packedBits = bitWriter.ToArray(out var skipBits);
    return BuildPp20File(efficiency, packedBits, input.Length, skipBits);
  }

  /// <summary>
  /// Groups tokens into (optional match, trailing literals) pairs for PP20 encoding.
  /// </summary>
  private static List<(Token? Match, byte[] Literals)> BuildGroups(List<Token> tokens, ReadOnlySpan<byte> input) {
    var groups = new List<(Token? Match, byte[] Literals)>();
    var i = 0;

    while (i < tokens.Count) {
      if (tokens[i].IsMatch) {
        var matchToken = tokens[i];
        ++i;

        // Collect following literals (in groups of up to 3, since lit count is 2 bits = 0-3)
        // Actually: if litCount is 0, no literals. Values 1-3 are direct.
        // For more than 3 literals, we need multiple groups with a "no match" + literals.
        var allLiterals = CollectLiterals(tokens, ref i);
        EmitLiteralGroups(groups, matchToken, allLiterals);
      } else {
        // Literal-only token
        var allLiterals = tokens[i].Literals;
        ++i;

        // Collect any additional consecutive literal tokens
        while (i < tokens.Count && !tokens[i].IsMatch) {
          var combined = new byte[allLiterals.Length + tokens[i].Literals.Length];
          allLiterals.CopyTo(combined, 0);
          tokens[i].Literals.CopyTo(combined, allLiterals.Length);
          allLiterals = combined;
          ++i;
        }

        EmitLiteralGroups(groups, null, allLiterals);
      }
    }

    // Ensure at least one group exists
    if (groups.Count == 0)
      groups.Add((null, []));

    return groups;
  }

  private static byte[] CollectLiterals(List<Token> tokens, ref int i) {
    var literals = new List<byte>();
    while (i < tokens.Count && !tokens[i].IsMatch) {
      literals.AddRange(tokens[i].Literals);
      ++i;
    }

    return literals.ToArray();
  }

  private static void EmitLiteralGroups(
    List<(Token? Match, byte[] Literals)> groups,
    Token? matchToken,
    byte[] allLiterals
  ) {
    if (allLiterals.Length <= 3) {
      groups.Add((matchToken, allLiterals));
    } else {
      // First group has the match + first 3 literals
      groups.Add((matchToken, allLiterals[..3]));

      // Remaining literals in groups of up to 3, each with no-match flag
      var remaining = allLiterals.AsSpan(3);
      while (remaining.Length > 0) {
        var chunk = Math.Min(remaining.Length, 3);
        groups.Add((null, remaining[..chunk].ToArray()));
        remaining = remaining[chunk..];
      }
    }
  }

  private static void WriteLiteralBytes(ReverseBitWriter writer, byte[] literals) {
    // Literals in decode order: first literal read → highest outPos.
    for (var i = 0; i < literals.Length; ++i)
      writer.WriteBits(literals[i], 8);
  }

  private static void WriteLiteralCount(ReverseBitWriter writer, int count) =>
    writer.WriteBits(count, 2);

  private static void WriteMatchData(ReverseBitWriter writer, Token match, int[] efficiency) {
    // Decoder reads: offset class (2 bits), offset, [ext offset], [length extensions].
    // Write in the same order.

    // Offset class (2 bits)
    writer.WriteBits(match.OffsetClass, 2);

    // Offset bits
    var maxOffset = (1 << efficiency[match.OffsetClass]) - 1;
    if (match.OffsetClass == 3 && match.Offset >= maxOffset) {
      writer.WriteBits(maxOffset, efficiency[3]);
      writer.WriteBits(match.Offset - maxOffset, 7);
    } else {
      writer.WriteBits(match.Offset, efficiency[match.OffsetClass]);
    }

    // Length (class 3 only — classes 0-2 have implicit length)
    if (match.OffsetClass >= 3) {
      var remaining = match.Length - 5;
      while (remaining >= 7) {
        writer.WriteBits(7, 3);
        remaining -= 7;
      }
      writer.WriteBits(remaining, 3);
    }
  }

  private static byte[] BuildPp20File(int[] efficiency, byte[] packedData, int originalSize, int skipBits = 0) {
    var totalSize = PowerPackerConstants.MagicLength
      + PowerPackerConstants.EfficiencyTableSize
      + packedData.Length
      + PowerPackerConstants.DecrunchInfoSize;

    var result = new byte[totalSize];
    var span = result.AsSpan();

    // Magic
    PowerPackerConstants.Magic.CopyTo(span);

    // Efficiency table
    if (efficiency.Length == 0) {
      PowerPackerConstants.DefaultEfficiency.CopyTo(span[4..]);
    } else {
      for (var i = 0; i < PowerPackerConstants.OffsetClasses; ++i)
        span[4 + i] = (byte)efficiency[i];
    }

    // Packed data
    packedData.AsSpan().CopyTo(span[(PowerPackerConstants.MagicLength + PowerPackerConstants.EfficiencyTableSize)..]);

    // Decrunch info (last 4 bytes): 24-bit original size (big-endian) + skip bits
    var infoOffset = totalSize - PowerPackerConstants.DecrunchInfoSize;
    span[infoOffset] = (byte)((originalSize >> 16) & 0xFF);
    span[infoOffset + 1] = (byte)((originalSize >> 8) & 0xFF);
    span[infoOffset + 2] = (byte)(originalSize & 0xFF);
    span[infoOffset + 3] = (byte)skipBits;

    return result;
  }

  // ── Reverse bit reader (for decompression) ────────────────────────────────

  /// <summary>
  /// Reads bits from a byte buffer starting at the last byte, MSB first,
  /// proceeding toward the first byte. This matches the PP20 bit ordering.
  /// </summary>
  private ref struct ReverseBitReader {
    private readonly byte[] _data;
    private int _bytePos;
    private uint _buffer;
    private int _bitsInBuffer;

    public ReverseBitReader(byte[] data) {
      this._data = data;
      this._bytePos = data.Length - 1;
      this._buffer = 0;
      this._bitsInBuffer = 0;

      // Pre-fill the buffer with up to 4 bytes from the end
      this.FillBuffer();
    }

    private void FillBuffer() {
      while (this._bitsInBuffer <= 24 && this._bytePos >= 0) {
        this._buffer |= (uint)this._data[this._bytePos] << (24 - this._bitsInBuffer);
        this._bitsInBuffer += 8;
        --this._bytePos;
      }
    }

    public int ReadBit() => this.ReadBits(1);

    public int ReadBits(int count) {
      if (count == 0)
        return 0;

      this.FillBuffer();

      // Extract top 'count' bits from the buffer
      var result = (int)(this._buffer >> (32 - count));
      this._buffer <<= count;
      this._bitsInBuffer -= count;

      return result;
    }
  }

  // ── Reverse bit writer (for compression) ──────────────────────────────────

  /// <summary>
  /// Accumulates bits that will form the PP20 packed data.
  /// Bits are collected in logical decode order (first decoded = first written),
  /// then reversed to produce the physical byte stream where the last byte
  /// is read first during decompression.
  /// </summary>
  private sealed class ReverseBitWriter {
    private readonly List<(int Value, int Bits)> _entries = [];

    public void WriteBit(int value) => this.WriteBits(value, 1);

    public void WriteBits(int value, int bits) {
      if (bits > 0)
        this._entries.Add((value & ((1 << bits) - 1), bits));
    }

    /// <summary>
    /// Converts the accumulated bit stream into a byte array suitable for PP20.
    /// The decoder reads from the last byte toward the first, MSB first.
    /// </summary>
    /// <param name="skipBits">
    /// Output: the number of padding bits at the end of the last physical byte
    /// that the decoder must skip before reading real data.
    /// </param>
    public byte[] ToArray(out int skipBits) {
      // The entries are in logical decode order: entry[0] is what the decoder
      // reads first. The decoder reads from the END of the byte array, MSB first.
      // So we pack entries[0] at the MSB of the last byte, proceeding toward
      // the first byte.

      // First, calculate total bits
      var totalBits = 0;
      foreach (var (_, bits) in this._entries)
        totalBits += bits;

      if (totalBits == 0) {
        skipBits = 0;
        return [];
      }

      // Pad to a full byte count
      var totalBytes = (totalBits + 7) / 8;
      skipBits = totalBytes * 8 - totalBits;

      var result = new byte[totalBytes];

      // Pack bits: start from the end of the byte array, MSB first.
      // The decoder reads byte[last] MSB first and skips `skipBits` padding bits
      // before reaching real data. So leave skipBits zeros at byte[last] MSB.
      var byteIdx = totalBytes - 1;
      var bitIdx = 7 - skipBits; // start after padding bits

      foreach (var (value, bits) in this._entries) {
        for (var i = bits - 1; i >= 0; --i) {
          if (((value >> i) & 1) == 1)
            result[byteIdx] |= (byte)(1 << bitIdx);

          --bitIdx;
          if (bitIdx < 0) {
            bitIdx = 7;
            --byteIdx;
          }
        }
      }

      return result;
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
