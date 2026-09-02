using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Balz;

/// <summary>
/// BALZ — Ilya Muravyov's ROLZ (reduced-offset LZ) compressor: matches are looked
/// up in a small per-context table instead of the full sliding window, and every
/// symbol is entropy-coded with a binary adaptive arithmetic coder.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on the publicly documented BALZ design (see references below): the
/// previous output byte selects one of 256 context tables, each a fixed-size
/// most-recently-used ring of earlier positions that shared that same preceding
/// byte. At each position the longest match against any entry in the current
/// context's table is found; a "is-match" bit (adaptively coded), the winning
/// table slot index, and the match length are then arithmetic-coded, or — for a
/// literal — the "is-match" bit followed by the raw byte, each bit modeled by its
/// own adaptive probability. Because candidates are restricted to one context's
/// table, offsets never need to be transmitted explicitly (ROLZ's namesake
/// "reduced offset"): the decoder rebuilds the identical tables as it consumes
/// output.
/// </para>
/// <para>
/// This is a clean-room implementation written from the format description, not
/// a port of Muravyov's reference `balz.cpp`; only this building block's own
/// round-trip is guaranteed, and the uncompressed length is carried by the
/// standard 4-byte little-endian building-block header rather than BALZ's own
/// container.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description>BALZ v1.00 release thread — https://encode.su/threads/1038-balz-v1-00-new-LZ77-encoder-is-here!</description></item>
///   <item><description>ROLZ — https://en.wikipedia.org/wiki/LZ77_and_LZ78</description></item>
/// </list>
/// </remarks>
public sealed class BalzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Balz";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "BALZ";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Muravyov's ROLZ compressor: per-context match tables entropy-coded with a binary adaptive arithmetic coder";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int ContextCount = 256;
  private const int TableSize = 64;          // entries per context, must be a power of two
  private const int TableIndexBits = 6;      // log2(TableSize)
  private const int MinMatch = 3;
  private const int MaxMatch = MinMatch + 255;

  private const int ProbBits = 12;
  private const uint ProbMax = 1 << ProbBits;
  private const int ProbInit = (int)ProbMax / 2;
  private const int AdaptShift = 5;

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
    var enc = new ArithmeticEncoder(ms);
    var model = new ProbabilityModel();

    var tables = new int[ContextCount][];
    var heads = new int[ContextCount];
    for (var c = 0; c < ContextCount; ++c) {
      tables[c] = new int[TableSize];
      Array.Fill(tables[c], -1);
    }

    var ctx = 0;
    var i = 0;
    while (i < src.Length) {
      var table = tables[ctx];
      var bestLen = 0;
      var bestSlot = 0;
      var maxLen = Math.Min(MaxMatch, src.Length - i);

      for (var slot = 0; slot < TableSize; ++slot) {
        var cand = table[slot];
        if (cand < 0)
          continue;

        var len = 0;
        while (len < maxLen && src[cand + len] == src[i + len])
          ++len;

        if (len > bestLen) {
          bestLen = len;
          bestSlot = slot;
          if (bestLen == maxLen)
            break;
        }
      }

      table[heads[ctx]] = i;
      heads[ctx] = (heads[ctx] + 1) & (TableSize - 1);

      if (bestLen >= MinMatch) {
        enc.EncodeBit(1, ref model.IsMatch);
        EncodeBits(enc, (uint)bestSlot, TableIndexBits, model.SlotBits);
        EncodeBits(enc, (uint)(bestLen - MinMatch), 8, model.LengthBits);
        ctx = src[i + bestLen - 1];
        i += bestLen;
      } else {
        enc.EncodeBit(0, ref model.IsMatch);
        EncodeBits(enc, src[i], 8, model.LiteralBits);
        ctx = src[i];
        ++i;
      }
    }

    enc.Flush();
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
    var dec = new ArithmeticDecoder(ms);
    var model = new ProbabilityModel();

    var tables = new int[ContextCount][];
    var heads = new int[ContextCount];
    for (var c = 0; c < ContextCount; ++c) {
      tables[c] = new int[TableSize];
      Array.Fill(tables[c], -1);
    }

    var dst = new byte[originalSize];
    var ctx = 0;
    var pos = 0;

    while (pos < originalSize) {
      var table = tables[ctx];
      var isMatch = dec.DecodeBit(ref model.IsMatch);

      if (isMatch == 1) {
        var slot = (int)DecodeBits(dec, TableIndexBits, model.SlotBits);
        var len = (int)DecodeBits(dec, 8, model.LengthBits) + MinMatch;
        var srcPos = table[slot];
        if (srcPos < 0)
          throw new InvalidDataException($"BALZ: empty ROLZ slot {slot} referenced at position {pos}.");

        table[heads[ctx]] = pos;
        heads[ctx] = (heads[ctx] + 1) & (TableSize - 1);

        var lastByte = 0;
        for (var k = 0; k < len && pos < originalSize; ++k, ++pos) {
          lastByte = dst[pos] = dst[srcPos + k];
        }
        ctx = lastByte;
      } else {
        var literal = (byte)DecodeBits(dec, 8, model.LiteralBits);
        table[heads[ctx]] = pos;
        heads[ctx] = (heads[ctx] + 1) & (TableSize - 1);
        dst[pos++] = literal;
        ctx = literal;
      }
    }

    return dst;
  }

  private static void EncodeBits(ArithmeticEncoder enc, uint value, int bitCount, int[] probs) {
    for (var b = bitCount - 1; b >= 0; --b)
      enc.EncodeBit((int)(value >> b) & 1, ref probs[bitCount - 1 - b]);
  }

  private static uint DecodeBits(ArithmeticDecoder dec, int bitCount, int[] probs) {
    var value = 0u;
    for (var b = bitCount - 1; b >= 0; --b)
      value = (value << 1) | (uint)dec.DecodeBit(ref probs[bitCount - 1 - b]);
    return value;
  }

  /// <summary>Adaptive bit-probability contexts, one array of positional probabilities per symbol kind.</summary>
  private sealed class ProbabilityModel {
    public int IsMatch = ProbInit;
    public readonly int[] SlotBits = CreateProbs(TableIndexBits);
    public readonly int[] LengthBits = CreateProbs(8);
    public readonly int[] LiteralBits = CreateProbs(8);

    private static int[] CreateProbs(int count) {
      var probs = new int[count];
      Array.Fill(probs, ProbInit);
      return probs;
    }
  }

  /// <summary>
  /// 32-bit binary arithmetic encoder with 12-bit adaptive probabilities. <c>prob</c>
  /// tracks the probability of a 0-bit; a 0-bit narrows to the upper part of the
  /// range, a 1-bit to the lower part, and each observed bit nudges the estimate
  /// by <c>1/16</c> of the remaining distance to its extreme.
  /// </summary>
  private sealed class ArithmeticEncoder(Stream output) {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;

    public void EncodeBit(int bit, ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * (uint)prob / ProbMax) - 1;
      if (mid >= _high)
        mid = _high - 1;

      if (bit == 0) {
        _high = mid;
        prob += ((int)ProbMax - prob) >> AdaptShift;
      } else {
        _low = mid + 1;
        prob -= prob >> AdaptShift;
      }

      while ((_low ^ _high) < (1u << 24)) {
        output.WriteByte((byte)(_high >> 24));
        _low <<= 8;
        _high = (_high << 8) | 0xFF;
      }
    }

    public void Flush() {
      for (var i = 0; i < 4; ++i) {
        output.WriteByte((byte)(_high >> 24));
        _high <<= 8;
      }
    }
  }

  /// <summary>Decoder counterpart of <see cref="ArithmeticEncoder"/>.</summary>
  private sealed class ArithmeticDecoder {
    private readonly Stream _input;
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private uint _code;

    public ArithmeticDecoder(Stream input) {
      _input = input;
      for (var i = 0; i < 4; ++i)
        _code = (_code << 8) | (uint)Math.Max(0, input.ReadByte());
    }

    public int DecodeBit(ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * (uint)prob / ProbMax) - 1;
      if (mid >= _high)
        mid = _high - 1;

      int bit;
      if (_code <= mid) {
        bit = 0;
        _high = mid;
        prob += ((int)ProbMax - prob) >> AdaptShift;
      } else {
        bit = 1;
        _low = mid + 1;
        prob -= prob >> AdaptShift;
      }

      while ((_low ^ _high) < (1u << 24)) {
        var next = _input.ReadByte();
        _code = (_code << 8) | (uint)(next < 0 ? 0xFF : next);
        _low <<= 8;
        _high = (_high << 8) | 0xFF;
      }

      return bit;
    }
  }
}
