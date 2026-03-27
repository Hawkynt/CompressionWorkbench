using System.Buffers.Binary;

namespace FileFormat.Lzg;

/// <summary>
/// Provides LZG compression and decompression (simplified liblzg-compatible format).
/// </summary>
/// <remarks>
/// 16-byte header: magic "LZG" (3 bytes), method (1 byte: 0=copy, 1=lzg1),
/// decoded size (4 bytes BE), encoded size (4 bytes BE), checksum (4 bytes — Adler-32 of decoded data).
/// For method 1 (LZG1): escape byte 0xFF is used:
///   Non-0xFF byte: literal.
///   0xFF 0x00: literal 0xFF.
///   0xFF + len_minus_2 + offset_hi + offset_lo: back-reference (len_minus_2 1-255 = length 3-257, offset 1-65535).
/// Sliding window: 2048 bytes, minimum match: 3, maximum match: 257.
/// </remarks>
public static class LzgStream {

  /// <summary>Magic bytes: LZG.</summary>
  private static readonly byte[] Magic = "LZG"u8.ToArray();

  private const byte Escape = 0xFF;
  private const int WindowSize = 2048;
  private const int MinMatch = 3;
  private const int MaxMatch = 257;
  private const int HashBits = 12;
  private const int HashSize = 1 << HashBits;
  private const int MaxChain = 32;

  private const byte MethodCopy = 0;
  private const byte MethodLzg1 = 1;

  /// <summary>
  /// Compresses <paramref name="input"/> to <paramref name="output"/> using LZG encoding.
  /// </summary>
  /// <param name="input">The uncompressed source stream.</param>
  /// <param name="output">The destination stream for compressed data.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    var checksum = Adler32(data);
    var encoded = data.Length > 0 ? CompressLzg1(data) : [];

    // If compressed is not smaller, use copy method.
    var useCopy = encoded.Length >= data.Length;
    var payload = useCopy ? data : encoded;
    var method = useCopy ? MethodCopy : MethodLzg1;

    // Write 16-byte header.
    output.Write(Magic);
    output.WriteByte(method);
    Span<byte> sizes = stackalloc byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(sizes, (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(sizes[4..], (uint)payload.Length);
    BinaryPrimitives.WriteUInt32BigEndian(sizes[8..], checksum);
    output.Write(sizes);

    output.Write(payload);
  }

  /// <summary>
  /// Decompresses <paramref name="input"/> to <paramref name="output"/> using LZG decoding.
  /// </summary>
  /// <param name="input">The compressed source stream.</param>
  /// <param name="output">The destination stream for decompressed data.</param>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read 16-byte header.
    Span<byte> header = stackalloc byte[16];
    if (input.ReadAtLeast(header, 16, throwOnEndOfStream: false) < 16)
      throw new InvalidDataException("Stream too short for LZG header.");

    if (header[0] != Magic[0] || header[1] != Magic[1] || header[2] != Magic[2])
      throw new InvalidDataException("Invalid LZG magic.");

    var method = header[3];
    var decodedSize = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
    var encodedSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
    var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);

    // Read encoded payload.
    var payload = new byte[encodedSize];
    if (encodedSize > 0) {
      var totalRead = 0;
      while (totalRead < (int)encodedSize) {
        var read = input.Read(payload, totalRead, (int)encodedSize - totalRead);
        if (read == 0)
          throw new InvalidDataException("Unexpected end of LZG stream.");
        totalRead += read;
      }
    }

    byte[] result;
    if (method == MethodCopy) {
      result = payload;
    } else if (method == MethodLzg1) {
      result = DecompressLzg1(payload, (int)decodedSize);
    } else {
      throw new InvalidDataException($"Unknown LZG method: {method}.");
    }

    // Verify checksum.
    var actualChecksum = Adler32(result);
    if (actualChecksum != expectedChecksum)
      throw new InvalidDataException("LZG checksum mismatch.");

