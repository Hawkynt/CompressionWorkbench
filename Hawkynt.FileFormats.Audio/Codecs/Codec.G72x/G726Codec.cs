#pragma warning disable CS1591
namespace Codec.G72x;

/// <summary>
/// ITU-T G.726 ADPCM at the 16, 24 and 40 kbit/s rates (2-, 3- and 5-bit codewords),
/// extending the shared <c>g72x.c</c> predictor / quantiser core that already backs the
/// 32 kbit/s (G.721) layer in <see cref="G72xCodec"/>. The three additional rates are a
/// faithful port of the CCITT / Sun reference (<c>g723_16.c</c>, <c>g723_24.c</c>,
/// <c>g723_40.c</c>): they reuse the identical predictor, scale-factor adaptation and
/// fixed-point arithmetic, differing only in the per-rate quantiser decision /
/// reconstruction / adaptation tables and the codeword width.
/// <para>
/// Codewords are packed MSB-first, the most significant codeword bit aligned to the most
/// significant free bit of the stream, consistent with the existing G.721 nibble packing.
/// The 3- and 5-bit codes cross byte boundaries, so packing uses a small big-endian bit
/// writer / reader. The 4-bit rate delegates to the existing G.721 core.
/// </para>
/// </summary>
public static partial class G72xCodec {

  // ── Per-rate tables (CCITT / Sun reference; witab values are pre-shifted to match
  //    the unshifted convention this core uses at the update() call site). ──────────

  // 16 kbit/s (2-bit). qtab length 1 → max codeword 3.
  private static readonly short[] Qtab72316 = [261];
  private static readonly short[] Dqlntab72316 = [116, 365, 365, 116];
  private static readonly short[] Witab72316 = [-704, 14048, 14048, -704];
  private static readonly short[] Fitab72316 = [0x000, 0xE00, 0xE00, 0x000];

  // 24 kbit/s (3-bit). qtab length 3 → max codeword 7.
  private static readonly short[] Qtab72324 = [8, 218, 331];
  private static readonly short[] Dqlntab72324 = [-2048, 135, 273, 373, 373, 273, 135, -2048];
  private static readonly short[] Witab72324 = [-128, 960, 4384, 18624, 18624, 4384, 960, -128];
  private static readonly short[] Fitab72324 = [0x000, 0x200, 0x400, 0xE00, 0xE00, 0x400, 0x200, 0x000];

  // 40 kbit/s (5-bit). qtab length 15 → max codeword 31.
  private static readonly short[] Qtab72340 = [-122, -16, 68, 139, 198, 250, 298, 339, 378, 413, 445, 475, 502, 528, 553];
  private static readonly short[] Dqlntab72340 = [
    -2048, -66, 28, 104, 169, 224, 274, 318, 358, 395, 429, 459, 488, 514, 539, 566,
    566, 539, 514, 488, 459, 429, 395, 358, 318, 274, 224, 169, 104, 28, -66, -2048,
  ];
  private static readonly short[] Witab72340 = [
    448, 448, 768, 1248, 1280, 1312, 1856, 3200, 4512, 5728, 7008, 8960, 11456, 14080, 16928, 22272,
    22272, 16928, 14080, 11456, 8960, 7008, 5728, 4512, 3200, 1856, 1312, 1280, 1248, 768, 448, 448,
  ];
  private static readonly short[] Fitab72340 = [
    0x000, 0x000, 0x000, 0x000, 0x000, 0x200, 0x200, 0x200, 0x200, 0x200, 0x400, 0x600, 0x800, 0xA00, 0xC00, 0xC00,
    0xC00, 0xC00, 0xA00, 0x800, 0x600, 0x400, 0x200, 0x200, 0x200, 0x200, 0x200, 0x000, 0x000, 0x000, 0x000, 0x000,
  ];

  /// <summary>
  /// Decodes a G.726 bitstream at the given codeword width to 16-bit linear PCM. Valid
  /// <paramref name="bitsPerSample"/> values are 2, 3, 4 and 5 (16/24/32/40 kbit/s); the
  /// 4-bit rate delegates to <see cref="DecodeG721"/>. Codewords are unpacked MSB-first.
  /// </summary>
  public static short[] DecodeG726(ReadOnlySpan<byte> data, int bitsPerSample) {
    if (bitsPerSample == 4)
      return DecodeG721(data);
    var (qtab, dqln, witab, fitab) = TablesFor(bitsPerSample);
    var signMask = 1 << (bitsPerSample - 1);
    var codeSize = bitsPerSample;

    var sampleCount = (int)((long)data.Length * 8 / bitsPerSample);
    var output = new short[sampleCount];
    var reader = new BitReader(data);
    var s = new State();
    for (var n = 0; n < sampleCount; ++n) {
      var i = reader.Read(bitsPerSample);
      output[n] = Clamp16(DecodeG726Sample(i, s, qtab, dqln, witab, fitab, signMask, codeSize));
    }
    return output;
  }

