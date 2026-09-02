using System.Buffers.Binary;
using System.Numerics;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Dictionary.Crush;

/// <summary>
/// Crush — Ilya Muravyov's fast LZ77 compressor, distinguished from simpler LZ77
/// variants (such as BriefLZ) by parsing the input with a bounded dynamic-program
/// instead of a purely greedy longest-match choice.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented Crush design (see references below): every
/// token is prefixed by one MSB-first tag bit — <c>0</c> for a literal byte, <c>1</c>
/// for a back-reference carrying an Elias-gamma coded length (<c>length - MinMatch
/// + 1</c>) followed by a fixed 16-bit offset (window up to 64 KiB).
/// </para>
/// <para>
/// Because the offset field has a fixed bit cost, the encoding cost of a match
/// depends only on its length, and that cost is a step function of Elias-gamma's
/// length-prefix brackets (<c>[1,1], [2,3], [4,7], [8,15], ...</c> — all lengths
/// within a bracket cost the same). The parser therefore runs a backward
/// dynamic program that, at every position, considers stopping at a literal or at
/// the longest reachable length in each gamma bracket (found via a hash-chain
/// match finder), and picks whichever local choice minimizes the total bit cost
/// to the end of the input — a bounded but genuine optimal parse over the
/// bracket-boundary candidate lengths, rather than Crush-the-family's usual greedy
/// longest-match choice.
/// </para>
/// <para>
/// This is a clean-room implementation written from the format description, not a
/// port of Muravyov's or Ibsen's (bcrush) reference source; only this building
/// block's own round-trip is guaranteed. The uncompressed length is carried by the
/// standard 4-byte little-endian building-block header.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>bcrush (CRUSH format notes) — https://github.com/jibsen/bcrush</description></item>
///   <item><description>Elias gamma coding — https://en.wikipedia.org/wiki/Elias_gamma_coding</description></item>
/// </list>
/// </remarks>
public sealed class CrushBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Crush";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Crush";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Muravyov's LZ77 compressor: bit-tagged tokens with gamma-coded lengths, parsed with a bracket-optimal dynamic program";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinMatch = 3;
  private const int MaxWindow = 65536;
  private const int OffsetBits = 16;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MaxChainSteps = 128;

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

    var (matchLen, matchOff) = FindAllMatches(src);
    var (choiceLen, _) = OptimalParse(matchLen, n);

    var writer = new BitWriter<MsbBitOrder>(ms);
    var i = 0;
    while (i < n) {
      var len = choiceLen[i];
      if (len >= MinMatch) {
        writer.WriteBit(1);
        WriteGamma(writer, (uint)(len - MinMatch + 1));
        writer.WriteBits((uint)(matchOff[i] - 1), OffsetBits);
        i += len;
      } else {
        writer.WriteBit(0);
        writer.WriteBits(src[i], 8);
        ++i;
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
        var off = (int)reader.ReadBits(OffsetBits) + 1;

        if (off > pos)
          throw new InvalidDataException($"Crush: match offset {off} invalid at position {pos}.");

        for (var k = 0; k < len && pos < originalSize; ++k, ++pos)
          dst[pos] = dst[pos - off];
      }
    }

    return dst;
  }

  /// <summary>Finds, for every position, the longest match (if any) reachable within the window.</summary>
  private static (int[] Length, int[] Offset) FindAllMatches(byte[] src) {
    var n = src.Length;
    var length = new int[n];
    var offset = new int[n];

    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[n];

    for (var i = 0; i < n; ++i) {
      if (i + MinMatch <= n) {
        var h = Hash3(src, i);
        var candidate = hashHead[h];
        var minPos = Math.Max(0, i - MaxWindow);
        var maxLen = n - i;
        var bestLen = 0;
        var bestOff = 0;
        var steps = MaxChainSteps;

        while (candidate >= minPos && steps-- > 0) {
          if (bestLen == 0 || src[candidate + bestLen] == src[i + bestLen]) {
            var len = 0;
            while (len < maxLen && src[candidate + len] == src[i + len])
              ++len;

            if (len > bestLen) {
              bestLen = len;
              bestOff = i - candidate;
              if (bestLen >= maxLen)
                break;
            }
          }

          var prev = chain[candidate];
          if (prev >= candidate)
            break;
          candidate = prev;
        }

        if (bestLen >= MinMatch) {
          length[i] = bestLen;
          offset[i] = bestOff;
        }

        chain[i] = hashHead[h];
        hashHead[h] = i;
      }
    }

    return (length, offset);
  }

  /// <summary>
  /// Backward DP over literal-vs-match choices. At each position the candidate
  /// match lengths are the longest length reachable in each Elias-gamma cost
  /// bracket, since every length within a bracket costs the same number of bits.
  /// </summary>
  private static (int[] ChoiceLen, int[] Cost) OptimalParse(int[] matchLen, int n) {
    const int literalCost = 1 + 8;
    var cost = new int[n + 1];
    var choiceLen = new int[n];

    for (var i = n - 1; i >= 0; --i) {
      var best = literalCost + cost[i + 1];
      var bestLen = 0;

      var maxLen = matchLen[i];
      if (maxLen >= MinMatch) {
        var maxV = maxLen - MinMatch + 1;
        var upper = 1;
        while (true) {
          var v = Math.Min(maxV, upper);
          var len = v + MinMatch - 1;
          var candidateCost = 1 + GammaBits((uint)v) + OffsetBits + cost[i + len];
          if (candidateCost < best) {
            best = candidateCost;
            bestLen = len;
          }
          if (v == maxV)
            break;
          upper = upper * 2 + 1;
        }
      }

      cost[i] = best;
      choiceLen[i] = bestLen;
    }

    return (choiceLen, cost);
  }

  private static int GammaBits(uint v) => 2 * (31 - BitOperations.LeadingZeroCount(v)) + 1;

  private static int Hash3(byte[] data, int pos) =>
    (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));

  private static void WriteGamma(BitWriter<MsbBitOrder> writer, uint value) {
    var bits = 31 - BitOperations.LeadingZeroCount(value);
    for (var i = 0; i < bits; ++i)
      writer.WriteBit(0);
    for (var i = bits; i >= 0; --i)
      writer.WriteBit((int)(value >> i) & 1);
  }

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
