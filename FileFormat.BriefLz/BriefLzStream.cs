using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.BriefLz;

/// <summary>
/// Provides static methods for compressing and decompressing data using the BriefLZ algorithm
/// with a blzpack container format.
/// </summary>
/// <remarks>
/// BriefLZ is an LZ77 variant that encodes match lengths and offsets using Elias gamma codes.
/// The bitstream is written MSB-first. The blzpack container adds a 24-byte big-endian header
/// with magic, version, sizes, and CRC-32 checksums.
/// </remarks>
public static class BriefLzStream {

  private const uint Magic = 0x626C7A1Au; // 'blz\x1A'
  private const int HeaderSize = 24;
  private const int MaxWindowSize = 65536;
  private const int MinMatchLength = 2;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes a blzpack-format stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the compressed blzpack data is written.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input into a buffer.
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var src = ms.ToArray();

    // Compute CRC of original data.
    var originalCrc = Crc32.Compute(src);

    // Compress the data.
    var compressed = CompressBlock(src);

    // Compute CRC of compressed payload.
    var compressedCrc = Crc32.Compute(compressed);

    // Write the 24-byte header (all big-endian).
    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], 1); // version
    BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)compressed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..], compressedCrc);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)src.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[20..], originalCrc);
    output.Write(header);

    // Write compressed payload.
    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses a blzpack-format stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing blzpack-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid, the version is unsupported, or a CRC check fails.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read header.
    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);

    var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
    if (magic != Magic)
      throw new InvalidDataException($"Invalid BriefLZ magic: 0x{magic:X8}, expected 0x{Magic:X8}.");

    var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
    if (version != 1)
      throw new InvalidDataException($"Unsupported BriefLZ version: {version}.");

    var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
    var expectedCompressedCrc = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
    var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
    var expectedOriginalCrc = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);

    // Read compressed payload.
    var compressed = new byte[compressedSize];
    input.ReadExactly(compressed);

    // Verify compressed CRC.
    var actualCompressedCrc = Crc32.Compute(compressed);
    if (actualCompressedCrc != expectedCompressedCrc)
      throw new InvalidDataException($"Compressed data CRC mismatch: 0x{actualCompressedCrc:X8} != 0x{expectedCompressedCrc:X8}.");

    // Decompress.
    var decompressed = DecompressBlock(compressed, (int)uncompressedSize);

    // Verify original CRC.
    var actualOriginalCrc = Crc32.Compute(decompressed);
    if (actualOriginalCrc != expectedOriginalCrc)
      throw new InvalidDataException($"Decompressed data CRC mismatch: 0x{actualOriginalCrc:X8} != 0x{expectedOriginalCrc:X8}.");

    output.Write(decompressed);
  }

  private static byte[] CompressBlock(byte[] src) {
    if (src.Length == 0)
      return [];

    var writer = new BitWriter();

    // Hash table: maps 3-byte hash to most recent position.
    var hashTable = new int[HashSize];
    Array.Fill(hashTable, -1);

    // Chain table for finding longer matches.
    var chain = new int[src.Length];
    Array.Fill(chain, -1);

    // First literal is always emitted directly (no bit prefix).
    writer.WriteByte(src[0]);

    if (src.Length >= 3)
      InsertHash(hashTable, chain, src, 0);

    var pos = 1;
    while (pos < src.Length) {
      var bestLen = 0;
      var bestOff = 0;

      if (pos + MinMatchLength <= src.Length) {
        // Find match.
        FindMatch(src, pos, hashTable, chain, out bestLen, out bestOff);
      }

      if (bestLen >= MinMatchLength) {
        // Emit match: bit 1, then gamma(length - 2 + 1) = gamma(length - 1), then gamma(offset).
        writer.WriteBit(1);
        WriteGamma(writer, bestLen - 2 + 1); // length - 2, stored as value >= 1
        WriteGamma(writer, bestOff); // offset, already >= 1

        // Insert all positions covered by the match into the hash table.
        for (var i = 0; i < bestLen && pos + i + 2 < src.Length; i++)
          InsertHash(hashTable, chain, src, pos + i);

        pos += bestLen;
      } else {
        // Emit literal: bit 0, then 8 bits of the byte.
        writer.WriteBit(0);
        writer.WriteByte(src[pos]);

        if (pos + 2 < src.Length)
          InsertHash(hashTable, chain, src, pos);

        pos++;
      }
    }

    return writer.ToArray();
  }

  private static void FindMatch(byte[] src, int pos, int[] hashTable, int[] chain, out int bestLen, out int bestOff) {
    bestLen = 0;
    bestOff = 0;

    if (pos + 2 >= src.Length)
      return;

    var h = Hash3(src, pos);
    var candidate = hashTable[h];
    var maxLen = Math.Min(src.Length - pos, 256); // cap match length
    var minPos = Math.Max(0, pos - MaxWindowSize);
    var attempts = 64; // limit chain traversal

    while (candidate >= minPos && attempts-- > 0) {
      if (src[candidate + bestLen] == src[pos + bestLen]) {
        var len = 0;
        var limit = Math.Min(maxLen, pos - candidate > MaxWindowSize ? 0 : maxLen);
        while (len < limit && src[candidate + len] == src[pos + len])
          len++;

        if (len >= MinMatchLength && len > bestLen) {
          bestLen = len;
          bestOff = pos - candidate;
          if (len == maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate) // prevent infinite loops
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

  /// <summary>
  /// Writes an Elias gamma code for value v (v >= 1) MSB-first.
  /// Encoding: floor(log2(v)) zero bits, then v in binary (floor(log2(v))+1 bits).
  /// </summary>
  private static void WriteGamma(BitWriter writer, int v) {
    // Determine number of bits needed (floor(log2(v)) + 1).
    var bits = 0;
    var tmp = v;
    while (tmp > 1) {
      bits++;
      tmp >>= 1;
    }

    // Write 'bits' zero bits.
    for (var i = 0; i < bits; i++)
      writer.WriteBit(0);

    // Write v in binary, MSB-first, using bits+1 bits.
    for (var i = bits; i >= 0; i--)
      writer.WriteBit((v >> i) & 1);
  }

  private static byte[] DecompressBlock(byte[] compressed, int uncompressedSize) {
    if (uncompressedSize == 0)
      return [];

    var reader = new BitReader(compressed);
    var dst = new byte[uncompressedSize];
    var pos = 0;

    // First byte is always a literal (no bit prefix).
    dst[pos++] = reader.ReadByte();

    while (pos < uncompressedSize) {
      var bit = reader.ReadBit();
      if (bit == 0) {
        // Literal.
        dst[pos++] = reader.ReadByte();
      } else {
        // Match.
        var lengthCode = ReadGamma(reader); // >= 1, represents length - 2
        var length = lengthCode + 2 - 1;    // actual length = code + 1 (min 2)
        var offset = ReadGamma(reader);      // >= 1

        if (offset > pos)
          throw new InvalidDataException($"BriefLZ match offset {offset} exceeds current position {pos}.");

        // Copy match bytes, handling overlapping copies.
        for (var i = 0; i < length; i++) {
          if (pos >= uncompressedSize)
            throw new InvalidDataException("BriefLZ decompressed data exceeds expected size.");
          dst[pos] = dst[pos - offset];
          pos++;
        }
      }
    }

    return dst;
  }

  /// <summary>
  /// Reads an Elias gamma coded value from the bitstream.
  /// Decoding: count leading zero bits (n), then read n+1 bits as the value.
  /// </summary>
  private static int ReadGamma(BitReader reader) {
    var zeros = 0;
    while (reader.ReadBit() == 0)
      zeros++;

    // The leading 1 bit is already consumed. Read the remaining 'zeros' bits.
    var value = 1;
    for (var i = 0; i < zeros; i++)
      value = (value << 1) | reader.ReadBit();

    return value;
  }

  /// <summary>
  /// MSB-first bit writer that accumulates bits into a byte buffer.
  /// </summary>
  private sealed class BitWriter {
    private readonly MemoryStream _buffer = new();
    private int _currentByte;
    private int _bitsUsed; // number of bits written into _currentByte (0..8)

    public void WriteBit(int bit) {
      _currentByte = (_currentByte << 1) | (bit & 1);
      _bitsUsed++;
      if (_bitsUsed == 8) {
        _buffer.WriteByte((byte)_currentByte);
        _currentByte = 0;
        _bitsUsed = 0;
      }
    }

    public void WriteByte(byte b) {
      for (var i = 7; i >= 0; i--)
        WriteBit((b >> i) & 1);
    }

    public byte[] ToArray() {
      // Flush any remaining bits, padding with zeros on the right.
      if (_bitsUsed > 0) {
        _currentByte <<= (8 - _bitsUsed);
        _buffer.WriteByte((byte)_currentByte);
      }

      return _buffer.ToArray();
    }
  }

  /// <summary>
  /// MSB-first bit reader that reads bits from a byte array.
  /// </summary>
  private sealed class BitReader(byte[] data) {
    private int _bytePos;
    private int _bitPos = 8; // force load on first read

    public int ReadBit() {
      if (_bitPos >= 8) {
        if (_bytePos >= data.Length)
          throw new InvalidDataException("Unexpected end of BriefLZ compressed data.");
        _bitPos = 0;
        _bytePos++;
      }

      // Read MSB-first from current byte.
      var bit = (data[_bytePos - 1] >> (7 - _bitPos)) & 1;
      _bitPos++;
      return bit;
    }

    public byte ReadByte() {
      var value = 0;
      for (var i = 0; i < 8; i++)
        value = (value << 1) | ReadBit();
      return (byte)value;
    }
  }
}