    output.Write(result);
  }

  private static byte[] CompressLzg1(byte[] data) {
    var result = new List<byte>(data.Length);
    var hashTable = new int[HashSize];
    var chain = new int[data.Length];
    Array.Fill(hashTable, -1);
    Array.Fill(chain, -1);

    var pos = 0;
    while (pos < data.Length) {
      var bestLen = 0;
      var bestOff = 0;

      if (pos + MinMatch <= data.Length) {
        var hash = Hash3(data, pos);
        var candidate = hashTable[hash];
        var minPos = Math.Max(0, pos - WindowSize);
        var attempts = MaxChain;

        while (candidate >= minPos && attempts-- > 0) {
          if (candidate < pos) {
            var maxLen = Math.Min(MaxMatch, data.Length - pos);
            var len = 0;
            while (len < maxLen && data[candidate + len] == data[pos + len])
              len++;

            if (len >= MinMatch && len > bestLen) {
              var dist = pos - candidate;
              if (dist <= 65535) {
                bestLen = len;
                bestOff = dist;
                if (bestLen == maxLen)
                  break;
              }
            }
          }

          var prev = chain[candidate];
          if (prev >= candidate)
            break;
          candidate = prev;
        }
      }

      // Insert hash for current position.
      if (pos + 2 < data.Length) {
        var h = Hash3(data, pos);
        chain[pos] = hashTable[h];
        hashTable[h] = pos;
      }

      if (bestLen >= MinMatch) {
        // Emit match: escape + len_minus_2 + offset_hi + offset_lo.
        // len_minus_2 is 1..255 (length 3..257), so never 0x00 (which is the literal escape).
        result.Add(Escape);
        result.Add((byte)(bestLen - 2));
        result.Add((byte)(bestOff >> 8));
        result.Add((byte)(bestOff & 0xFF));

        // Insert hashes for matched positions (skip pos 0, already inserted).
        for (var i = 1; i < bestLen && pos + i + 2 < data.Length; i++) {
          var h = Hash3(data, pos + i);
          chain[pos + i] = hashTable[h];
          hashTable[h] = pos + i;
        }

        pos += bestLen;
      } else {
        // Literal.
        if (data[pos] == Escape) {
          result.Add(Escape);
          result.Add(0x00);
        } else {
          result.Add(data[pos]);
        }
        pos++;
      }
    }

    return result.ToArray();
  }

  private static byte[] DecompressLzg1(byte[] payload, int decodedSize) {
    if (decodedSize == 0)
      return [];

    var result = new List<byte>(decodedSize);
    var i = 0;

    while (result.Count < decodedSize && i < payload.Length) {
      if (payload[i] == Escape) {
        i++;
        if (i >= payload.Length)
          throw new InvalidDataException("Unexpected end of LZG1 stream after escape.");

        if (payload[i] == 0x00) {
          // Escaped literal 0xFF.
          result.Add(Escape);
          i++;
        } else {
          // Match: len_minus_2, offset_hi, offset_lo.
          if (i + 2 >= payload.Length)
            throw new InvalidDataException("Unexpected end of LZG1 stream during match.");

          var length = payload[i] + 2;
          var offset = (payload[i + 1] << 8) | payload[i + 2];
          i += 3;

          if (offset > result.Count || offset == 0)
            throw new InvalidDataException($"LZG1 invalid offset {offset} at position {result.Count}.");

          for (var j = 0; j < length; j++)
            result.Add(result[result.Count - offset]);
        }
      } else {
        // Literal byte.
        result.Add(payload[i]);
        i++;
      }
    }

    if (result.Count != decodedSize)
      throw new InvalidDataException($"LZG1 decoded size mismatch: expected {decodedSize}, got {result.Count}.");

    return result.ToArray();
  }

  private static int Hash3(byte[] data, int pos) =>
    ((data[pos] << 10) ^ (data[pos + 1] << 5) ^ data[pos + 2]) & (HashSize - 1);

  private static uint Adler32(byte[] data) {
    uint a = 1, b = 0;
    const uint mod = 65521;

    foreach (var d in data) {
      a = (a + d) % mod;
      b = (b + a) % mod;
    }

    return (b << 16) | a;
  }
}
