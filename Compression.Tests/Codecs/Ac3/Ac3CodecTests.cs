#pragma warning disable CS1591
using Codec.Ac3;

namespace Compression.Tests.Codecs.Ac3;

/// <summary>
/// Pins the clean-room AC-3 (ATSC A/52) decoder. A hand-crafted "silence" sync frame (acmod 2,
/// no coupling, snroffst forced so every bap = 0 and dithflag = 0) decodes to exact zeros, which
/// lets the per-syncframe / multi-frame sample counts and channel layout be checked deterministically
/// without depending on the masking-curve arithmetic. Dedicated unit tests pin the exponent grouping,
/// mantissa grouping and bit-allocation masking against hand-computed values.
/// </summary>
[TestFixture]
public class Ac3CodecTests {

  // ── A minimal MSB-first bit writer mirroring the decoder's read order ──────────────────
  private sealed class BitWriter {
    private readonly List<bool> _bits = [];
    public void Put(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        this._bits.Add(((value >> i) & 1) != 0);
    }
    public void Flag(bool b) => this._bits.Add(b);
    public int BitCount => this._bits.Count;
    public byte[] ToBytes(int totalBytes) {
      var bytes = new byte[totalBytes];
      for (var i = 0; i < this._bits.Count; ++i)
        if (this._bits[i])
          bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
      return bytes;
    }
  }

  /// <summary>
  /// Builds one valid legacy AC-3 silence sync frame: acmod 2 (stereo, 2 full-bandwidth channels),
  /// no LFE, no coupling, no rematrixing. Block 0 sets exponent strategy D15 for both channels with a
  /// flat (max-attenuation) exponent envelope, the bit-allocation parameters and an snroffst that
  /// drives every bap to 0; dithflag = 0 means every mantissa is an exact zero. Blocks 1..5 reuse the
  /// exponent strategy. The result decodes to all-zero PCM.
  /// </summary>
  public static byte[] BuildSilenceFrame(int fscod = 0, int frmsizecod = 12) {
    const int acmod = 2;
    var w = new BitWriter();

    // syncinfo
    w.Put(0x0B77, 16);            // sync word
    w.Put(0, 16);                 // crc1
    w.Put(fscod, 2);              // fscod
    w.Put(frmsizecod, 6);         // frmsizecod
    // BSI
    w.Put(8, 5);                  // bsid (legacy)
    w.Put(0, 3);                  // bsmod
    w.Put(acmod, 3);              // acmod = 2
    // acmod 2 → dsurmod (2 bits); no cmixlev/surmixlev for 2/0
    w.Put(0, 2);                  // dsurmod
    w.Flag(false);                // lfeon
    w.Put(31, 5);                 // dialnorm
    w.Flag(false);                // compre
    w.Flag(false);                // langcode
    w.Flag(false);                // audprodie
    w.Flag(false);                // copyrightb
    w.Flag(false);                // origbs
    w.Flag(false);                // timecod1e
    w.Flag(false);                // timecod2e
    w.Flag(false);                // addbsie

    for (var blk = 0; blk < 6; ++blk)
      WriteSilenceBlock(w, blk, acmod, nfchans: 2);

    // Determine frame size from the table and pad to it.
    var probe = new byte[2048];
    var seed = w.ToBytes(2048);
    Array.Copy(seed, probe, Math.Min(seed.Length, probe.Length));
    var header = Ac3FrameHeader.TryParse(probe, 0)!.Value;
    return probe[..header.FrameSize];
  }

