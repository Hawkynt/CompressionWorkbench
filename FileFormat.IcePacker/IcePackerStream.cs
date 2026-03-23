using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.IcePacker;

/// <summary>
/// Compressor and decompressor for the Atari ST ICE Packer format (Axe of Delight, 1989).
/// ICE is a stream format popular in the Atari demo scene that uses backward LZ77 with
/// variable-length match encoding. Bits are read from end to start during decompression,
/// and the output buffer is filled from end to start.
/// </summary>
public static class IcePackerStream {
  // ── Public API ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Decompresses ICE-packed data from <paramref name="input"/> and writes the
  /// original data to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream positioned at the start of the ICE file.</param>
  /// <param name="output">Stream that receives the decompressed data.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the data is truncated.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var result = DecompressCore(data);
    output.Write(result);
  }

  /// <summary>
  /// Compresses raw data from <paramref name="input"/> in ICE Packer format and
  /// writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream containing the raw data to compress.</param>
  /// <param name="output">Stream that receives the ICE-packed output.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var result = CompressCore(data);
    output.Write(result);
  }

  /// <summary>
  /// Decompresses ICE-packed data from a byte span.
  /// </summary>
  /// <param name="data">The complete ICE file contents.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) => DecompressCore(data);

  /// <summary>
  /// Compresses raw data into ICE Packer format.
  /// </summary>
  /// <param name="data">The raw bytes to compress.</param>
  /// <returns>The ICE-packed data including header.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) => CompressCore(data);

  // ── Decompression ──────────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < IcePackerConstants.HeaderSize)
      throw new InvalidDataException("Input is shorter than the ICE header.");

    // Read and validate magic.
    var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
    if (magic != IcePackerConstants.Magic1 && magic != IcePackerConstants.Magic2)
      throw new InvalidDataException(
        $"Invalid ICE magic: 0x{magic:X8}. Expected 'Ice!' or 'ICE!'.");

    var packedSize = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var originalSize = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);

    if (data.Length < IcePackerConstants.HeaderSize + (int)packedSize)
      throw new InvalidDataException(
        $"Packed data truncated: expected {packedSize} bytes but only {data.Length - IcePackerConstants.HeaderSize} available.");

    var packed = data.Slice(IcePackerConstants.HeaderSize, (int)packedSize);

    var output = new byte[originalSize];
    var dstPos = (int)originalSize;

    // Backward bit reader state.
    var reader = new BackwardBitReader(packed);

    while (dstPos > 0) {
      if (reader.ReadBit() == 1) {
        // Match
        var length = DecodeMatchLength(ref reader);
        var offset = DecodeMatchOffset(ref reader, length);

        // Copy from already-decompressed data (ahead in output buffer).
        for (var i = 0; i < length && dstPos > 0; i++) {
          dstPos--;
          output[dstPos] = output[dstPos + offset];
        }
      } else {
        // Literal byte
        dstPos--;
        output[dstPos] = (byte)reader.ReadBits(8);
      }
    }

    return output;
  }

  /// <summary>Decodes match length from the backward bit stream.</summary>
  private static int DecodeMatchLength(ref BackwardBitReader reader) {
    if (reader.ReadBit() == 0) {
      // 0b: length = 2 or 3
      return reader.ReadBit() + 2;
    }

    if (reader.ReadBit() == 0) {
      // 10b: length = 4..11 (3-bit value + 4)
      return reader.ReadBits(3) + 4;
    }

    // 11b: length = 12..267 (8-bit value + 12)
    return reader.ReadBits(8) + 12;
  }

  /// <summary>Decodes match offset based on match length.</summary>
  private static int DecodeMatchOffset(ref BackwardBitReader reader, int length) {
    if (length == 2)
      return reader.ReadBits(9) + 1;   // 1..512
    if (length <= 3)
      return reader.ReadBits(10) + 1;  // 1..1024
    return reader.ReadBits(12) + 1;    // 1..4096
  }

  // ── Compression ────────────────────────────────────────────────────────────

  /// <summary>
  /// Token produced during forward LZ77 scanning, to be encoded in reverse.
  /// </summary>
  private readonly struct Token {
    public bool IsMatch { get; }
    public byte Literal { get; }
    public int Length { get; }
    public int Offset { get; }

    private Token(bool isMatch, byte literal, int length, int offset) {
      IsMatch = isMatch;
      Literal = literal;
      Length = length;
      Offset = offset;
    }

    public static Token CreateLiteral(byte value) => new(false, value, 0, 0);
    public static Token CreateMatch(int length, int offset) => new(true, 0, length, offset);
  }

  private static byte[] CompressCore(ReadOnlySpan<byte> input) {
    if (input.Length == 0) {
      var empty = new byte[IcePackerConstants.HeaderSize];
      BinaryPrimitives.WriteUInt32BigEndian(empty, IcePackerConstants.Magic1);
      return empty;
    }

    // ICE decompresses backward (output filled from end to start), so matches
    // reference data at higher indices. To use a forward LZ77 match finder, we
    // reverse the input first. Tokens from the reversed scan are in decode order.
    var reversed = input.ToArray();
    Array.Reverse(reversed);
    var tokens = FindTokens(reversed);

    // Phase 2: Encode tokens using a backward bit writer.
    // The decompressor reads bits from the END of packed data toward the START,
    // MSB first within each byte. So we build the bitstream such that the LAST
    // byte's MSB is the first bit the decompressor reads.
    //
    // We process tokens in forward order (first token → first bits read by decompressor).
    // We collect bits in read-order, then pack them into bytes from end to start.
    var bits = new List<int>(); // bits in decompressor read order

    foreach (var token in tokens) {
      if (token.IsMatch) {
        bits.Add(1); // match flag
        EncodeLength(bits, token.Length);
        EncodeBitsMsb(bits, token.Offset - 1, OffsetBitCount(token.Length));
      } else {
        bits.Add(0); // literal flag
        EncodeBitsMsb(bits, token.Literal, 8);
      }
    }

    // Pack bits into bytes, filling from end. The decompressor reads from the
    // last byte backward, MSB first. So bits[0] goes to the MSB of the last byte.
    var byteCount = (bits.Count + 7) / 8;
    var packed = new byte[byteCount];
    var padBits = byteCount * 8 - bits.Count; // unused bits at the start of byte 0

    for (var i = 0; i < bits.Count; i++) {
      if (bits[i] != 0) {
        // Map bit i (in read order) to its byte/bit position.
        // Bit 0 → last byte MSB, bit 1 → last byte bit 6, etc.
        var globalBitPos = padBits + i; // position from start of packed (padded)
        // We want: decompressor reads from end, MSB first, so bit 0 in read-order
        // is the MSB of the last byte. Reverse the byte order:
        var reversedPos = (byteCount * 8 - 1) - globalBitPos + padBits;
        // Actually simpler: bit i in read-order = position counting from end.
        var fromEnd = i; // 0-based position reading from end
        var byteFromEnd = fromEnd / 8;
        var bitInByte = 7 - (fromEnd % 8); // MSB first
        var byteIndex = byteCount - 1 - byteFromEnd;
        if (byteIndex >= 0)
          packed[byteIndex] |= (byte)(1 << bitInByte);
      }
    }

    // Build final output.
    var result = new byte[IcePackerConstants.HeaderSize + packed.Length];
    BinaryPrimitives.WriteUInt32BigEndian(result, IcePackerConstants.Magic1);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)packed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)input.Length);
    packed.AsSpan().CopyTo(result.AsSpan(IcePackerConstants.HeaderSize));

    return result;
  }

  /// <summary>
  /// Scans the input forward using a hash chain match finder and produces a list of tokens.
  /// </summary>
  private static List<Token> FindTokens(ReadOnlySpan<byte> input) {
    var tokens = new List<Token>();

    // Use HashChainMatchFinder for efficient match finding.
    // ICE max offset is 4096 and max length is 267.
    var matchFinder = new HashChainMatchFinder(
      windowSize: IcePackerConstants.MaxOffsetLong,
      maxChainDepth: 64);

    var pos = 0;
    while (pos < input.Length) {
      // Determine the max offset allowed based on minimum possible match length.
      var best = matchFinder.FindMatch(
        input, pos,
        maxDistance: IcePackerConstants.MaxOffsetLong,
        maxLength: IcePackerConstants.MaxMatchLength,
        minLength: IcePackerConstants.MinMatchLength);

      // Validate the match fits ICE offset constraints for its length.
      if (best.Length >= IcePackerConstants.MinMatchLength &&
          IsValidIceMatch(best.Length, best.Distance)) {
        tokens.Add(Token.CreateMatch(best.Length, best.Distance));
        // Insert skipped positions into the hash chain.
        for (var i = 1; i < best.Length; i++)
          matchFinder.InsertPosition(input, pos + i);
        pos += best.Length;
      } else {
        tokens.Add(Token.CreateLiteral(input[pos]));
        pos++;
      }
    }

    return tokens;
  }

  /// <summary>
  /// Checks whether a match length/offset pair fits the ICE encoding constraints.
  /// </summary>
  private static bool IsValidIceMatch(int length, int offset) {
    if (length < IcePackerConstants.MinMatchLength || offset < 1)
      return false;

    return length switch {
      2 => offset <= IcePackerConstants.MaxOffset2,
      3 => offset <= IcePackerConstants.MaxOffset3,
      _ => offset <= IcePackerConstants.MaxOffsetLong
    };
  }

  /// <summary>Returns the number of offset bits for the given match length.</summary>
  private static int OffsetBitCount(int length) =>
    length switch {
      2 => 9,
      3 => 10,
      _ => 12
    };

  /// <summary>
  /// Encodes <paramref name="value"/> as <paramref name="numBits"/> bits into the list, MSB first.
  /// </summary>
  private static void EncodeBitsMsb(List<int> bits, int value, int numBits) {
    for (var i = numBits - 1; i >= 0; i--)
      bits.Add((value >> i) & 1);
  }

  /// <summary>
  /// Encodes the match length into the bit list (decompressor read order).
  /// </summary>
  private static void EncodeLength(List<int> bits, int length) {
    if (length <= 3) {
      // Decompressor: ReadBit()==0, then ReadBit() → length-2
      bits.Add(0); // first flag: not extended
      bits.Add(length - 2);
    } else if (length <= 11) {
      // Decompressor: ReadBit()==1, ReadBit()==0, then ReadBits(3) → length-4
      bits.Add(1);
      bits.Add(0);
      EncodeBitsMsb(bits, length - 4, 3);
    } else {
      // Decompressor: ReadBit()==1, ReadBit()==1, then ReadBits(8) → length-12
      bits.Add(1);
      bits.Add(1);
      EncodeBitsMsb(bits, length - 12, 8);
    }
  }

  // ── Backward bit reader ────────────────────────────────────────────────────

  /// <summary>
  /// Reads bits from packed data starting at the end and working backward.
  /// Bits within each byte are read MSB first.
  /// </summary>
  private ref struct BackwardBitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private int _bitBuffer;
    private int _bitsRemaining;

    public BackwardBitReader(ReadOnlySpan<byte> data) {
      _data = data;
      _bytePos = data.Length - 1;
      _bitBuffer = 0;
      _bitsRemaining = 0;
    }

    /// <summary>Reads a single bit from the stream.</summary>
    public int ReadBit() {
      if (_bitsRemaining == 0) {
        if (_bytePos < 0)
          return 0; // past the start, return zero padding
        _bitBuffer = _data[_bytePos--];
        _bitsRemaining = 8;
      }

      var bit = (_bitBuffer >> 7) & 1;
      _bitBuffer <<= 1;
      _bitsRemaining--;
      return bit;
    }

    /// <summary>Reads <paramref name="count"/> bits, MSB first.</summary>
    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; i++)
        value = (value << 1) | ReadBit();
      return value;
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
