using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Snappy;

/// <summary>
/// Compresses data using the Snappy block format.
/// Snappy is a fast LZ77 variant with no entropy coding, designed for speed over ratio.
/// </summary>
public static class SnappyCompressor {
  /// <summary>
  /// Compresses the input data using Snappy block format.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed data including the varint-encoded original size header.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    if (source.Length == 0)
      return [0]; // varint 0

    // Worst case: varint header + source + overhead
    var buf = new byte[10 + source.Length + source.Length / 6 + 32];
    var pos = WriteVarInt(buf, 0, source.Length);
    pos = CompressBlock(source, buf, pos);
    return buf.AsSpan(0, pos).ToArray();
  }

  private static int CompressBlock(ReadOnlySpan<byte> src, Span<byte> dst, int dstPos) {
    var srcLen = src.Length;
    if (srcLen == 0)
      return dstPos;

    var hashTable = new int[SnappyConstants.HashTableSize];
    hashTable.AsSpan().Fill(-1);
    
    var pos = 0;
    var litStart = 0;

    while (pos + 3 < srcLen) {
      var h = Hash4(src, pos);
      var candidate = hashTable[h];
      hashTable[h] = pos;

      if (candidate >= 0 && pos - candidate <= SnappyConstants.MaxCopy2Offset &&
          src[candidate] == src[pos] &&
          src[candidate + 1] == src[pos + 1] &&
          src[candidate + 2] == src[pos + 2] &&
          src[candidate + 3] == src[pos + 3]) {
        // Found a match, emit pending literals first
        if (pos > litStart)
          dstPos = EmitLiterals(dst, dstPos, src, litStart, pos - litStart);

        // Extend match
        var matchLen = 4;
        while (pos + matchLen < srcLen &&
               src[candidate + matchLen] == src[pos + matchLen] &&
               matchLen < SnappyConstants.MaxMatchLength)
          ++matchLen;

        var offset = pos - candidate;
        dstPos = EmitCopy(dst, dstPos, offset, matchLen);

        // Insert hash entries for positions inside the match
        var end = pos + matchLen;
        ++pos;
        while (pos < end && pos + 3 < srcLen) {
          hashTable[Hash4(src, pos)] = pos;
          ++pos;
        }
        pos = end;
        litStart = pos;
      } else
        ++pos;
    }

    // Emit remaining literals
    if (litStart < srcLen)
      dstPos = EmitLiterals(dst, dstPos, src, litStart, srcLen - litStart);

    return dstPos;
  }

  private static int EmitLiterals(Span<byte> dst, int dstPos,
      ReadOnlySpan<byte> src, int start, int length) {
    var n = length - 1; // tag encodes length-1
    switch (n) {
      case < 60:
        dst[dstPos++] = (byte)(SnappyConstants.TagLiteral | (n << 2));
        break;

      case < 0x100:
        dst[dstPos++] = (byte)(SnappyConstants.TagLiteral | (60 << 2));
        dst[dstPos++] = (byte)n;
        break;

      case < 0x10000:
        dst[dstPos++] = (byte)(SnappyConstants.TagLiteral | (61 << 2));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[dstPos..], (ushort)n);
        dstPos += 2;
        break;

      case < 0x1000000:
        dst[dstPos++] = (byte)(SnappyConstants.TagLiteral | (62 << 2));
        dst[dstPos++] = (byte)n;
        dst[dstPos++] = (byte)(n >> 8);
        dst[dstPos++] = (byte)(n >> 16);
        break;

      default:
        dst[dstPos++] = (byte)(SnappyConstants.TagLiteral | (63 << 2));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[dstPos..], (uint)n);
        dstPos += 4;
        break;
    }

    src.Slice(start, length).CopyTo(dst[dstPos..]);
    dstPos += length;
    return dstPos;
  }

  private static int EmitCopy(Span<byte> dst, int dstPos, int offset, int length) {
    // Use copy-1 (2 bytes) for short offsets, copy-2 (3 bytes) for longer
    while (length > 0) {
      var chunk = Math.Min(length, SnappyConstants.MaxMatchLength);
      switch (offset) {
        case <= SnappyConstants.MaxCopy1Offset when chunk is >= 4 and <= 11:
          // Copy-1: 1 byte tag + 1 byte with top 3 bits of offset
          // Tag: OOOLLL01 where OOO = offset bits 10:8, LLL = length - 4
          dst[dstPos++] = (byte)(SnappyConstants.TagCopy1 |
            ((chunk - 4) << 2) |
            ((offset >> 8) << 5));
          dst[dstPos++] = (byte)offset;
          break;

        case <= SnappyConstants.MaxCopy2Offset: {
          // Copy-2: 1 byte tag + 2 byte LE offset
          // Tag: LLLLLL10 where LLLLLL = length - 1
          var l = Math.Min(chunk, SnappyConstants.MaxMatchLength);
          dst[dstPos++] = (byte)(SnappyConstants.TagCopy2 | ((l - 1) << 2));
          BinaryPrimitives.WriteUInt16LittleEndian(dst[dstPos..], (ushort)offset);
          dstPos += 2;
          chunk = l;
          break;
        }

        default: {
          // Copy-4: 1 byte tag + 4 byte LE offset (for very large offsets)
          var l = Math.Min(chunk, SnappyConstants.MaxMatchLength);
          dst[dstPos++] = (byte)(SnappyConstants.TagCopy4 | ((l - 1) << 2));
          BinaryPrimitives.WriteUInt32LittleEndian(dst[dstPos..], (uint)offset);
          dstPos += 4;
          chunk = l;
          break;
        }
      }
      length -= chunk;
    }
    return dstPos;
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    (int)(BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) * 0x1E35A7BD >> (32 - SnappyConstants.HashTableBits));

  private static int WriteVarInt(Span<byte> buf, int pos, int value) {
    var v = (uint)value;
    while (v >= 128) {
      buf[pos++] = (byte)(v | 0x80);
      v >>= 7;
    }
    buf[pos++] = (byte)v;
    return pos;
  }
}
