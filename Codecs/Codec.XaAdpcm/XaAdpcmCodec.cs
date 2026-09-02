#pragma warning disable CS1591
namespace Codec.XaAdpcm;

/// <summary>
/// CD-ROM XA / PlayStation streaming ADPCM (Green-Book / PSX XA-ADPCM) encoder and
/// decoder. XA audio is carried in fixed 128-byte <em>sound groups</em>:
/// <list type="bullet">
///   <item>16 header bytes — the per-unit sound parameters, stored twice redundantly.
///     Bytes 0–3 repeat params 0–3, bytes 4–7 hold params 0–3, bytes 8–11 hold
///     params 4–7, bytes 12–15 repeat params 4–7. The canonical read takes bytes
///     4..11 → the parameter for sound units 0..7.</item>
///   <item>112 data bytes — 28 four-byte columns. In 4-bit mode each column carries
///     one nibble for each of the 8 units (two units per byte); in 8-bit mode each
///     column carries one byte for each of 4 units.</item>
/// </list>
/// A parameter byte holds <c>shift</c> in its low nibble (0..12) and <c>filter</c> in
/// bits 4–5 (0..3). The predictor shares the SPU/VAG heritage but uses only the first
/// four filters: <c>K0 = {0, 60, 115, 98}</c>, <c>K1 = {0, 0, -52, -55}</c> (scaled by
/// 1/64). Each sample is reconstructed as
/// <c>s = (signExtend(code) &lt;&lt; 12) &gt;&gt; shift</c>, then
/// <c>s += (h1*K0[f] + h2*K1[f] + 32) &gt;&gt; 6</c>, clamped to <see cref="short"/>; the
/// two per-channel histories then shift forward.
/// <para>Stereo content interleaves channels by unit parity: even units feed the LEFT
/// channel, odd units the RIGHT (each with its own predictor history). Mono content
/// plays all units sequentially.</para>
/// </summary>
public static class XaAdpcmCodec {

  /// <summary>Predictor coefficient 0 (a.k.a. K1 elsewhere), indexed by filter (scaled by 1/64).</summary>
  private static readonly int[] FilterK0 = [0, 60, 115, 98];

  /// <summary>Predictor coefficient 1 (a.k.a. K2 elsewhere), indexed by filter (scaled by 1/64).</summary>
  private static readonly int[] FilterK1 = [0, 0, -52, -55];

  /// <summary>Size in bytes of one XA sound group.</summary>
  public const int SoundGroupSize = 128;

  /// <summary>Header bytes preceding the 112 data bytes in a sound group.</summary>
  public const int HeaderSize = 16;

  /// <summary>Sound units per group in 4-bit mode.</summary>
  public const int UnitsPerGroup4Bit = 8;

  /// <summary>Sound units per group in 8-bit mode.</summary>
  public const int UnitsPerGroup8Bit = 4;

  /// <summary>Decoded PCM samples produced by each sound unit.</summary>
  public const int SamplesPerUnit = 28;

  /// <summary>Per-channel predictor state carried across sound units.</summary>
  public struct History {
        /// <summary>
    /// Provides the h 1 value.
    /// </summary>
public int H1;
        /// <summary>
    /// Provides the h 2 value.
    /// </summary>
public int H2;
  }