  private static void WriteSilenceBlock(BitWriter w, int blk, int acmod, int nfchans) {
    for (var ch = 0; ch < nfchans; ++ch) w.Flag(false);   // blksw (long blocks)
    for (var ch = 0; ch < nfchans; ++ch) w.Flag(false);   // dithflag = 0 → exact zeros for bap 0

    w.Flag(false);                                         // dynrnge

    // Coupling strategy: present in block 0, "not in use".
    if (blk == 0) {
      w.Flag(true);                                        // cplstre
      w.Flag(false);                                       // cplinu = 0
    } else {
      w.Flag(false);                                       // cplstre (reuse)
    }

    // Rematrixing (acmod 2): present in block 0 with rematstr = 0 (no rematrixing flags follow).
    if (blk == 0)
      w.Flag(false);                                       // rematstr = 0

    // Exponent strategy.
    if (blk == 0) {
      for (var ch = 0; ch < nfchans; ++ch)
        w.Put(1, 2);                                       // chexpstr = D15
    } else {
      for (var ch = 0; ch < nfchans; ++ch)
        w.Put(0, 2);                                       // reuse
    }

    // chbwcod (only when exponent strategy set this block).
    if (blk == 0) {
      for (var ch = 0; ch < nfchans; ++ch)
        w.Put(0, 6);                                       // chbwcod 0 → endmant = 37
    }

    // Exponents (D15): one absolute 4-bit exponent + grouped 7-bit words, then 2-bit gainrng.
    if (blk == 0) {
      for (var ch = 0; ch < nfchans; ++ch) {
        var nmant = 37;
        var ngrp = (nmant - 1 + 2) / 3;                    // ceil((nmant-1)/3) for D15
        w.Put(15, 4);                                      // absolute exponent = 15 (max attenuation)
        for (var g = 0; g < ngrp; ++g)
          w.Put(2 * 25 + 2 * 5 + 2, 7);                    // each delta code 2 → delta 0 (flat envelope)
        w.Put(0, 2);                                       // gainrng
      }
    }

    // Bit-allocation parametric info (block 0 sets it).
    if (blk == 0) {
      w.Flag(true);                                        // baie
      w.Put(0, 2);                                         // sdcycod
      w.Put(0, 2);                                         // fdcycod
      w.Put(0, 2);                                         // sgaincod
      w.Put(0, 2);                                         // dbpbcod
      w.Put(0, 3);                                         // floorcod
    } else {
      w.Flag(false);                                       // baie (reuse)
    }

    // snroffset (block 0 sets it): csnroffst 0 + per-channel fsnroffst 0 / fgaincod 0 → bap 0.
    if (blk == 0) {
      w.Flag(true);                                        // snroffste
      w.Put(0, 6);                                         // csnroffst = 0
      for (var ch = 0; ch < nfchans; ++ch) {
        w.Put(0, 4);                                       // fsnroffst
        w.Put(0, 3);                                       // fgaincod
      }
    } else {
      w.Flag(false);                                       // snroffste (reuse)
    }

    w.Flag(false);                                         // deltbaie
    w.Flag(false);                                         // skiple
    // No mantissas: every bap is 0, so no mantissa bits are read.
  }

  private static byte[] Decode(byte[] frame) {
    using var src = new MemoryStream(frame, writable: false);
    using var dst = new MemoryStream();
    Ac3Codec.Decompress(src, dst);
    return dst.ToArray();
  }

