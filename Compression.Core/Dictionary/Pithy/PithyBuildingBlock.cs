using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Pithy;

/// <summary>
/// Pithy — John Engelhart's fast LZ77 compressor, deliberately similar in shape
/// to Google's Snappy but with an incompatible tag layout: a third copy tier with
/// a 3-byte offset replaces Snappy's 4-byte-offset tier, trading maximum window
/// size (16 MiB instead of 4 GiB) for a shorter encoding of large-window matches.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented Pithy tag scheme (see references below): a
/// varint-encoded uncompressed length is followed by a stream of one-byte tags
/// whose low 2 bits select a type — <c>00</c> literal, <c>01</c>/<c>10</c>/<c>11</c>
/// a copy with a 1/2/3-byte offset:
/// </para>
/// <list type="bullet">
///   <item><description>Literal: the upper 6 bits hold <c>length - 1</c> (0–59
///     direct; 60/61/62/63 mean "1/2/3/4 following bytes hold the length",
///     matching Snappy's literal length encoding).</description></item>
///   <item><description>Copy-1: an 11-bit offset (3 bits packed into the tag's
///     upper bits, 8 more in one following byte) and a length of 4–11 (3 bits in
///     the tag).</description></item>
///   <item><description>Copy-2 / Copy-3: the upper 6 bits hold <c>length - 1</c>
///     (1–64), followed by a 2- or 3-byte little-endian offset.</description></item>
/// </list>
/// <para>
/// This is a clean-room implementation written from the format description, not
/// a port of Engelhart's reference `pithy.c`; only this building block's own
/// round-trip is guaranteed.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>Pithy — https://github.com/johnezang/pithy</description></item>
///   <item><description>Pithy header (tag layout comments) — https://github.com/johnezang/pithy/blob/master/pithy.h</description></item>
/// </list>
/// </remarks>
public sealed class PithyBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Pithy";
  /// <inheritdoc/>
  public string DisplayName => "Pithy";
  /// <inheritdoc/>
  public string Description => "Engelhart's Snappy-shaped LZ77 codec with a 3-byte-offset copy tier in place of Snappy's 4-byte tier";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const byte TagLiteral = 0;
  private const byte TagCopy1 = 1;
  private const byte TagCopy2 = 2;
  private const byte TagCopy3 = 3;

  private const int MinMatch = 4;
  private const int MaxCopy1Offset = 2047;      // 11-bit offset
  private const int MaxCopy1Length = 11;
  private const int MaxCopy2Offset = 65535;     // 16-bit offset
  private const int MaxCopy3Offset = 16777215;  // 24-bit offset
  private const int MaxCopy23Length = 64;

  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 64;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    WriteVarInt(ms, (uint)data.Length);

    if (data.Length == 0)
      return ms.ToArray();

    var src = data.ToArray();
    var n = src.Length;
    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[n];

    var pos = 0;
    var litStart = 0;

    while (pos + MinMatch <= n) {
      var (bestLen, bestOff) = FindMatch(src, pos, hashHead, chain);
      InsertHash(src, pos, hashHead, chain);

      if (bestLen < MinMatch) {
        ++pos;
        continue;
      }

      if (pos > litStart)
        EmitLiterals(ms, src, litStart, pos - litStart);

      var end = Math.Min(pos + bestLen, n - 2);
      for (var i = pos + 1; i < end; ++i)
        InsertHash(src, i, hashHead, chain);

      EmitCopy(ms, bestOff, bestLen);
      pos += bestLen;
      litStart = pos;
    }

    if (litStart < n)
      EmitLiterals(ms, src, litStart, n - litStart);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var i = 0;
    var originalSize = (int)ReadVarInt(data, ref i);
    var dst = new byte[originalSize];
    var pos = 0;

    while (pos < originalSize) {
      var tag = data[i++];
      var type = tag & 0x3;

      switch (type) {
        case TagLiteral: {
          var len = ReadLiteralLength(data, ref i, tag >> 2);
          data.Slice(i, len).CopyTo(dst.AsSpan(pos));
          i += len;
          pos += len;
          break;
        }

        case TagCopy1: {
          var len = ((tag >> 2) & 0x7) + 4;
          var offset = ((tag >> 5) << 8) | data[i++];
          CopyMatch(dst, ref pos, offset, len, originalSize);
          break;
        }

        case TagCopy2: {
          var len = (tag >> 2) + 1;
          var offset = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
          i += 2;
          CopyMatch(dst, ref pos, offset, len, originalSize);
          break;
        }

        default: { // TagCopy3
          var len = (tag >> 2) + 1;
          var offset = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
          i += 3;
          CopyMatch(dst, ref pos, offset, len, originalSize);
          break;
        }
      }
    }

    return dst;
  }

  private static void CopyMatch(byte[] dst, ref int pos, int offset, int length, int limit) {
    if (offset <= 0 || offset > pos)
      throw new InvalidDataException($"Pithy: match offset {offset} invalid at position {pos}.");

    for (var k = 0; k < length && pos < limit; ++k, ++pos)
      dst[pos] = dst[pos - offset];
  }

  private static void EmitLiterals(Stream output, byte[] src, int start, int length) {
    var n = length - 1;
    switch (n) {
      case < 60:
        output.WriteByte((byte)(TagLiteral | (n << 2)));
        break;
      case < 0x100:
        output.WriteByte((byte)(TagLiteral | (60 << 2)));
        output.WriteByte((byte)n);
        break;
      case < 0x10000:
        output.WriteByte((byte)(TagLiteral | (61 << 2)));
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(two, (ushort)n);
        output.Write(two);
        break;
      case < 0x1000000:
        output.WriteByte((byte)(TagLiteral | (62 << 2)));
        output.WriteByte((byte)n);
        output.WriteByte((byte)(n >> 8));
        output.WriteByte((byte)(n >> 16));
        break;
      default:
        output.WriteByte((byte)(TagLiteral | (63 << 2)));
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)n);
        output.Write(four);
        break;
    }

    output.Write(src, start, length);
  }

  private static int ReadLiteralLength(ReadOnlySpan<byte> data, ref int i, int n) {
    switch (n) {
      case < 60:
        return n + 1;
      case 60:
        return data[i++] + 1;
      case 61: {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
        i += 2;
        return v + 1;
      }
      case 62: {
        var v = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
        i += 3;
        return v + 1;
      }
      default: {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        i += 4;
        return (int)v + 1;
      }
    }
  }

  private static void EmitCopy(Stream output, int offset, int length) {
    while (length > 0) {
      // Copy-1 needs length >= 4 (its 3-bit field encodes length - 4); Math.Min
      // against MaxCopy1Length (11) with that guard means chunk is always in [4,11].
      if (offset <= MaxCopy1Offset && length >= 4) {
        var chunk = Math.Min(length, MaxCopy1Length);
        output.WriteByte((byte)(TagCopy1 | ((chunk - 4) << 2) | ((offset >> 8) << 5)));
        output.WriteByte((byte)offset);
        length -= chunk;
        continue;
      }

      if (offset <= MaxCopy2Offset) {
        var chunk = Math.Min(length, MaxCopy23Length);
        output.WriteByte((byte)(TagCopy2 | ((chunk - 1) << 2)));
        output.WriteByte((byte)offset);
        output.WriteByte((byte)(offset >> 8));
        length -= chunk;
      } else {
        var chunk = Math.Min(length, MaxCopy23Length);
        output.WriteByte((byte)(TagCopy3 | ((chunk - 1) << 2)));
        output.WriteByte((byte)offset);
        output.WriteByte((byte)(offset >> 8));
        output.WriteByte((byte)(offset >> 16));
        length -= chunk;
      }
    }
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    var h = Hash4(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - MaxCopy3Offset);
    var maxLen = Math.Min(src.Length - pos, MaxCopy23Length * 64); // generous cap, chunked at emit time
    var bestLen = 0;
    var bestOff = 0;
    var steps = MaxChainSteps;

    while (candidate >= minPos && steps-- > 0) {
      if (bestLen == 0 || (candidate + bestLen < src.Length && src[candidate + bestLen] == src[pos + bestLen])) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          ++len;

        if (len > bestLen) {
          bestLen = len;
          bestOff = pos - candidate;
          if (bestLen >= maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }

    return bestLen >= MinMatch ? (bestLen, bestOff) : (0, 0);
  }

  private static void InsertHash(byte[] src, int pos, int[] hashHead, int[] chain) {
    if (pos + 4 > src.Length)
      return;
    var h = Hash4(src, pos);
    chain[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static int Hash4(byte[] data, int pos) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) * 2654435761u) >> (32 - HashBits));

  private static void WriteVarInt(Stream output, uint value) {
    while (value >= 0x80) {
      output.WriteByte((byte)(value | 0x80));
      value >>= 7;
    }
    output.WriteByte((byte)value);
  }

  private static uint ReadVarInt(ReadOnlySpan<byte> data, ref int i) {
    var result = 0u;
    var shift = 0;
    while (true) {
      var b = data[i++];
      result |= (uint)(b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return result;
      shift += 7;
    }
  }
}