  /// <summary>
  /// Decodes one 128-byte sound group in 4-bit mode into <paramref name="output"/>,
  /// advancing the per-channel histories. When <paramref name="stereo"/> is set the
  /// output is interleaved L/R and the eight units route even→<paramref name="left"/>,
  /// odd→<paramref name="right"/>; otherwise all eight units feed
  /// <paramref name="left"/> and are written sequentially. Returns the number of PCM
  /// samples written (<c>8*28</c> mono = 224; stereo writes the same count split as
  /// 4 units per channel × 2 interleaved).
  /// </summary>
  public static int DecodeGroup(
      ReadOnlySpan<byte> group, bool stereo, ref History left, ref History right, Span<short> output) {
    if (group.Length < SoundGroupSize)
      throw new ArgumentException("Sound group must be 128 bytes.", nameof(group));

    if (stereo) {
      // Decode the four left + four right units into per-channel scratch, then weave.
      Span<short> leftSamples = stackalloc short[(UnitsPerGroup4Bit / 2) * SamplesPerUnit];
      Span<short> rightSamples = stackalloc short[(UnitsPerGroup4Bit / 2) * SamplesPerUnit];
      var li = 0;
      var ri = 0;
      for (var u = 0; u < UnitsPerGroup4Bit; ++u) {
        if ((u & 1) == 0) {
          DecodeUnit4Bit(group, u, ref left, leftSamples.Slice(li, SamplesPerUnit));
          li += SamplesPerUnit;
        } else {
          DecodeUnit4Bit(group, u, ref right, rightSamples.Slice(ri, SamplesPerUnit));
          ri += SamplesPerUnit;
        }
      }

      var written = 0;
      for (var s = 0; s < li; ++s) {
        output[written++] = leftSamples[s];
        output[written++] = rightSamples[s];
      }
      return written;
    }

    var pos = 0;
    for (var u = 0; u < UnitsPerGroup4Bit; ++u) {
      DecodeUnit4Bit(group, u, ref left, output.Slice(pos, SamplesPerUnit));
      pos += SamplesPerUnit;
    }
    return pos;
  }

  private static void DecodeUnit4Bit(ReadOnlySpan<byte> group, int unit, ref History hist, Span<short> output) {
    var param = group[4 + unit];          // params for units 0..7 live at bytes 4..11
    var shift = param & 0x0F;
    var filter = (param >> 4) & 0x03;
    if (shift > 12) shift = 12;            // hardware clamps illegal shifts

    var k0 = FilterK0[filter];
    var k1 = FilterK1[filter];
    var h1 = hist.H1;
    var h2 = hist.H2;

    for (var i = 0; i < SamplesPerUnit; ++i) {
      var dataByte = group[HeaderSize + i * 4 + (unit >> 1)];
      var nibble = (unit & 1) == 0 ? dataByte & 0x0F : (dataByte >> 4) & 0x0F;
      var s = SignExtend4(nibble) << 12;
      s >>= shift;
      s += (h1 * k0 + h2 * k1 + 32) >> 6;
      s = Clamp16(s);
      output[i] = (short)s;
      h2 = h1;
      h1 = s;
    }

    hist.H1 = h1;
    hist.H2 = h2;
  }

  private static void DecodeUnit8Bit(ReadOnlySpan<byte> group, int unit, ref History hist, Span<short> output) {
    var param = group[4 + unit];          // params for units 0..3 live at bytes 4..7
    var shift = param & 0x0F;
    var filter = (param >> 4) & 0x03;
    if (shift > 12) shift = 12;

    var k0 = FilterK0[filter];
    var k1 = FilterK1[filter];
    var h1 = hist.H1;
    var h2 = hist.H2;

    for (var i = 0; i < SamplesPerUnit; ++i) {
      var code = group[HeaderSize + i * 4 + unit]; // one byte per unit per column
      var s = SignExtend8(code) << 8;              // 8-bit codes occupy the high byte
      s >>= shift;
      s += (h1 * k0 + h2 * k1 + 32) >> 6;
      s = Clamp16(s);
      output[i] = (short)s;
      h2 = h1;
      h1 = s;
    }

    hist.H1 = h1;
    hist.H2 = h2;
  }

  /// <summary>
  /// Decodes a 4-bit XA-ADPCM stream of whole 128-byte sound groups to interleaved
  /// 16-bit PCM. A trailing partial group (shorter than 128 bytes) is ignored. Stereo
  /// output is L/R interleaved; mono output is sequential.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> groups, bool stereo) {
    var groupCount = groups.Length / SoundGroupSize;
    var samplesPerGroup = UnitsPerGroup4Bit * SamplesPerUnit; // 224 (count is the same mono or stereo)
    var output = new short[groupCount * samplesPerGroup];

