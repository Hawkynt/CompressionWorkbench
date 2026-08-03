using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzav;

/// <summary>
/// LZAV — Aleksey Vaneev's modern, byte-oriented (no bit-level packing) LZ77
/// compressor, tuned for very high throughput at a better ratio than LZ4.
/// </summary>
/// <remarks>
/// <para>
/// LZAV's reference format (see the header comment of <c>lzav.h</c>, linked
/// below) is a byte-aligned block stream where each block's leading byte packs a
/// 2-bit block type together with a 4-bit length nibble, offsets are emitted in a
/// width tied to the block type (10/15/21-bit tiers, carried across 1–3 extra
/// bytes), and a length nibble of zero means "read a 7-bit-continuation extension
/// byte" for lengths that don't fit the nibble. This building block captures
/// that same shape — byte-oriented tiered-offset blocks, a nibble-or-continuation
/// length field, minimum match length 6 — as a self-contained format:
/// </para>
/// <list type="bullet">
///   <item><description>Header byte: top 2 bits select the block type — <c>00</c>
///     literal run, <c>01</c>/<c>10</c>/<c>11</c> a back-reference whose offset is
///     carried in 1/2/3 further little-endian bytes (offset ranges 1..256,
///     1..65536, 1..16777216 respectively). The low 6 bits hold the length field:
///     <c>length - 1</c> for a literal run, <c>length - MinMatch</c> for a
///     match.</description></item>
///   <item><description>When the 6-bit length field equals 63, one or more
///     continuation bytes follow: the low 7 bits of each contribute the next
///     length chunk and the high bit signals "more bytes follow", mirroring the
///     reference format's variable-length extension.</description></item>
/// </list>
/// <para>
/// This is a clean-room implementation written from the format description, not
/// a port of <c>lzav.h</c>; it does not reproduce the reference's offset
/// carry-bit reuse optimization and is not bit-compatible with
/// <c>lzav_compress</c>/<c>lzav_decompress</c> output — only this building
/// block's own round-trip is guaranteed. The uncompressed length is carried by
/// the standard 4-byte little-endian building-block header.
/// </para>
/// <para>Reference: LZAV — https://github.com/avaneev/lzav</para>
/// </remarks>
public sealed class LzavBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzav";
  /// <inheritdoc/>
  public string DisplayName => "LZAV";
  /// <inheritdoc/>
  public string Description => "Vaneev's byte-oriented LZ77 with tiered offset widths and a nibble-or-continuation length field";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinMatch = 6;
  private const int LengthEscape = 63;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 64;
  private const int MaxWindow = 1 << 24; // largest tier: 3 offset bytes

  private const byte TypeLiteral = 0 << 6;
  private const byte Type1Byte = 1 << 6;
  private const byte Type2Byte = 2 << 6;
  private const byte Type3Byte = 3 << 6;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var src = data.ToArray();
    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[src.Length];

    var pos = 0;
    var litStart = 0;

    while (pos < src.Length) {
      var (bestLen, bestOff) = FindMatch(src, pos, hashHead, chain);

      if (pos + 3 <= src.Length)
        InsertHash(src, pos, hashHead, chain);

      if (bestLen >= MinMatch) {
        if (pos > litStart)
          EmitLiteralRun(ms, src, litStart, pos - litStart);

        EmitMatch(ms, bestLen, bestOff);

        var end = Math.Min(pos + bestLen, src.Length - 2);
        for (var i = pos + 1; i < end; ++i)
          InsertHash(src, i, hashHead, chain);

        pos += bestLen;
        litStart = pos;
      } else {
        ++pos;
      }
    }

    if (litStart < src.Length)
      EmitLiteralRun(ms, src, litStart, src.Length - litStart);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var payload = data[4..];
    var dst = new byte[originalSize];
    var pos = 0;
    var i = 0;

    while (pos < originalSize) {
      var b = payload[i++];
      var type = b & 0xC0;
      var length = ReadLength(payload, ref i, b & 0x3F) + (type == TypeLiteral ? 1 : MinMatch);

      if (type == TypeLiteral) {
        payload.Slice(i, length).CopyTo(dst.AsSpan(pos));
        i += length;
        pos += length;
      } else {
        int offset;
        switch (type) {
          case Type1Byte:
            offset = payload[i] + 1;
            i += 1;
            break;
          case Type2Byte:
            offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[i..]) + 1;
            i += 2;
            break;
          default:
            offset = (payload[i] | (payload[i + 1] << 8) | (payload[i + 2] << 16)) + 1;
            i += 3;
            break;
        }

        if (offset > pos)
          throw new InvalidDataException($"LZAV: match offset {offset} invalid at position {pos}.");

        for (var k = 0; k < length && pos < originalSize; ++k, ++pos)
          dst[pos] = dst[pos - offset];
      }
    }

    return dst;
  }

  private static void EmitLiteralRun(Stream output, byte[] src, int start, int length) {
    var remaining = length;
    var offset = start;

    // Split arbitrarily long runs into segments the 6-bit + continuation
    // length field can express in one header byte's worth of chunks.
    while (remaining > 0) {
      var chunk = remaining; // literal runs are not bounded except by input size
      EmitHeader(output, TypeLiteral, chunk - 1);
      output.Write(src, offset, chunk);
      offset += chunk;
      remaining -= chunk;
    }
  }

  private static void EmitMatch(Stream output, int length, int offset) {
    var type = offset switch {
      <= 256 => Type1Byte,
      <= 65536 => Type2Byte,
      _ => Type3Byte,
    };

    EmitHeader(output, type, length - MinMatch);

    var o = offset - 1;
    switch (type) {
      case Type1Byte:
        output.WriteByte((byte)o);
        break;
      case Type2Byte:
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(two, (ushort)o);
        output.Write(two);
        break;
      default:
        output.WriteByte((byte)o);
        output.WriteByte((byte)(o >> 8));
        output.WriteByte((byte)(o >> 16));
        break;
    }
  }

  private static void EmitHeader(Stream output, byte type, int lengthField) {
    if (lengthField < LengthEscape) {
      output.WriteByte((byte)(type | lengthField));
      return;
    }

    output.WriteByte((byte)(type | LengthEscape));
    var remaining = lengthField - LengthEscape;
    while (remaining >= 0x7F) {
      output.WriteByte((byte)(0x80 | 0x7F));
      remaining -= 0x7F;
    }
    output.WriteByte((byte)remaining);
  }

  private static int ReadLength(ReadOnlySpan<byte> payload, ref int i, int field) {
    if (field < LengthEscape)
      return field;

    var total = LengthEscape;
    while (true) {
      var b = payload[i++];
      total += b & 0x7F;
      if ((b & 0x80) == 0)
        break;
    }
    return total;
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    if (pos + MinMatch > src.Length)
      return (0, 0);

    var h = Hash3(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - MaxWindow);
    var maxLen = src.Length - pos;
    var bestLen = 0;
    var bestOff = 0;
    var steps = MaxChainSteps;

    while (candidate >= minPos && steps-- > 0) {
      if (bestLen == 0 || src[candidate + bestLen] == src[pos + bestLen]) {
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
    var h = Hash3(src, pos);
    chain[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static int Hash3(byte[] data, int pos) =>
    (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));
}
