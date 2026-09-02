using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Dictionary.BriefLz;

/// <summary>
/// BriefLZ — Jørgen Ibsen's byte-oriented LZ77 compressor with an interleaved
/// single-bit-per-token tag stream and Elias-gamma coded match parameters.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented BriefLZ design (see references below): every
/// token is prefixed by one MSB-first tag bit — <c>0</c> for a literal byte that
/// follows verbatim, <c>1</c> for a back-reference. A back-reference then carries
/// two Elias-gamma coded values: <c>length - MinMatch + 1</c> (so the smallest
/// representable match encodes as gamma value 1) and the offset (distance to the
/// match, &gt;= 1). Matches are found with a hash-chain over 3-byte hashes,
/// resolved greedily (longest match at each position, no lazy/optimal parsing).
/// </para>
/// <para>
/// This is a clean-room implementation written from the format description; it is
/// not bit-compatible with the reference <c>blz</c> container (which additionally
/// wraps the stream in a checksummed header) — only this building block's own
/// round-trip is guaranteed. The uncompressed length is instead carried by the
/// standard 4-byte little-endian building-block header.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>BriefLZ — https://github.com/jibsen/brieflz</description></item>
///   <item><description>Elias gamma coding — https://en.wikipedia.org/wiki/Elias_gamma_coding</description></item>
/// </list>
/// </remarks>
public sealed class BriefLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_BriefLz";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BriefLZ";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Ibsen's byte-oriented LZ77 with an interleaved tag-bit stream and Elias-gamma coded match length/offset";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinMatch = 3;
  private const int MaxMatch = int.MaxValue - MinMatch; // gamma has no practical upper bound
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 128;
  private const int MaxWindow = 1 << 20;

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
    var writer = new BitWriter<MsbBitOrder>(ms);

    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[src.Length];

    var pos = 0;
    while (pos < src.Length) {
      var (bestLen, bestOff) = FindMatch(src, pos, hashHead, chain);

      if (bestLen >= MinMatch) {
        writer.WriteBit(1);
        WriteGamma(writer, (uint)(bestLen - MinMatch + 1));
        WriteGamma(writer, (uint)bestOff);

        var end = Math.Min(pos + bestLen, src.Length - 2);
        for (var i = pos; i < end; ++i)
          InsertHash(src, i, hashHead, chain);

        pos += bestLen;
      } else {
        writer.WriteBit(0);
        writer.WriteBits(src[pos], 8);

        if (pos < src.Length - 2)
          InsertHash(src, pos, hashHead, chain);

        ++pos;
      }
    }

    writer.FlushBits();
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

    using var ms = new MemoryStream(data[4..].ToArray());
    var reader = new BitReader<MsbBitOrder>(ms);
    var dst = new byte[originalSize];
    var pos = 0;

    while (pos < originalSize) {
      var tag = reader.ReadBit();
      if (tag == 0) {
        dst[pos++] = (byte)reader.ReadBits(8);
      } else {
        var len = (int)ReadGamma(reader) + MinMatch - 1;
        var off = (int)ReadGamma(reader);

        if (off <= 0 || off > pos)
          throw new InvalidDataException($"BriefLZ: match offset {off} invalid at position {pos}.");

        for (var i = 0; i < len && pos < originalSize; ++i, ++pos)
          dst[pos] = dst[pos - off];
      }
    }

    return dst;
  }

  private static (int Length, int Offset) FindMatch(byte[] src, int pos, int[] hashHead, int[] chain) {
    if (pos + MinMatch > src.Length)
      return (0, 0);

    var h = Hash3(src, pos);
    var candidate = hashHead[h];
    var minPos = Math.Max(0, pos - MaxWindow);
    var maxLen = Math.Min(MaxMatch, src.Length - pos);
    var bestLen = 0;
    var bestOff = 0;
    var steps = MaxChainSteps;

    while (candidate >= minPos && steps-- > 0) {
      if (src[candidate + bestLen] == src[pos + bestLen] || bestLen == 0) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          ++len;

        if (len > bestLen) {
          bestLen = len;
          bestOff = pos - candidate;
          if (len >= maxLen)
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

  /// <summary>Writes an Elias-gamma code for <paramref name="value"/> (&gt;= 1), MSB-first.</summary>
  private static void WriteGamma(BitWriter<MsbBitOrder> writer, uint value) {
    var bits = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
    for (var i = 0; i < bits; ++i)
      writer.WriteBit(0);
    for (var i = bits; i >= 0; --i)
      writer.WriteBit((int)(value >> i) & 1);
  }

  /// <summary>Reads an Elias-gamma coded value (&gt;= 1), MSB-first.</summary>
  private static uint ReadGamma(BitReader<MsbBitOrder> reader) {
    var zeros = 0;
    while (reader.ReadBit() == 0)
      ++zeros;

    var value = 1u;
    for (var i = 0; i < zeros; ++i)
      value = (value << 1) | (uint)reader.ReadBit();

    return value;
  }
}