    var left = new History();
    var right = new History();
    var pos = 0;
    for (var g = 0; g < groupCount; ++g) {
      pos += DecodeGroup(groups.Slice(g * SoundGroupSize, SoundGroupSize), stereo, ref left, ref right,
        output.AsSpan(pos, samplesPerGroup));
    }
    return output;
  }

  /// <summary>
  /// Decodes an 8-bit XA-ADPCM stream of whole 128-byte sound groups to interleaved
  /// 16-bit PCM. 8-bit groups carry four sound units of 28 eight-bit codes each. Stereo
  /// routes even→left, odd→right; mono plays units sequentially.
  /// </summary>
  public static short[] Decode8Bit(ReadOnlySpan<byte> groups, bool stereo) {
    var groupCount = groups.Length / SoundGroupSize;
    var samplesPerGroup = UnitsPerGroup8Bit * SamplesPerUnit; // 112
    var output = new short[groupCount * samplesPerGroup];

    var left = new History();
    var right = new History();
    var pos = 0;
    Span<short> leftSamples = stackalloc short[(UnitsPerGroup8Bit / 2) * SamplesPerUnit];
    Span<short> rightSamples = stackalloc short[(UnitsPerGroup8Bit / 2) * SamplesPerUnit];
    for (var g = 0; g < groupCount; ++g) {
      var group = groups.Slice(g * SoundGroupSize, SoundGroupSize);
      if (stereo) {
        var li = 0;
        var ri = 0;
        for (var u = 0; u < UnitsPerGroup8Bit; ++u) {
          if ((u & 1) == 0) {
            DecodeUnit8Bit(group, u, ref left, leftSamples.Slice(li, SamplesPerUnit));
            li += SamplesPerUnit;
          } else {
            DecodeUnit8Bit(group, u, ref right, rightSamples.Slice(ri, SamplesPerUnit));
            ri += SamplesPerUnit;
          }
        }
        for (var s = 0; s < li; ++s) {
          output[pos++] = leftSamples[s];
          output[pos++] = rightSamples[s];
        }
      } else {
        for (var u = 0; u < UnitsPerGroup8Bit; ++u) {
          DecodeUnit8Bit(group, u, ref left, output.AsSpan(pos, SamplesPerUnit));
          pos += SamplesPerUnit;
        }
      }
    }
    return output;
  }

  /// <summary>
  /// Encodes interleaved 16-bit PCM into 4-bit XA-ADPCM sound groups. Each group packs
  /// 8 sound units of 28 samples; stereo de-interleaves the input and routes the four
  /// left units to even slots and the four right units to odd slots (each with its own
  /// predictor history), mono fills all eight units sequentially. Every unit is encoded
  /// by brute-forcing all four filters and every legal shift and keeping the lowest
  /// reconstruction error. The final group is zero-padded. The redundant parameter
  /// bytes are written in both copies so the output round-trips through any conformant
  /// reader.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, bool stereo) {
    var channels = stereo ? 2 : 1;
    var frameCount = interleaved.Length / channels;
    if (frameCount == 0) return [];

    // Samples each channel contributes per group: 4 units × 28 (stereo) or 8 × 28 (mono).
    var unitsPerChannel = stereo ? UnitsPerGroup4Bit / 2 : UnitsPerGroup4Bit;
    var samplesPerChannelPerGroup = unitsPerChannel * SamplesPerUnit;
    var groupCount = (frameCount + samplesPerChannelPerGroup - 1) / samplesPerChannelPerGroup;

    var output = new byte[groupCount * SoundGroupSize];

    var left = new History();
    var right = new History();
    Span<short> unitSource = stackalloc short[SamplesPerUnit];
    Span<int> bestCodes = stackalloc int[SamplesPerUnit];

    for (var g = 0; g < groupCount; ++g) {
      var groupStart = g * SoundGroupSize;
      for (var u = 0; u < UnitsPerGroup4Bit; ++u) {
        // Channel + intra-channel unit index for this slot.
        var channel = stereo ? u & 1 : 0;
        var channelUnit = stereo ? u >> 1 : u;
        ref var hist = ref (channel == 0 ? ref left : ref right);

        // Gather this unit's source frames (zero-padded past the end).
        var baseFrame = (g * unitsPerChannel + channelUnit) * SamplesPerUnit;
        for (var i = 0; i < SamplesPerUnit; ++i) {
          var frame = baseFrame + i;
          unitSource[i] = frame < frameCount ? interleaved[frame * channels + channel] : (short)0;
        }

        EncodeUnit4Bit(unitSource, ref hist, bestCodes, out var filter, out var shift);

        var param = (byte)((filter << 4) | shift);
        // Stored twice: bytes 4..11 are the live copy; 0..3 / 12..15 are redundant.
        if (u < 4) {
          output[groupStart + u] = param;          // redundant copy of params 0..3
          output[groupStart + 4 + u] = param;       // live params 0..3
        } else {
          output[groupStart + 4 + u] = param;       // live params 4..7
          output[groupStart + 8 + u] = param;       // redundant copy of params 4..7
        }

        for (var i = 0; i < SamplesPerUnit; ++i) {
          var idx = groupStart + HeaderSize + i * 4 + (u >> 1);
          if ((u & 1) == 0)
            output[idx] = (byte)((output[idx] & 0xF0) | (bestCodes[i] & 0x0F));
          else
            output[idx] = (byte)((output[idx] & 0x0F) | ((bestCodes[i] & 0x0F) << 4));
        }
      }
    }

    return output;
  }

  private static void EncodeUnit4Bit(
      ReadOnlySpan<short> source, ref History hist, Span<int> bestCodes, out int bestFilter, out int bestShift) {
    var bestError = long.MaxValue;
    bestFilter = 0;
    bestShift = 0;
    var bestH1 = hist.H1;
    var bestH2 = hist.H2;
    Span<int> tryCodes = stackalloc int[SamplesPerUnit];

    for (var filter = 0; filter < FilterK0.Length; ++filter) {
      var k0 = FilterK0[filter];
      var k1 = FilterK1[filter];
      for (var shift = 0; shift <= 12; ++shift) {
        var h1 = hist.H1;
        var h2 = hist.H2;
        long error = 0;

        for (var i = 0; i < SamplesPerUnit; ++i) {
          var predicted = (h1 * k0 + h2 * k1 + 32) >> 6;
          var residual = source[i] - predicted;

          var quant = (residual << shift) + (1 << 11); // round
          quant >>= 12;
          if (quant > 7) quant = 7;
          else if (quant < -8) quant = -8;
          tryCodes[i] = quant & 0x0F;

          var s = SignExtend4(quant & 0x0F) << 12;
          s >>= shift;
          s += predicted;
          s = Clamp16(s);

          var diff = (long)s - source[i];
          error += diff * diff;

          h2 = h1;
          h1 = s;
        }

        if (error >= bestError)
          continue;

        bestError = error;
        bestFilter = filter;
        bestShift = shift;
        bestH1 = h1;
        bestH2 = h2;
        tryCodes.CopyTo(bestCodes);
        if (error == 0) break;
      }
    }

    hist.H1 = bestH1;
    hist.H2 = bestH2;
  }

  /// <summary>Sign-extends a 4-bit value (0..15) to the full signed range -8..7.</summary>
  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  /// <summary>Sign-extends an 8-bit value (0..255) to -128..127.</summary>
  private static int SignExtend8(int code) => (sbyte)code;

  private static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;
}