  [Test]
  [Category("HappyPath")]
  public void Decode_SilenceFrame_YieldsExactZeros() {
    var frame = BuildSilenceFrame();
    var pcm = Decode(frame);

    // 6 blocks × 256 samples × 2 channels × 2 bytes = 6144 bytes (1536 samples per channel).
    Assert.That(pcm, Has.Length.EqualTo(6 * 256 * 2 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void Decode_TwoSilenceFrames_DoublesSampleCount() {
    var frame = BuildSilenceFrame();
    var two = new byte[frame.Length * 2];
    Array.Copy(frame, 0, two, 0, frame.Length);
    Array.Copy(frame, 0, two, frame.Length, frame.Length);

    var pcm = Decode(two);
    Assert.That(pcm, Has.Length.EqualTo(2 * 6 * 256 * 2 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void ReadStreamInfo_ReportsStereoChannelsAndRate() {
    var frame = BuildSilenceFrame(fscod: 0, frmsizecod: 12);
    using var src = new MemoryStream(frame, writable: false);
    var info = Ac3Codec.ReadStreamInfo(src);

    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.IsEnhanced, Is.False);
    Assert.That(info.DurationSamples, Is.EqualTo(1536));
  }

  [Test]
  [Category("EdgeCase")]
  public void Decode_Truncated_StopsGracefully() {
    var frame = BuildSilenceFrame();
    var truncated = frame[..(frame.Length / 2)];
    byte[] pcm = null!;
    Assert.That(() => pcm = Decode(truncated), Throws.Nothing);
    // The truncated trailing frame is dropped → no full frame decoded.
    Assert.That(pcm.Length, Is.EqualTo(0));
  }

  [Test]
  [Category("EdgeCase")]
  public void Decode_Garbage_Throws() {
    var junk = System.Text.Encoding.ASCII.GetBytes("definitely not an AC-3 elementary stream");
    using var src = new MemoryStream(junk, writable: false);
    using var dst = new MemoryStream();
    Assert.That(() => Ac3Codec.Decompress(src, dst), Throws.TypeOf<InvalidDataException>());
  }

  // ── Exponent grouping unit test (hand-computed) ────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void DecodeExponents_GroupedD15_MatchesHandComputed() {
    // Absolute exponent 5; one grouped word with delta codes (3,1,4) → deltas (+1,-1,+2).
    // Expected exponents: 5, 6, 5, 7.
    var w = new BitWriter();
    var word = 3 * 25 + 1 * 5 + 4;       // = 84
    w.Put(word, 7);
    var bytes = w.ToBytes(4);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);
    var exp = new byte[16];
    var end = Ac3Exponents.Decode(r, exp, start: 0, absExp: 5, nGroups: 1, Ac3Exponents.Strategy.D15);

    Assert.That(end, Is.EqualTo(4));
    Assert.That(exp[0], Is.EqualTo(5));
    Assert.That(exp[1], Is.EqualTo(6));
    Assert.That(exp[2], Is.EqualTo(5));
    Assert.That(exp[3], Is.EqualTo(7));
  }

  [Test]
  [Category("HappyPath")]
  public void DecodeExponents_GroupedD25_RepeatsEachExponentTwice() {
    // D25: every decoded exponent applies to 2 mantissa bins. Absolute 4, word deltas (2,3,2) →
    // (0,+1,0): exponents 4,4 | 4,4 | 5,5 | 5,5.
    var w = new BitWriter();
    var word = 2 * 25 + 3 * 5 + 2;       // = 67
    w.Put(word, 7);
    var bytes = w.ToBytes(4);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);
    var exp = new byte[16];
    Ac3Exponents.Decode(r, exp, 0, absExp: 4, nGroups: 1, Ac3Exponents.Strategy.D25);

    Assert.That(new[] { exp[0], exp[1], exp[2], exp[3], exp[4], exp[5], exp[6], exp[7] },
                Is.EqualTo(new byte[] { 4, 4, 4, 4, 5, 5, 5, 5 }));
  }

  // ── Mantissa grouping unit tests (hand-computed) ───────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Mantissa_Bap1_DecodesThreeLevelsFromOneFiveBitWord() {
    // bap 1 = 3-level quantizer, 3 mantissas packed in one 5-bit word as base-3 (m0,m1,m2).
    // word = ((m0*3)+m1)*3+m2; choose (2,0,1) → word = (2*3+0)*3+1 = 19.
    // 3-level dequant: index k → (2k-2)/3 → {-2/3, 0, +2/3}.
    var w = new BitWriter();
    w.Put((2 * 3 + 0) * 3 + 1, 5);
    var bytes = w.ToBytes(2);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);
    var m = new Ac3Mantissas(r);

    Assert.That(m.Next(1, dither: false), Is.EqualTo(2f / 3f).Within(1e-6));   // m0 = 2
    Assert.That(m.Next(1, dither: false), Is.EqualTo(-2f / 3f).Within(1e-6));  // m1 = 0
    Assert.That(m.Next(1, dither: false), Is.EqualTo(0f).Within(1e-6));        // m2 = 1
  }

  [Test]
  [Category("HappyPath")]
  public void Mantissa_Bap2_DecodesFiveLevelsFromOneSevenBitWord() {
    // bap 2 = 5-level quantizer, 3 mantissas in one 7-bit word base-5 (m0,m1,m2).
    // word = ((m0*5)+m1)*5+m2; choose (4,2,0) → word = (4*5+2)*5+0 = 110.
    // 5-level dequant: index k → (2k-4)/5.
    var w = new BitWriter();
    w.Put((4 * 5 + 2) * 5 + 0, 7);
    var bytes = w.ToBytes(2);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);
    var m = new Ac3Mantissas(r);

