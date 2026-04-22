#pragma warning disable CS1591
namespace FileSystem.DoubleSpace;

/// <summary>
/// DoubleSpace/DriveSpace sector-level LZ77 compression.
/// Each compressed block has a 2-byte header: bit 15 = compressed flag,
/// bits 0-11 = data size minus 1. Compressed data uses flag bytes (8 tokens
/// per flag), with literals and (offset, length) match pairs.
/// Match format: uint16 LE with offset in high bits, length-3 in low bits.
/// </summary>
public static class DsCompression {
  private const int WindowSize = 512;
  private const int MinMatch = 3;
  private const int MaxMatch = 18; // 4 bits for length-3 = 0..15, +3 = 3..18

  /// <summary>Compresses a single sector (up to 512 or 1024 bytes).</summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.Length == 0) return [0x00, 0x00]; // empty block, uncompressed, size=1

    // Try compression
    var compressed = CompressCore(input);

    // If compression didn't shrink, store uncompressed
    if (compressed.Length >= input.Length) {
      var result = new byte[2 + input.Length];
      // Header: bit 15 clear (uncompressed), size-1 in low 12 bits
      var header = (ushort)(input.Length - 1);
      result[0] = (byte)(header & 0xFF);
      result[1] = (byte)((header >> 8) & 0xFF);
      input.CopyTo(result.AsSpan(2));
      return result;
    } else {
      var result = new byte[2 + compressed.Length];
      // Header: bit 15 set (compressed), size-1 in low 12 bits
      var header = (ushort)((compressed.Length - 1) | 0x8000);
      result[0] = (byte)(header & 0xFF);
      result[1] = (byte)((header >> 8) & 0xFF);
      compressed.CopyTo(result, 2);
      return result;
    }
  }

  private static byte[] CompressCore(ReadOnlySpan<byte> input) {
    var output = new List<byte>();
    var pos = 0;

    while (pos < input.Length) {
      var flagPos = output.Count;
      output.Add(0); // placeholder for flag byte
      byte flags = 0;

      for (var bit = 0; bit < 8 && pos < input.Length; bit++) {
        var bestLen = 0;
        var bestOff = 0;

        // Search for matches in the window
        var searchStart = Math.Max(0, pos - WindowSize);
        for (var s = searchStart; s < pos; s++) {
          var len = 0;
          while (len < MaxMatch && pos + len < input.Length && input[s + len] == input[pos + len])
            len++;
          if (len >= MinMatch && len > bestLen) {
            bestLen = len;
            bestOff = pos - s;
          }
        }

        if (bestLen >= MinMatch) {
          // Match: set flag bit
          flags |= (byte)(1 << bit);
          // Encode: offset in high 12 bits, (length-3) in low 4 bits
          var encoded = (ushort)(((bestOff & 0xFFF) << 4) | ((bestLen - MinMatch) & 0xF));
          output.Add((byte)(encoded & 0xFF));
          output.Add((byte)((encoded >> 8) & 0xFF));
          pos += bestLen;
        } else {
          // Literal: flag bit stays 0
          output.Add(input[pos]);
          pos++;
        }
      }

      output[flagPos] = flags;
    }

    return output.ToArray();
  }

  /// <summary>Decompresses a single block (header + data).</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> block) {
    if (block.Length < 2)
      throw new InvalidDataException("DS: block too small.");

    var header = (ushort)(block[0] | (block[1] << 8));
    var isCompressed = (header & 0x8000) != 0;
    var dataSize = (header & 0x0FFF) + 1;

    if (2 + dataSize > block.Length)
      throw new InvalidDataException("DS: block data truncated.");

    var data = block.Slice(2, dataSize);

    if (!isCompressed)
      return data.ToArray();

    return DecompressCore(data);
  }

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    var output = new List<byte>();
    var pos = 0;

    while (pos < data.Length) {
      var flags = data[pos++];

      for (var bit = 0; bit < 8 && pos < data.Length; bit++) {
        if ((flags & (1 << bit)) != 0) {
          // Match
          if (pos + 2 > data.Length) break;
          var encoded = (ushort)(data[pos] | (data[pos + 1] << 8));
          pos += 2;
          var offset = (encoded >> 4) & 0xFFF;
          var length = (encoded & 0xF) + MinMatch;

          if (offset == 0 || offset > output.Count)
            throw new InvalidDataException("DS: invalid match offset.");

          var srcPos = output.Count - offset;
          for (var j = 0; j < length; j++)
            output.Add(output[srcPos + j]);
        } else {
          // Literal
          output.Add(data[pos++]);
        }
      }
    }

    return output.ToArray();
  }
}