  /// <summary>
  /// Encodes 16-bit linear PCM to a G.726 bitstream at the given codeword width. Valid
  /// <paramref name="bitsPerSample"/> values are 2, 3, 4 and 5 (16/24/32/40 kbit/s); the
  /// 4-bit rate delegates to <see cref="EncodeG721"/>. Codewords are packed MSB-first;
  /// the final byte is zero-padded in its least-significant bits.
  /// </summary>
  public static byte[] EncodeG726(ReadOnlySpan<short> pcm, int bitsPerSample) {
    if (bitsPerSample == 4)
      return EncodeG721(pcm);
    var (qtab, dqln, witab, fitab) = TablesFor(bitsPerSample);
    var signMask = 1 << (bitsPerSample - 1);
    var codeSize = bitsPerSample;

    var totalBits = pcm.Length * bitsPerSample;
    var writer = new BitWriter((totalBits + 7) / 8);
    var s = new State();
    foreach (var sample in pcm) {
      var i = EncodeG726Sample(sample, s, qtab, dqln, witab, fitab, signMask, codeSize);
      writer.Write(i, bitsPerSample);
    }
    return writer.ToArray();
  }

  private static (short[] qtab, short[] dqln, short[] witab, short[] fitab) TablesFor(int bits) => bits switch {
    2 => (Qtab72316, Dqlntab72316, Witab72316, Fitab72316),
    3 => (Qtab72324, Dqlntab72324, Witab72324, Fitab72324),
    5 => (Qtab72340, Dqlntab72340, Witab72340, Fitab72340),
    _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "G.726 supports 2, 3, 4 or 5 bits per sample."),
  };

  private static int EncodeG726Sample(int sl, State s, short[] qtab, short[] dqln, short[] witab, short[] fitab, int signMask, int codeSize) {
    sl >>= 2;                                          // 16-bit → 14-bit reference domain

    var sezi = PredictorZero(s);
    var sez = sezi >> 1;
    var se = (sezi + PredictorPole(s)) >> 1;

    var d = sl - se;

    var y = StepSize(s);
    var i = Quantize(d, y, qtab, qtab.Length);

    var dq = Reconstruct(i & signMask, dqln[i], y);

    var sr = (dq < 0) ? (se - (dq & 0x3FFF)) : (se + dq);
    var dqsez = sr + sez - se;

    Update(codeSize, y, witab[i], fitab[i], dq, sr, dqsez, s);
    return i;
  }

  private static int DecodeG726Sample(int i, State s, short[] qtab, short[] dqln, short[] witab, short[] fitab, int signMask, int codeSize) {
    var sezi = PredictorZero(s);
    var sez = sezi >> 1;
    var sei = sezi + PredictorPole(s);
    var se = sei >> 1;

    var y = StepSize(s);
    var dq = Reconstruct(i & signMask, dqln[i], y);

    var sr = (dq < 0) ? (se - (dq & 0x3FFF)) : (se + dq);
    var dqsez = sr - se + sez;

    Update(codeSize, y, witab[i], fitab[i], dq, sr, dqsez, s);
    return sr << 2;                                    // 14-bit → 16-bit reference domain
  }

  // ── Big-endian (MSB-first) bit packer/unpacker for sub-byte codewords. ──────────

  private sealed class BitWriter(int capacity) {
    private readonly byte[] _buffer = new byte[capacity];
    private int _bitPos;

    public void Write(int value, int bits) {
      for (var b = bits - 1; b >= 0; --b) {
        if (((value >> b) & 1) != 0)
          this._buffer[this._bitPos >> 3] |= (byte)(0x80 >> (this._bitPos & 7));
        ++this._bitPos;
      }
    }

    public byte[] ToArray() => this._buffer;
  }

  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPos;

    public int Read(int bits) {
      var value = 0;
      for (var b = 0; b < bits; ++b) {
        var bit = (this._data[this._bitPos >> 3] >> (7 - (this._bitPos & 7))) & 1;
        value = (value << 1) | bit;
        ++this._bitPos;
      }
      return value;
    }
  }
}