    Assert.That(m.Next(2, dither: false), Is.EqualTo((2f * 4 - 4) / 5).Within(1e-6));  // m0 = 4 → +4/5
    Assert.That(m.Next(2, dither: false), Is.EqualTo((2f * 2 - 4) / 5).Within(1e-6));  // m1 = 2 → 0
    Assert.That(m.Next(2, dither: false), Is.EqualTo((2f * 0 - 4) / 5).Within(1e-6));  // m2 = 0 → -4/5
  }

  [Test]
  [Category("HappyPath")]
  public void Mantissa_Bap0_DithDisabled_IsZero() {
    var bytes = new byte[2];
    var r = new Ac3BitReader(bytes, 0, bytes.Length);
    var m = new Ac3Mantissas(r);
    Assert.That(m.Next(0, dither: false), Is.EqualTo(0f));
  }

  [Test]
  [Category("HappyPath")]
  public void Mantissa_Bap0_DithEnabled_IsDeterministicNonZero() {
    var bytes = new byte[2];
    var first = new Ac3Mantissas(new Ac3BitReader(bytes, 0, bytes.Length)).Next(0, dither: true);
    var again = new Ac3Mantissas(new Ac3BitReader(bytes, 0, bytes.Length)).Next(0, dither: true);
    Assert.That(again, Is.EqualTo(first));    // deterministic LFSR seed → reproducible dither
  }

  // ── Bit-allocation masking unit test ───────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void BitAllocation_ZeroSnrOffset_ForcesBapZero() {
    // With a heavily negative snroffset the (psd - mask) address clamps to 0 for every bin → bap 0.
    var exp = new byte[256];
    for (var i = 0; i < 256; ++i) exp[i] = 15;     // flat max-attenuation envelope
    var bap = new byte[256];
    var p = Ac3BitAllocation.Resolve(0, 0, 0, 0, 0);
    var snr = ((0 - 15) << 4) << 2;                 // csnroffst 0, fsnroffst 0

    Ac3BitAllocation.ComputeBap(exp, bap, start: 0, end: 37, p, fgain: 0x100, snrOffset: snr,
      fscod: 0, isCoupling: false, 0, 0, deltas: null);

    for (var bin = 0; bin < 37; ++bin)
      Assert.That(bap[bin], Is.EqualTo(0), $"bap[{bin}] should be 0 for zero snroffset");
  }

  [Test]
  [Category("HappyPath")]
  public void BitAllocation_HighSnrOffset_AllocatesBits() {
    // A strong (positive) snroffset against a flat envelope should allocate non-zero baps,
    // confirming the masking curve responds to snroffset.
    var exp = new byte[256];      // all-zero exponents → maximum psd
    var bap = new byte[256];
    var p = Ac3BitAllocation.Resolve(0, 0, 0, 0, 0);
    var snr = (((63 - 15) << 4) + 15) << 2;         // large snroffset

    Ac3BitAllocation.ComputeBap(exp, bap, start: 0, end: 37, p, fgain: 0x400, snrOffset: snr,
      fscod: 0, isCoupling: false, 0, 0, deltas: null);

    var anyAllocated = false;
    for (var bin = 0; bin < 37; ++bin)
      if (bap[bin] > 0) { anyAllocated = true; break; }
    Assert.That(anyAllocated, Is.True, "a large snroffset should allocate at least one mantissa");
  }

  // ── IMDCT impulse sanity ───────────────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Imdct_ZeroCoefficients_ProduceZeroOutput() {
    var coeffs = new float[256];
    var delay = new float[256];
    var output = new float[256];
    Ac3Imdct.Long(coeffs, delay, output);
    Assert.That(output, Is.All.EqualTo(0f));
    Assert.That(delay, Is.All.EqualTo(0f));
  }

  [Test]
  [Category("HappyPath")]
  public void Imdct_SingleImpulse_ProducesWindowedCosine() {
    // A single non-zero transform coefficient should map to a windowed cosine basis function,
    // i.e. a non-trivial, finite time-domain signal — basic IMDCT liveness.
    var coeffs = new float[256];
    coeffs[1] = 1.0f;
    var delay = new float[256];
    var output = new float[256];
    Ac3Imdct.Long(coeffs, delay, output);

    var energy = 0.0;
    foreach (var s in output) {
      Assert.That(float.IsFinite(s), Is.True);
      energy += s * (double)s;
    }
    Assert.That(energy, Is.GreaterThan(0.0), "a single coefficient must yield a non-zero time signal");
  }
}
