using System.Buffers.Binary;

namespace FileFormat.Yaz0;

/// <summary>
/// Provides static methods for compressing and decompressing data in the Yaz0 format.
/// </summary>
/// <remarks>
/// Yaz0 is a grouped-flag LZSS compression format used by Nintendo.
/// The header is 16 bytes: 4-byte magic "Yaz0", 4-byte big-endian uncompressed size,
/// and 8 bytes reserved (zeros). The payload uses flag bytes where each bit (MSB first)
/// controls whether the next operation is a literal (1) or a back-reference (0).
/// </remarks>
public static class Yaz0Stream {
  private static readonly byte[] Magic = "Yaz0"u8.ToArray();

  private const int HeaderSize = 16;
  private const int WindowSize = 4096;    // 12-bit distance
  private const int MinMatch = 3;
  private const int MaxMatch2Byte = 17;   // nibble + 2, nibble in 1..15
  private const int MaxMatch3Byte = 273;  // byte + 0x12
  private const int HashSize = 1 << 14;   // 16384 buckets
  private const int HashMask = HashSize - 1;

  /// <summary>
  /// Compresses all data from <paramref name="input"/> and writes a Yaz0 stream
  /// to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream that receives the Yaz0 compressed data.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input into memory for sliding-window access.
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write header.
    Span<byte> header = stackalloc byte[HeaderSize];
    Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)data.Length);
    // Bytes 8..15 are reserved zeros (already zeroed by stackalloc).
    output.Write(header);

    if (data.Length == 0)
      return;

    // Hash chain for match finding: head[hash] -> most recent position,
    // prev[pos] -> previous position with the same hash.
    var head = new int[HashSize];
    var prev = new int[data.Length];
    Array.Fill(head, -1);
    Array.Fill(prev, -1);

    var pos = 0;
    while (pos < data.Length) {
      // Buffer up to 8 operations, then write flag byte + payload.
      var flagByte = 0;
      using var chunk = new MemoryStream();

      for (var bit = 7; bit >= 0 && pos < data.Length; --bit) {
        var (matchDist, matchLen) = FindMatch(data, pos, head, prev);

        if (matchLen >= MinMatch) {
          // Back-reference: flag bit = 0.
          var dist = matchDist - 1; // encode as dist-1 (12-bit)
          if (matchLen <= MaxMatch2Byte) {
            // 2-byte back-reference: upper nibble = len - 2, lower 12 bits = dist.
            var nibble = matchLen - 2;
            chunk.WriteByte((byte)(((nibble & 0x0F) << 4) | ((dist >> 8) & 0x0F)));
            chunk.WriteByte((byte)(dist & 0xFF));
          } else {
            // 3-byte back-reference: upper nibble = 0, lower 12 bits = dist, third byte = len - 0x12.
            chunk.WriteByte((byte)((dist >> 8) & 0x0F));
            chunk.WriteByte((byte)(dist & 0xFF));
            chunk.WriteByte((byte)(matchLen - 0x12));
          }

          // Update hash chain for all positions in the match.
          for (var i = 0; i < matchLen; ++i)
            UpdateHash(data, pos + i, head, prev);

          pos += matchLen;
        } else {
          // Literal: flag bit = 1.
          flagByte |= 1 << bit;
          chunk.WriteByte(data[pos]);
          UpdateHash(data, pos, head, prev);
          ++pos;
        }
      }

      output.WriteByte((byte)flagByte);
      chunk.WriteTo(output);
    }
  }

  /// <summary>
  /// Decompresses a Yaz0 stream from <paramref name="input"/> and writes
  /// the uncompressed data to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing Yaz0 compressed data.</param>
  /// <param name="output">The stream that receives the decompressed data.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the stream does not start with the Yaz0 magic signature.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read header.
    Span<byte> header = stackalloc byte[HeaderSize];
    ReadExact(input, header);

    if (!header[..4].SequenceEqual(Magic))
      throw new InvalidDataException("Not a valid Yaz0 stream: bad magic.");

    var uncompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]);

    var buf = new byte[uncompressedSize];
    var outputPos = 0;

    while (outputPos < uncompressedSize) {
      var codeByte = ReadByte(input);

      for (var bit = 7; bit >= 0 && outputPos < uncompressedSize; --bit) {
        if (((codeByte >> bit) & 1) == 1) {
          // Literal byte.
          buf[outputPos++] = ReadByte(input);
        } else {
          // Back-reference.
          var byte1 = ReadByte(input);
          var byte2 = ReadByte(input);
          var dist = ((byte1 & 0x0F) << 8) | byte2;
          var copyFrom = outputPos - dist - 1;

          var nibble = byte1 >> 4;
          int count;
          if (nibble == 0) {
            count = ReadByte(input) + 0x12;
          } else {
            count = nibble + 2;
          }

          for (var i = 0; i < count; ++i) {
            buf[outputPos++] = buf[copyFrom + i];
          }
        }
      }
    }

    output.Write(buf, 0, uncompressedSize);
  }

  private static int Hash3(byte[] data, int pos) {
    if (pos + 2 >= data.Length)
      return 0;
    return ((data[pos] << 6) ^ (data[pos + 1] << 3) ^ data[pos + 2]) & HashMask;
  }

  private static void UpdateHash(byte[] data, int pos, int[] head, int[] prev) {
    if (pos + 2 >= data.Length)
      return;
    var h = Hash3(data, pos);
    prev[pos] = head[h];
    head[h] = pos;
  }

  private static (int Distance, int Length) FindMatch(byte[] data, int pos, int[] head, int[] prev) {
    if (pos + 2 >= data.Length)
      return (0, 0);

    var h = Hash3(data, pos);
    var bestLen = 0;
    var bestDist = 0;
    var maxLen = Math.Min(MaxMatch3Byte, data.Length - pos);
    var minPos = Math.Max(0, pos - WindowSize);

    var chainLen = 0;
    const int maxChain = 4096;

    var candidate = head[h];
    while (candidate >= 0 && candidate >= minPos && chainLen < maxChain) {
      // Check candidate.
      if (data[candidate] == data[pos]) {
        var len = 0;
        while (len < maxLen && data[candidate + len] == data[pos + len])
          ++len;

        if (len > bestLen) {
          bestLen = len;
          bestDist = pos - candidate;
          if (bestLen == maxLen)
            break;
        }
      }

      candidate = prev[candidate];
      ++chainLen;
    }

    if (bestLen < MinMatch)
      return (0, 0);

    return (bestDist, bestLen);
  }

  private static void ReadExact(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        throw new InvalidDataException("Unexpected end of Yaz0 stream.");
      offset += read;
    }
  }

  private static byte ReadByte(Stream stream) {
    var b = stream.ReadByte();
    if (b < 0)
      throw new InvalidDataException("Unexpected end of Yaz0 stream.");
    return (byte)b;
  }
}
