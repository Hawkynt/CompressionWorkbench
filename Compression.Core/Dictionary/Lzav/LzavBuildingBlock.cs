using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzav;

/// <summary>
/// LZAV — Aleksey Vaneev's modern, byte-oriented (no bit-level packing) LZ77
/// compressor, tuned for very high throughput at a better ratio than LZ4.
/// </summary>
/// <remarks>
/// <para>
/// This is a clean-room implementation of LZAV's "data format 3" block layout,
/// written from the textual format description in the doc comments of the
/// reference header (<c>lzav_write_blk_3</c> in <c>lzav.h</c>), not a port or
/// paraphrase of the reference's C code:
/// </para>
/// <list type="bullet">
///   <item><description>A one-byte stream prefix packs the format identifier
///     (<c>3</c>) into the upper nibble and the minimum reference length
///     (<c>mref</c>, fixed at <c>6</c> here — the format allows 5 or 6) into
///     the lower nibble.</description></item>
///   <item><description>The stream is a sequence of unnumbered blocks. A block
///     header byte is <c>OOTTLLLL</c>: bits 6-7 (<c>OO</c>) are the low 2 bits
///     of a reference offset (0 for literal blocks), bits 4-5 (<c>TT</c>)
///     select the block type — <c>00</c> literal, <c>01</c>/<c>10</c>/<c>11</c>
///     a back-reference with a 10-/15-/21-bit offset carried in 1/2/3 further
///     little-endian bytes (<c>offset &gt;&gt; 2</c>) — and bits 0-3
///     (<c>LLLL</c>) hold the length field: the literal run length, or
///     <c>matchLength - mref + 1</c> for a reference, when that value is
///     1-15.</description></item>
///   <item><description>A length field of 0 means "read a base-128
///     continuation chain": each following byte contributes 7 bits (low bits
///     first) to <c>value - 16</c>, and a set high bit means "one more byte
///     follows" — mirroring the reference format's variable-length
///     extension. For reference blocks the continuation bytes follow the
///     offset bytes; for literal blocks they precede the literal
///     payload.</description></item>
///   <item><description>Reference offsets are restricted to
///     <c>[8, 2^21 - 1]</c> (the format's documented minimum offset and the
///     largest tier's plain bit width).</description></item>
/// </list>
/// <para>
/// Two reference-implementation details are deliberately not reproduced, and
/// are called out here rather than silently dropped:
/// </para>
/// <list type="bullet">
///   <item><description>The reference's offset-carry-bit reuse (spare high
///     bits of a 15-/21-bit offset's last byte are reserved to smuggle a few
///     bits of the *next* block's offset, letting the effective window grow
///     past the plain tier width) is not implemented — those spare bits are
///     always zero here, so the addressable window is a flat 2 MiB instead of
///     an opportunistically larger one. This only affects compression ratio
///     on very large inputs, never correctness.</description></item>
///   <item><description>The reference pads every stream with
///     <c>LZAV_LIT_FIN</c> (9) trailing filler literal bytes purely so its
///     SIMD copy loops may over-read past the logical end of the buffer
///     safely; that padding is a memory-safety trick for unmanaged/vectorized
///     code and has no bearing on a bounds-checked managed decoder, so it is
///     omitted.</description></item>
/// </list>
/// <para>
/// Only this building block's own round-trip is guaranteed; it is not claimed
/// to be bit-compatible with <c>lzav_compress</c>/<c>lzav_decompress</c>
/// output. The uncompressed length is carried by the standard 4-byte
/// little-endian building-block header (the reference API takes the expected
/// output length out-of-band from its caller, which this interface does not
/// support).
/// </para>
/// <para>Reference: LZAV — https://github.com/avaneev/lzav (format described
/// in the doc comments of <c>lzav_write_blk_3</c>/<c>lzav_decompress_3</c> in
/// <c>lzav.h</c>).</para>
/// </remarks>
public sealed class LzavBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzav";
  /// <inheritdoc/>
  public string DisplayName => "LZAV";
  /// <inheritdoc/>
  public string Description => "Vaneev's byte-oriented LZ77 using LZAV's real data-format-3 block layout (tiered offset bytes, base-128 length continuation)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int FormatId = 3;
  private const int MRef = 6; // Minimum reference (match) length; the format allows 5 or 6.
  private const int OfsMin = 8; // LZAV_OFS_MIN: smallest permitted reference offset.
  private const int OfsTh1 = (1 << 10) - 1; // Largest offset for the 1-offset-byte tier.
  private const int OfsTh2 = (1 << 15) - 1; // Largest offset for the 2-offset-byte tier.
  private const int OfsTh3 = (1 << 21) - 1; // Largest offset for the 3-offset-byte tier (our window cap).

  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 64;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    ms.WriteByte((byte)((FormatId << 4) | MRef));

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

      if (bestLen >= MRef) {
        if (pos > litStart)
          EmitLiteralBlock(ms, src, litStart, pos - litStart);

        EmitReferenceBlock(ms, bestLen, bestOff);

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
      EmitLiteralBlock(ms, src, litStart, src.Length - litStart);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var payload = data[4..];
    var prefix = payload[0];
    if ((prefix >> 4) != FormatId)
      throw new InvalidDataException($"LZAV: unsupported data format identifier {prefix >> 4}.");
    var mref = prefix & 0x0F;

    var dst = new byte[originalSize];
    var pos = 0;
    var i = 1;

    while (pos < originalSize) {
      var b = payload[i++];
      var type = (b >> 4) & 3;
      var nibble = b & 0x0F;

      if (type == 0) {
        var length = ReadLengthField(payload, ref i, nibble);
        payload.Slice(i, length).CopyTo(dst.AsSpan(pos));
        i += length;
        pos += length;
      } else {
        var oo = (b >> 6) & 3;
        var bytesVal = 0;
        for (var k = 0; k < type; ++k)
          bytesVal |= payload[i++] << (8 * k);
        var offset = (bytesVal << 2) | oo;

        var field = ReadLengthField(payload, ref i, nibble);
        var length = field + mref - 1;

        if (offset <= 0 || offset > pos)
          throw new InvalidDataException($"LZAV: match offset {offset} invalid at position {pos}.");

        for (var k = 0; k < length && pos < originalSize; ++k, ++pos)
          dst[pos] = dst[pos - offset];
      }
    }

    return dst;
  }

  private static void EmitLiteralBlock(Stream output, byte[] src, int start, int length) {
    var nibble = length <= 15 ? length : 0;
    output.WriteByte((byte)nibble);
    WriteLengthContinuation(output, length);
    output.Write(src, start, length);
  }

  private static void EmitReferenceBlock(Stream output, int length, int offset) {
    var type = offset switch {
      <= OfsTh1 => 1,
      <= OfsTh2 => 2,
      _ => 3,
    };

    var oo = offset & 3;
    var field = length - MRef + 1;
    var nibble = field <= 15 ? field : 0;

    output.WriteByte((byte)((oo << 6) | (type << 4) | nibble));

    var bytesVal = offset >> 2;
    for (var k = 0; k < type; ++k)
      output.WriteByte((byte)(bytesVal >> (8 * k)));

    WriteLengthContinuation(output, field);
  }

  // Writes the base-128 continuation chain for a length field that did not fit
  // the header's 4-bit nibble (i.e. field > 15): low-7-bits-first, high bit of
  // a byte set means "one more byte follows".
  private static void WriteLengthContinuation(Stream output, int field) {
    if (field <= 15)
      return;

    var remaining = field - 16;
    while (remaining > 127) {
      output.WriteByte((byte)(0x80 | (remaining & 0x7F)));
      remaining >>= 7;
    }
    output.WriteByte((byte)remaining);
  }

  private static int ReadLengthField(ReadOnlySpan<byte> payload, ref int i, int nibble) {
    if (nibble != 0)
      return nibble;

    var value = 0;
    var shift = 0;
    while (true) {
      var b = payload[i++];
      value |= (b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        break;
      shift += 7;
    }
    return 16 + value;
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    if (pos + MRef > src.Length)
      return (0, 0);

    var h = Hash3(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - OfsTh3);
    var maxLen = src.Length - pos;
    var bestLen = 0;
    var bestOff = 0;
    var steps = MaxChainSteps;

    while (candidate >= minPos && steps-- > 0) {
      var offset = pos - candidate;
      if (offset >= OfsMin && (bestLen == 0 || src[candidate + bestLen] == src[pos + bestLen])) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          ++len;

        if (len > bestLen) {
          bestLen = len;
          bestOff = offset;
          if (bestLen >= maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }

    return bestLen >= MRef ? (bestLen, bestOff) : (0, 0);
  }

  private static void InsertHash(byte[] src, int pos, int[] hashHead, int[] chain) {
    var h = Hash3(src, pos);
    chain[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static int Hash3(byte[] data, int pos) =>
    (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));
}
