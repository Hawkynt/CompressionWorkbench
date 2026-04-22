#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ntfs;

/// <summary>
/// LZNT1 compression used by NTFS for compressed files.
/// Operates on 4KB compression units. Each unit is independently compressed.
/// </summary>
public static class Lznt1 {
  private const int BlockSize = 4096;

  /// <summary>Decompresses LZNT1-compressed data.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize) {
    var output = new List<byte>(uncompressedSize);
    var pos = 0;

    while (pos + 2 <= input.Length && output.Count < uncompressedSize) {
      var header = BinaryPrimitives.ReadUInt16LittleEndian(input[pos..]);
      pos += 2;

      if (header == 0) break;

      var chunkSize = (header & 0x0FFF) + 1;
      var isCompressed = (header & 0x8000) != 0;

      if (!isCompressed) {
        // Uncompressed chunk — copy raw bytes
        var toCopy = Math.Min(chunkSize, input.Length - pos);
        toCopy = Math.Min(toCopy, uncompressedSize - output.Count);
        for (var i = 0; i < toCopy; i++)
          output.Add(input[pos + i]);
        pos += chunkSize;
        continue;
      }

      // Compressed chunk
      var chunkEnd = pos + chunkSize;
      if (chunkEnd > input.Length) chunkEnd = input.Length;
      var blockStart = output.Count;

      while (pos < chunkEnd && output.Count < uncompressedSize) {
        if (pos >= input.Length) break;
        var flagByte = input[pos++];

        for (var bit = 0; bit < 8 && pos < chunkEnd && output.Count < uncompressedSize; bit++) {
          if ((flagByte & (1 << bit)) == 0) {
            // Literal byte
            if (pos < input.Length)
              output.Add(input[pos++]);
          } else {
            // Match reference
            if (pos + 2 > input.Length) break;
            var token = BinaryPrimitives.ReadUInt16LittleEndian(input[pos..]);
            pos += 2;

            var posInBlock = output.Count - blockStart;
            var offsetBits = GetOffsetBits(posInBlock);
            var lengthBits = 16 - offsetBits;
            var lengthMask = (1 << lengthBits) - 1;

            var matchLength = (token & lengthMask) + 3;
            var matchOffset = (token >> lengthBits) + 1;

            for (var j = 0; j < matchLength && output.Count < uncompressedSize; j++) {
              var srcIdx = output.Count - matchOffset;
              output.Add(srcIdx >= 0 && srcIdx < output.Count ? output[srcIdx] : (byte)0);
            }
          }
        }
      }

      pos = Math.Max(pos, chunkEnd);
    }

    return output.Count > uncompressedSize
      ? output.GetRange(0, uncompressedSize).ToArray()
      : [.. output];
  }

  /// <summary>Compresses data using LZNT1.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    using var ms = new MemoryStream();

    var offset = 0;
    var hdrBuf = new byte[2];
    while (offset < input.Length) {
      var blockLen = Math.Min(BlockSize, input.Length - offset);
      var block = input.Slice(offset, blockLen);
      var compressed = CompressBlock(block);

      if (compressed.Length < blockLen) {
        // Write compressed chunk header
        var header = (ushort)(0x8000 | (compressed.Length - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(hdrBuf, header);
        ms.Write(hdrBuf);
        ms.Write(compressed);
      } else {
        // Write uncompressed chunk header
        var header = (ushort)(blockLen - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(hdrBuf, header);
        ms.Write(hdrBuf);
        ms.Write(block);
      }

      offset += blockLen;
    }

    return ms.ToArray();
  }

  private static byte[] CompressBlock(ReadOnlySpan<byte> block) {
    using var ms = new MemoryStream();
    var pos = 0;

    while (pos < block.Length) {
      var flagPos = (int)ms.Position;
      ms.WriteByte(0); // placeholder for flag byte
      byte flags = 0;

      for (var bit = 0; bit < 8 && pos < block.Length; bit++) {
        var offsetBits = GetOffsetBits(pos);
        var lengthBits = 16 - offsetBits;
        var maxLength = (1 << lengthBits) - 1 + 3;
        var maxOffset = (1 << offsetBits);

        // Search for a match
        var bestLen = 0;
        var bestOff = 0;

        if (pos >= 1) {
          var searchStart = Math.Max(0, pos - maxOffset);
          for (var s = pos - 1; s >= searchStart; s--) {
            var len = 0;
            while (pos + len < block.Length && len < maxLength && block[s + len] == block[pos + len])
              len++;
            if (len >= 3 && len > bestLen) {
              bestLen = len;
              bestOff = pos - s;
              if (bestLen == maxLength) break;
            }
          }
        }

        if (bestLen >= 3) {
          // Match reference
          flags |= (byte)(1 << bit);
          var token = (ushort)(((bestOff - 1) << lengthBits) | (bestLen - 3));
          var tb = new byte[2];
          BinaryPrimitives.WriteUInt16LittleEndian(tb, token);
          ms.Write(tb);
          pos += bestLen;
        } else {
          // Literal
          ms.WriteByte(block[pos++]);
        }
      }

      // Patch flags
      var savedPos = ms.Position;
      ms.Position = flagPos;
      ms.WriteByte(flags);
      ms.Position = savedPos;
    }

    return ms.ToArray();
  }

  private static int GetOffsetBits(int posInBlock) {
    if (posInBlock < 0x10) return 12;
    // offset bits = 16 - ceil(log2(posInBlock))
    var bits = 0;
    var v = posInBlock - 1;
    while (v > 0) { bits++; v >>= 1; }
    return 16 - bits;
  }
}
