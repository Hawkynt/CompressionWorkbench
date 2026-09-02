using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lizard;

/// <summary>
/// Lizard (formerly LZ5) — Przemysław Skibiński's LZ4 derivative. The reference
/// codec offers several parsers ranging from an LZ4-compatible fast mode up to
/// full-search modes with a Huffman/FSE entropy stage for maximum ratio; this
/// building block implements the baseline LZ4-compatible block parser (the
/// fastest of Lizard's modes) — the entropy-coded high-ratio modes are out of
/// scope for a single building block.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented LZ4/Lizard block format (see references
/// below): the stream is a sequence of sequences, each starting with a token
/// byte whose high nibble is the literal-run length and low nibble is
/// <c>match length - MinMatch</c>. A nibble value of 15 means "add the following
/// byte(s)", each contributing 0–255 more, continuing while a byte reads 255 —
/// the same additive-byte extension LZ4 uses. Literal bytes follow the token,
/// then (unless this is the final, match-less sequence) a 2-byte little-endian
/// offset and any match-length extension bytes.
/// </para>
/// <para>
/// This is a clean-room implementation written from the format description, not
/// a port of the reference `liblizard`/`lz4` C sources; only this building
/// block's own round-trip is guaranteed. Unlike Lizard's frame container (magic,
/// flags, content size, header checksum, per-block sizes), the uncompressed
/// length here is carried by the standard 4-byte little-endian building-block
/// header and the payload is a single block.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>Lizard — https://github.com/inikep/lizard</description></item>
///   <item><description>Lizard block format — https://github.com/inikep/lizard/blob/lizard/doc/lizard_Block_format.md</description></item>
///   <item><description>LZ4 (predecessor block format) — https://github.com/lz4/lz4</description></item>
/// </list>
/// </remarks>
public sealed class LizardBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lizard";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Lizard";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Skibiński's LZ4-derived compressor (formerly LZ5); this block implements its baseline LZ4-compatible fast parser";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinMatch = 4;
  private const int LastLiteralsMinLength = 5; // LZ4-style: final sequence must leave room for a match-less tail
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxWindow = 65536;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var src = data.ToArray();
    var n = src.Length;
    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);

    var anchor = 0;
    var pos = 0;
    var matchLimit = n - LastLiteralsMinLength;

    while (pos < matchLimit) {
      var (bestLen, bestOff) = FindMatch(src, pos, hashHead, matchLimit + LastLiteralsMinLength);
      if (bestLen < MinMatch) {
        InsertHash(src, pos, hashHead);
        ++pos;
        continue;
      }

      EmitSequence(ms, src, anchor, pos, bestOff, bestLen);

      var end = pos + bestLen;
      for (var i = pos; i < end && i + 3 < n; ++i)
        InsertHash(src, i, hashHead);

      pos = end;
      anchor = pos;
    }

    EmitFinalLiterals(ms, src, anchor, n);
    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var payload = data[4..];
    var dst = new byte[originalSize];
    var pos = 0;
    var i = 0;

    while (pos < originalSize) {
      var token = payload[i++];
      var litLen = ReadExtendedLength(payload, ref i, token >> 4);

      payload.Slice(i, litLen).CopyTo(dst.AsSpan(pos));
      i += litLen;
      pos += litLen;

      if (i >= payload.Length)
        break; // final, match-less sequence

      var offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[i..]);
      i += 2;

      var matchLen = ReadExtendedLength(payload, ref i, token & 0xF) + MinMatch;

      if (offset <= 0 || offset > pos)
        throw new InvalidDataException($"Lizard: match offset {offset} invalid at position {pos}.");

      for (var k = 0; k < matchLen && pos < originalSize; ++k, ++pos)
        dst[pos] = dst[pos - offset];
    }

    return dst;
  }

  private static void EmitSequence(Stream output, byte[] src, int litStart, int matchStart, int offset, int matchLen) {
    var litLen = matchStart - litStart;
    var mlCode = matchLen - MinMatch;

    var litNibble = Math.Min(litLen, 15);
    var mlNibble = Math.Min(mlCode, 15);
    output.WriteByte((byte)((litNibble << 4) | mlNibble));

    WriteExtendedLength(output, litLen, litNibble);
    output.Write(src, litStart, litLen);

    Span<byte> off = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(off, (ushort)offset);
    output.Write(off);

    WriteExtendedLength(output, mlCode, mlNibble);
  }

  private static void EmitFinalLiterals(Stream output, byte[] src, int start, int end) {
    var litLen = end - start;
    if (litLen == 0)
      return;

    var litNibble = Math.Min(litLen, 15);
    output.WriteByte((byte)(litNibble << 4));
    WriteExtendedLength(output, litLen, litNibble);
    output.Write(src, start, litLen);
  }

  private static void WriteExtendedLength(Stream output, int actual, int nibble) {
    if (nibble < 15)
      return;

    var remaining = actual - 15;
    while (remaining >= 255) {
      output.WriteByte(255);
      remaining -= 255;
    }
    output.WriteByte((byte)remaining);
  }

  private static int ReadExtendedLength(ReadOnlySpan<byte> payload, ref int i, int nibble) {
    if (nibble < 15)
      return nibble;

    var total = 15;
    int extra;
    do {
      extra = payload[i++];
      total += extra;
    } while (extra == 255);

    return total;
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int limit) {
    if (pos + MinMatch > src.Length)
      return (0, 0);

    var h = Hash4(src, pos);
    var candidate = hashHead[h];

    if (candidate < 0 || pos - candidate > MaxWindow ||
        src[candidate] != src[pos] || src[candidate + 1] != src[pos + 1] ||
        src[candidate + 2] != src[pos + 2] || src[candidate + 3] != src[pos + 3])
      return (0, 0);

    var maxLen = Math.Min(limit, src.Length) - pos;
    var len = 4;
    while (len < maxLen && src[candidate + len] == src[pos + len])
      ++len;

    return (len, pos - candidate);
  }

  private static void InsertHash(byte[] src, int pos, int[] hashHead) {
    if (pos + 4 > src.Length)
      return;
    hashHead[Hash4(src, pos)] = pos;
  }

  private static int Hash4(byte[] data, int pos) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) * 2654435761u) >> (32 - HashBits));
}
