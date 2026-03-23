using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.ApLib;

/// <summary>
/// Provides static methods for compressing and decompressing data using the aPLib algorithm
/// with an AP32 framed container format.
/// </summary>
/// <remarks>
/// aPLib is an LZ-based compression library that uses a bitstream read LSB-first.
/// The AP32 container adds a 24-byte little-endian header with magic, flags, sizes,
/// CRC-32 checksum, and a reserved field.
/// </remarks>
public static class ApLibStream {

  /// <summary>Magic bytes: <c>AP32</c> (0x41503332).</summary>
  private const uint Magic = 0x32335041u; // 'A','P','3','2' as LE uint

  private const int HeaderSize = 24;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxWindowSize = 0x7D000; // ~512 KB window

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes an AP32-format stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the compressed AP32 data is written.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var src = ms.ToArray();

    var originalCrc = Crc32.Compute(src);
    var compressed = CompressBlock(src);

    // Write the 24-byte header (all little-endian).
    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);           // magic AP32
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0);          // flags
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)compressed.Length);  // compressed size
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)src.Length);        // uncompressed size
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..], originalCrc);             // CRC32
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);         // reserved
    output.Write(header);

    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses an AP32-format stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing AP32-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the CRC check fails.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (magic != Magic)
      throw new InvalidDataException($"Invalid aPLib magic: 0x{magic:X8}, expected 0x{Magic:X8}.");

    var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);

    var compressed = new byte[compressedSize];
    input.ReadExactly(compressed);

    var decompressed = DecompressBlock(compressed, (int)uncompressedSize);

    var actualCrc = Crc32.Compute(decompressed);
    if (actualCrc != expectedCrc)
      throw new InvalidDataException($"Decompressed data CRC mismatch: 0x{actualCrc:X8} != 0x{expectedCrc:X8}.");

    output.Write(decompressed);
  }

  #region Decompression

  private static byte[] DecompressBlock(byte[] compressed, int uncompressedSize) {
    if (uncompressedSize == 0)
      return [];

    var br = new BitReader(compressed);
    var dst = new byte[uncompressedSize];
    var pos = 0;
    var lastOffset = 0;

    // First byte is always a literal.
    dst[pos++] = br.ReadByte();

    while (pos < uncompressedSize) {
      if (br.ReadBit() == 1) {
        // Bit = 1: literal byte.
        dst[pos++] = br.ReadByte();
      } else if (br.ReadBit() == 1) {
        if (br.ReadBit() == 1) {
          // 011: Long match.
          var highBits = ReadGamma(br);
          int offset;
          if (highBits == 2) {
            offset = br.ReadByte();
            if (offset == 0)
              break; // end-of-stream marker
          } else {
            offset = ((highBits - 2) << 8) | br.ReadByte();
          }

          var length = ReadGamma(br);
          if (offset < 128)
            length += 2;
          else if (offset < 1280)
            length += 1;
          else if (offset >= 32000)
            length -= 1;
          // else length unchanged

          if (length < 1)
            length = 1;

          lastOffset = offset;
          for (var i = 0; i < length && pos < uncompressedSize; i++) {
            dst[pos] = dst[pos - offset - 1];
            pos++;
          }
        } else {
          // 010: Rep-match (reuse last offset).
          var length = ReadGamma(br);
          for (var i = 0; i < length + 2 && pos < uncompressedSize; i++) {
            dst[pos] = dst[pos - lastOffset - 1];
            pos++;
          }
        }
      } else {
        // 00: Short match -- 2 bits encode offset (0..3).
        var code = (br.ReadBit() << 1) | br.ReadBit();
        if (code == 0) {
          dst[pos++] = 0;
        } else {
          dst[pos] = dst[pos - code];
          pos++;
        }
      }
    }

    return dst;
  }

  /// <summary>
  /// Reads an Elias gamma coded value from the aPLib bitstream.
  /// The aPLib gamma coding reads pairs of (data_bit, continue_bit).
  /// As long as continue_bit is 0, it continues. Minimum returned value is 2.
  /// </summary>
  private static int ReadGamma(BitReader br) {
    var result = 1;
    do {
      result = (result << 1) + br.ReadBit();
    } while (br.ReadBit() == 0);
    return result; // minimum value is 2
  }

  #endregion

  #region Compression

  /// <summary>
  /// Returns the minimum match length for a long match at the given 0-based offset.
  /// The gamma code has a minimum value of 2, so the encoded length must be at least 2.
  /// After adjustment: encoded = length - adjustment, and we need encoded >= 2.
  /// </summary>
  private static int MinLongMatchLength(int offset) {
    if (offset < 128)
      return 4;  // gamma(2) + 2 = 4
    if (offset < 1280)
      return 3;  // gamma(2) + 1 = 3
    if (offset < 32000)
      return 2;  // gamma(2) + 0 = 2
    return 3;    // gamma(2) - 1 = 1 is too short; gamma(3) - 1 = 2
  }

  private static byte[] CompressBlock(byte[] src) {
    if (src.Length == 0)
      return [];

    var writer = new BitWriter();
    var hashTable = new int[HashSize];
    Array.Fill(hashTable, -1);
    var chain = new int[src.Length];
    Array.Fill(chain, -1);

    var lastOffset = 0;

    // First byte is always a literal (no bit prefix).
    writer.WriteByte(src[0]);
    if (src.Length >= 3)
      InsertHash(hashTable, chain, src, 0);

    var pos = 1;
    while (pos < src.Length) {
      var bestLen = 0;
      var bestOff = 0;

      if (pos + 1 < src.Length)
        FindMatch(src, pos, hashTable, chain, out bestLen, out bestOff);

      // Convert to 0-based offset for aPLib encoding and check minimum length.
      var offset0 = bestOff > 0 ? bestOff - 1 : 0;
      var minLen = bestOff > 0 ? MinLongMatchLength(offset0) : int.MaxValue;

      // Check if rep-match is useful. Rep-match gamma encodes (length - 2), minimum gamma = 2,
      // so minimum rep-match length = 2 + 2 = 4.
      var repLen = 0;
      if (lastOffset > 0 && pos < src.Length) {
        var srcOff = pos - lastOffset - 1;
        if (srcOff >= 0) {
          while (pos + repLen < src.Length && src[pos + repLen] == src[srcOff + repLen])
            repLen++;
        }
      }

      // Choose best encoding.
      if (repLen >= 4 && repLen >= bestLen) {
        EmitRepMatch(writer, repLen);
        for (var i = 0; i < repLen && pos + i + 2 < src.Length; i++)
          InsertHash(hashTable, chain, src, pos + i);
        pos += repLen;
      } else if (bestLen >= minLen) {
        EmitLongMatch(writer, offset0, bestLen);
        lastOffset = offset0;
        for (var i = 0; i < bestLen && pos + i + 2 < src.Length; i++)
          InsertHash(hashTable, chain, src, pos + i);
        pos += bestLen;
      } else {
        // Check for short match: 1-byte copy from offset 1..3.
        var shortCode = 0;
        if (pos >= 1 && src[pos] == src[pos - 1]) shortCode = 1;
        else if (pos >= 2 && src[pos] == src[pos - 2]) shortCode = 2;
        else if (pos >= 3 && src[pos] == src[pos - 3]) shortCode = 3;

        if (shortCode > 0) {
          EmitShortMatch(writer, shortCode);
        } else if (src[pos] == 0) {
          EmitShortMatch(writer, 0);
        } else {
          EmitLiteral(writer, src[pos]);
        }

        if (pos + 2 < src.Length)
          InsertHash(hashTable, chain, src, pos);
        pos++;
      }
    }

    // Emit end-of-stream marker: 011 + gamma(2) + byte(0).
    writer.WriteBit(0); // not literal
    writer.WriteBit(1); // not short
    writer.WriteBit(1); // long match (not rep)
    WriteGamma(writer, 2); // highBits = 2 means offset = next byte
    writer.WriteByte(0);   // offset byte = 0 -> end of stream

    return writer.ToArray();
  }

  private static void EmitLiteral(BitWriter writer, byte value) {
    writer.WriteBit(1); // literal flag
    writer.WriteByte(value);
  }

  private static void EmitShortMatch(BitWriter writer, int code) {
    writer.WriteBit(0); // not literal
    writer.WriteBit(0); // short match
    writer.WriteBit((code >> 1) & 1);
    writer.WriteBit(code & 1);
  }

  private static void EmitRepMatch(BitWriter writer, int length) {
    writer.WriteBit(0); // not literal
    writer.WriteBit(1); // not short
    writer.WriteBit(0); // rep-match (not long)
    WriteGamma(writer, length - 2); // length - 2 >= 2 since length >= 4
  }

  private static void EmitLongMatch(BitWriter writer, int offset, int length) {
    writer.WriteBit(0); // not literal
    writer.WriteBit(1); // not short
    writer.WriteBit(1); // long match

    // Encode offset.
    int highBits;
    if (offset <= 255) {
      highBits = 2;
    } else {
      highBits = (offset >> 8) + 2;
    }
    WriteGamma(writer, highBits);
    writer.WriteByte((byte)(offset & 0xFF));

    // Adjust length for encoding. Result must be >= 2 (gamma minimum).
    var encodedLength = length;
    if (offset < 128)
      encodedLength -= 2;
    else if (offset < 1280)
      encodedLength -= 1;
    else if (offset >= 32000)
      encodedLength += 1;

    WriteGamma(writer, encodedLength);
  }

  /// <summary>
  /// Writes an aPLib gamma code. The format writes pairs of (data_bit, continue_bit).
  /// Continue_bit = 0 means keep reading, 1 means stop. Minimum value is 2.
  /// </summary>
  private static void WriteGamma(BitWriter writer, int value) {
    // Collect bits to write in reverse order.
    // Gamma decoding: result=1; do { result = (result<<1)+dataBit; } while (continueBit==0);
    // We need to produce the bit pairs that decode to 'value'.
    // Build the chain of data bits from MSB to LSB of value (excluding the leading 1).
    var bits = new List<int>();
    var tmp = value;
    while (tmp > 1) {
      bits.Add(tmp & 1);
      tmp >>= 1;
    }

    // Write in reverse order (MSB first). All but the last pair have continue=0.
    for (var i = bits.Count - 1; i >= 0; i--) {
      writer.WriteBit(bits[i]);
      writer.WriteBit(i == 0 ? 1 : 0); // continue=0 except for the last pair
    }
  }

  private static void FindMatch(byte[] src, int pos, int[] hashTable, int[] chain, out int bestLen, out int bestOff) {
    bestLen = 0;
    bestOff = 0;

    if (pos + 2 >= src.Length)
      return;

    var h = Hash3(src, pos);
    var candidate = hashTable[h];
    var maxLen = Math.Min(src.Length - pos, 0xFFFF);
    var minPos = Math.Max(0, pos - MaxWindowSize);
    var attempts = 128;

    while (candidate >= minPos && attempts-- > 0) {
      if (src[candidate + bestLen] == src[pos + bestLen]) {
        var len = 0;
        var limit = Math.Min(maxLen, pos - candidate);
        limit = Math.Min(limit, maxLen);
        while (len < limit && src[candidate + len] == src[pos + len])
          len++;

        if (len > bestLen) {
          bestLen = len;
          bestOff = pos - candidate; // 1-based distance
          if (len == maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }
  }

  private static int Hash3(byte[] data, int pos) =>
    ((data[pos] << 8 | data[pos + 1]) * 0x9E37 + data[pos + 2]) & (HashSize - 1);

  private static void InsertHash(int[] hashTable, int[] chain, byte[] data, int pos) {
    var h = Hash3(data, pos);
    chain[pos] = hashTable[h];
    hashTable[h] = pos;
  }

  #endregion

  #region Bit I/O

  /// <summary>
  /// LSB-first bit reader for aPLib bitstream. Tag bytes and data bytes are
  /// interleaved in the same stream. <see cref="ReadBit"/> consumes bits from the
  /// current tag byte (LSB first); when the tag is exhausted the next byte in
  /// the stream becomes the new tag. <see cref="ReadByte"/> reads the next byte
  /// from the stream as a raw data byte.
  /// </summary>
  private sealed class BitReader(byte[] data) {
    private int _pos;
    private int _tag;
    private int _bitsLeft; // bits remaining in current tag byte

    /// <summary>Reads a single bit from the tag byte, LSB-first.</summary>
    public int ReadBit() {
      if (_bitsLeft == 0) {
        if (_pos >= data.Length)
          throw new InvalidDataException("Unexpected end of aPLib compressed data.");
        _tag = data[_pos++];
        _bitsLeft = 8;
      }

      var bit = _tag & 1;
      _tag >>= 1;
      _bitsLeft--;
      return bit;
    }

    /// <summary>Reads the next raw byte from the stream (not from the tag).</summary>
    public byte ReadByte() {
      if (_pos >= data.Length)
        throw new InvalidDataException("Unexpected end of aPLib compressed data.");
      return data[_pos++];
    }
  }

  /// <summary>
  /// LSB-first bit writer for aPLib bitstream. Tag bytes and data bytes are
  /// interleaved in the output. <see cref="WriteBit"/> accumulates bits into a
  /// tag byte (LSB first); when 8 bits are accumulated the tag is flushed to
  /// a previously reserved position. <see cref="WriteByte"/> writes a raw data
  /// byte to the output stream.
  /// </summary>
  private sealed class BitWriter {
    private readonly MemoryStream _buffer = new();
    private int _tag;
    private int _bitsUsed;
    private long _tagPos = -1;

    /// <summary>Writes a single bit, LSB-first.</summary>
    public void WriteBit(int bit) {
      if (_bitsUsed == 0) {
        // Reserve a position for the new tag byte.
        _tagPos = _buffer.Position;
        _buffer.WriteByte(0); // placeholder
        _tag = 0;
      }

      _tag |= (bit & 1) << _bitsUsed;
      _bitsUsed++;

      if (_bitsUsed == 8)
        FlushTag();
    }

    /// <summary>Writes a raw data byte to the output stream.</summary>
    public void WriteByte(byte b) {
      _buffer.WriteByte(b);
    }

    /// <summary>Finalizes any partial tag byte and returns the buffer contents.</summary>
    public byte[] ToArray() {
      if (_bitsUsed > 0)
        FlushTag();
      return _buffer.ToArray();
    }

    private void FlushTag() {
      var currentPos = _buffer.Position;
      _buffer.Position = _tagPos;
      _buffer.WriteByte((byte)_tag);
      _buffer.Position = currentPos;
      _tag = 0;
      _bitsUsed = 0;
      _tagPos = -1;
    }
  }

  #endregion
}
