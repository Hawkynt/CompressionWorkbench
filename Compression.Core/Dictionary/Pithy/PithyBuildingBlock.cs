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
/// This is a clean-room implementation of Pithy's actual tag/offset scheme,
/// written from the reference project's own source comments and constant
/// tables (<c>pithy_EmitLiteral</c>/<c>pithy_EmitCopyLessThan63</c>/
/// <c>pithy_EmitCopyGreaterThan63</c>/<c>pithy_Decompress</c> in
/// <c>pithy.c</c>) — not a port or paraphrase of that code. A varint-encoded
/// uncompressed length is followed by a stream of one-byte tags whose low 2
/// bits select a type — <c>00</c> literal, <c>01</c>/<c>10</c>/<c>11</c> a
/// copy with a 1/2/3-byte offset:
/// </para>
/// <list type="bullet">
///   <item><description>Literal: the upper 6 bits hold <c>length - 1</c> (0–59
///     direct; 60/61/62/63 mean "1/2/3/4 following little-endian bytes hold
///     <c>length - 1</c>", matching Snappy's literal length
///     encoding).</description></item>
///   <item><description>Copy-1 (offset &lt; 2048, length 4-11): the upper 6
///     bits pack a 3-bit <c>length - 4</c> and 3 more bits that are the high
///     bits of the offset; one following byte holds the offset's low 8
///     bits.</description></item>
///   <item><description>Copy-2 / Copy-3 (2-/3-byte little-endian offset
///     follows the tag): the upper 6 bits are a length field. Values 0-61
///     mean <c>length - 1</c> directly (so a 1-62 byte copy); value 62 means
///     "one more byte follows holding <c>length - 63</c>" (63-318 bytes);
///     value 63 means "two more bytes follow holding the raw 16-bit length"
///     (up to 65535 bytes). Longer matches are split into several copy tags,
///     as the reference encoder does.</description></item>
/// </list>
/// <para>
/// One reference quirk is deliberately not reproduced: <c>pithy_EmitCopy</c>'s
/// chunk-size arithmetic can leave a match length of exactly 65536-65538
/// truncated by the 16-bit length field it stores into (that count is never
/// reached through the reference's own <c>kBlockSize</c>-bounded search, so it
/// is latent rather than exercised). This implementation instead always
/// leaves each chunk's remainder at 0 or &gt;= 4, so every emitted tag's
/// length field is always in range.
/// </para>
/// <para>
/// Only this building block's own round-trip is guaranteed; it is not claimed
/// to be bit-compatible with <c>pithy_Compress</c>/<c>pithy_Decompress</c>
/// output.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>Pithy — https://github.com/johnezang/pithy</description></item>
///   <item><description>Pithy source (tag layout, <c>EmitCopy*</c>/<c>Decompress</c>) — https://github.com/johnezang/pithy/blob/master/pithy.c</description></item>
/// </list>
/// </remarks>
public sealed class PithyBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Pithy";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Pithy";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Engelhart's real Pithy tag scheme: Snappy-shaped literals plus a 3-byte-offset copy tier with 62/63 length-escape values in place of Snappy's 4-byte tier";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
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
  private const int Copy23LengthEscape1 = 62;   // Field value: one more byte holds (length - 63).
  private const int Copy23LengthEscape2 = 63;   // Field value: two more bytes hold the raw 16-bit length.
  private const int MaxCopy23Escape1Length = 63 + 255; // Largest length the one-extra-byte escape can hold.
  private const int MaxCopy23Length = 65535;    // Largest length the two-extra-byte escape can hold.

  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 64;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
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
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
          var offset = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
          i += 2;
          var len = ReadCopy23Length(data, ref i, tag >> 2);
          CopyMatch(dst, ref pos, offset, len, originalSize);
          break;
        }

        default: { // TagCopy3
          var offset = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
          i += 3;
          var len = ReadCopy23Length(data, ref i, tag >> 2);
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

  // Matches the reference's pithy_EmitCopy dispatch: chunks of 63+ bytes go
  // through the "greater than 63" tag shape (using the 62/63 length-escape
  // values), and the final remainder under 63 bytes goes through the "less
  // than 63" shape, which additionally prefers the compact copy-1 tag when
  // it is short enough and close enough. Unlike the reference, the chunk
  // size is always chosen so the remainder is 0 or >= 4, never 1-3 (see the
  // class remarks for why).
  private static void EmitCopy(Stream output, int offset, int length) {
    while (length >= 63) {
      int chunk;
      if (length <= MaxCopy23Length)
        chunk = length;
      else if (length - MaxCopy23Length < MinMatch)
        chunk = length - MinMatch;
      else
        chunk = MaxCopy23Length;

      EmitCopyGreaterThan63(output, offset, chunk);
      length -= chunk;
    }

    if (length > 0)
      EmitCopyLessThan63(output, offset, length);
  }

  private static void EmitCopyLessThan63(Stream output, int offset, int length) {
    if (length < MaxCopy1Length + 1 && offset <= MaxCopy1Offset) {
      output.WriteByte((byte)(TagCopy1 | ((length - 4) << 2) | ((offset >> 8) << 5)));
      output.WriteByte((byte)offset);
      return;
    }

    var type = offset <= MaxCopy2Offset ? TagCopy2 : TagCopy3;
    output.WriteByte((byte)(type | ((length - 1) << 2)));
    WriteCopyOffset(output, offset, type);
  }

  private static void EmitCopyGreaterThan63(Stream output, int offset, int length) {
    var type = offset <= MaxCopy2Offset ? TagCopy2 : TagCopy3;

    if (length <= MaxCopy23Escape1Length) {
      output.WriteByte((byte)(type | (Copy23LengthEscape1 << 2)));
      WriteCopyOffset(output, offset, type);
      output.WriteByte((byte)(length - 63));
    } else {
      output.WriteByte((byte)(type | (Copy23LengthEscape2 << 2)));
      WriteCopyOffset(output, offset, type);
      output.WriteByte((byte)length);
      output.WriteByte((byte)(length >> 8));
    }
  }

  private static void WriteCopyOffset(Stream output, int offset, byte type) {
    output.WriteByte((byte)offset);
    output.WriteByte((byte)(offset >> 8));
    if (type == TagCopy3)
      output.WriteByte((byte)(offset >> 16));
  }

  private static int ReadCopy23Length(ReadOnlySpan<byte> data, ref int i, int field) {
    switch (field) {
      case < Copy23LengthEscape1:
        return field + 1;
      case Copy23LengthEscape1:
        return data[i++] + 63;
      default: {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
        i += 2;
        return v;
      }
    }
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    var h = Hash4(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - MaxCopy3Offset);
    var maxLen = src.Length - pos; // EmitCopy chunks arbitrarily long matches itself.
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
