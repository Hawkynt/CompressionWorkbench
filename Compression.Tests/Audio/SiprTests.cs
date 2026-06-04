#pragma warning disable CS1591
using Codec.Sipr;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the SIPR / ACELP.NET decoder (<see cref="SiprCodec"/>). Bit-exact cross-checks against
/// FFmpeg are unavailable here, so these tests pin: the block-align → mode mapping, the LE
/// bitstream convention, hand-computed pitch-lag / pulse-decode / gain-prediction / LSP→LP
/// arithmetic against independently derived values, the <c>sipr_swaps</c> superblock reorder on
/// a synthetic pattern, exact per-frame sample counts, bounded + deterministic decode of the
/// three 8 kbit/s modes, truncation tolerance, and rejection of the unsupported 16k mode.
/// </summary>
[TestFixture]
public class SiprTests {

  // ── mode / flavor mapping ─────────────────────────────────────────────────

  [TestCase(19, SiprCodec.SiprMode.Mode8k5)]
  [TestCase(29, SiprCodec.SiprMode.Mode6k5)]
  [TestCase(37, SiprCodec.SiprMode.Mode5k0)]
  [TestCase(20, SiprCodec.SiprMode.Mode16k)]
  public void ModeFromBlockAlign_MapsCodedFrameSizeExactly(int blockAlign, SiprCodec.SiprMode expected)
    => Assert.That(SiprCodec.ModeFromBlockAlign(blockAlign), Is.EqualTo(expected));

  [TestCase(0)]
  [TestCase(18)]
  [TestCase(96)]
  public void ModeFromBlockAlign_UnknownSize_ReturnsNull(int blockAlign)
    => Assert.That(SiprCodec.ModeFromBlockAlign(blockAlign), Is.Null);

  [Test]
  public void Mode16k_Constructor_Throws() =>
    Assert.That(() => new SiprCodec(SiprCodec.SiprMode.Mode16k),
      Throws.TypeOf<NotSupportedException>());

  [TestCase(SiprCodec.SiprMode.Mode8k5, 19, 144)]
  [TestCase(SiprCodec.SiprMode.Mode6k5, 29, 288)]
  [TestCase(SiprCodec.SiprMode.Mode5k0, 37, 480)]
  public void Mode_FrameSizesAreExact(SiprCodec.SiprMode mode, int frameBytes, int samples) {
    var c = new SiprCodec(mode);
    Assert.That(c.FrameBytes, Is.EqualTo(frameBytes));
    Assert.That(c.SamplesPerFrame, Is.EqualTo(samples));
  }

  // ── LE bitstream convention ───────────────────────────────────────────────

  [Test]
  public void BitReader_ReadsLsbFirstWithinBytes() {
    // 0xB3 = 1011_0011. LSB-first: first 3 bits = 1,1,0 → value 3. Next 5 bits (pos 3) span
    // into the second byte 0x4D and decode to 22 (verified against the reference LE reader).
    var gb = new SiprBitReader([0xB3, 0x4D], 0, 16);
    Assert.That(gb.GetBits(3), Is.EqualTo(3));
    Assert.That(gb.GetBits(5), Is.EqualTo(22));
  }

  [Test]
  public void BitReader_ReadsPastDeclaredLength_AsZero() {
    var gb = new SiprBitReader([0xFF], 0, 4);
    Assert.That(gb.GetBits(4), Is.EqualTo(0xF));
    Assert.That(gb.GetBits(4), Is.EqualTo(0)); // beyond size_in_bits → zero
  }

  // ── pitch-lag decode (ff_decode_pitch_lag, resolution 6) ──────────────────

  [TestCase(0, 0, 60, 19, 1)]
  [TestCase(100, 0, 60, 53, -1)]
  [TestCase(200, 0, 60, 88, 0)]
  [TestCase(10, 1, 60, 58, -1)]
  public void DecodePitchLag_MatchesReference(int pitchIndex, int subframe, int prevLag,
      int expectInt, int expectFrac) {
    var (li, lf) = SiprCodec.Internals.DecodePitchLag(pitchIndex, subframe, false, 6, prevLag);
    Assert.That(li, Is.EqualTo(expectInt));
    Assert.That(lf, Is.EqualTo(expectFrac));
  }

  // ── fixed-codebook pulse decode (decode_fixed_sparse) ─────────────────────

  [Test]
  public void DecodeFixedSparse_6k5_ThreePulses() {
    var (n, x, y) = SiprCodec.Internals.DecodeFixedSparse([0x1f, 0x00, 0x0a],
      SiprCodec.SiprMode.Mode6k5, false);
    Assert.That(n, Is.EqualTo(3));
    Assert.That(x[..3], Is.EqualTo(new[] { 45, 1, 32 }));
    Assert.That(y[..3], Is.EqualTo(new[] { -1f, 1f, 1f }));
  }

  [Test]
  public void DecodeFixedSparse_8k5_SixPulsesWithSignMirroring() {
    var (n, x, y) = SiprCodec.Internals.DecodeFixedSparse([0x123, 0, 0],
      SiprCodec.SiprMode.Mode8k5, false);
    Assert.That(n, Is.EqualTo(6));
    Assert.That(x[..6], Is.EqualTo(new[] { 6, 9, 1, 1, 2, 2 }));
    Assert.That(y[..6], Is.EqualTo(new[] { -1f, -1f, 1f, 1f, 1f, 1f }));
  }

  [Test]
  public void DecodeFixedSparse_5k0_HighGain_TwoPulses() {
    var (n, x, y) = SiprCodec.Internals.DecodeFixedSparse([0x135],
      SiprCodec.SiprMode.Mode5k0, lowGain: false);
    Assert.That(n, Is.EqualTo(2));
    Assert.That(x[..2], Is.EqualTo(new[] { 10, 17 }));
    Assert.That(y[..2], Is.EqualTo(new[] { 1f, -1f }));
  }

  [Test]
  public void DecodeFixedSparse_5k0_LowGain_ThreePulses() {
    var (n, x, y) = SiprCodec.Internals.DecodeFixedSparse([73],
      SiprCodec.SiprMode.Mode5k0, lowGain: true);
    Assert.That(n, Is.EqualTo(3));
    Assert.That(x[..3], Is.EqualTo(new[] { 10, 8, 6 }));
    Assert.That(y[..3], Is.EqualTo(new[] { -1f, 1f, -1f }));
  }

  // ── MA gain prediction walk (ff_amr_set_fixed_gain) ───────────────────────

  [Test]
  public void AmrSetFixedGain_ProducesReferenceValueAndShiftsHistory() {
    var pe = new[] { -14f, -14f, -14f, -14f };
    var pred = new[] { 0.200f, 0.334f, 0.504f, 0.691f };
    var energyMean = (float)(34 - 15.0 / (0.05 * Math.Log(10) / Math.Log(2)));

    var val = SiprCodec.Internals.AmrSetFixedGain(1.0f, 1.0f, pe, energyMean, pred);

    // Independently computed: 1.0 * 10^(0.05*(dot(pred,[-14*4]) + energyMean)) / sqrt(1).
    Assert.That(val, Is.EqualTo(9.424320889845334e-05f).Within(1e-9));
    // History shifts left and the new entry is 20*log10(1.0) = 0.
    Assert.That(pe, Is.EqualTo(new[] { -14f, -14f, -14f, 0f }));
  }

  // ── LSP → LP conversion (lsp2polyf / ff_amrwb_lsp2lpc) ─────────────────────

  [Test]
  public void Lsp2Polyf_SmallCase_MatchesRecurrence() {
    // half order 2, lsp = {0.5, 0.25, -0.25, -0.5}. By the recurrence:
    // f0=1, f1=-2*0.5=-1 → after i=2: val=-2*(-0.25)=0.5, f2=0.5*(-1)+2=1.5, f1+=0.5 → -0.5.
    var f = SiprCodec.Internals.Lsp2Polyf([0.5, 0.25, -0.25, -0.5], 2);
    Assert.That(f, Is.EqualTo(new[] { 1.0, -0.5, 1.5 }).Within(1e-12));
  }

  [Test]
  public void AmrwbLsp2Lpc_Order4_MatchesReference() {
    var lp = SiprCodec.Internals.AmrwbLsp2Lpc([0.5, 0.25, -0.25, -0.5], 4);
    Assert.That(lp.Length, Is.EqualTo(4));
    Assert.That(lp[0], Is.EqualTo(-0.5f).Within(1e-6));
    Assert.That(lp[1], Is.EqualTo(0.375f).Within(1e-6));
    Assert.That(lp[2], Is.EqualTo(0.25f).Within(1e-6));
    Assert.That(lp[3], Is.EqualTo(-0.5f).Within(1e-6)); // lp[order-1] == lsp[order-1]
  }

  // ── LSF spacing / sort helpers ────────────────────────────────────────────

  [Test]
  public void SetMinDistLsf_EnforcesMonotonicMinimumSpacing() {
    var lsf = new[] { 0.0f, 0.05f, 0.05f, 0.20f };
    SiprCodec.Internals.SetMinDistLsf(lsf, 0.1, 4);
    Assert.That(lsf[0], Is.EqualTo(0.1f).Within(1e-6));
    Assert.That(lsf[1], Is.EqualTo(0.2f).Within(1e-6));
    Assert.That(lsf[2], Is.EqualTo(0.3f).Within(1e-6));
    Assert.That(lsf[3], Is.EqualTo(0.4f).Within(1e-6));
  }

  [Test]
  public void SortNearlySortedFloats_SortsAscending() {
    var v = new[] { 3f, 1f, 2f, 0.5f };
    SiprCodec.Internals.SortNearlySortedFloats(v, 4);
    Assert.That(v, Is.EqualTo(new[] { 0.5f, 1f, 2f, 3f }));
  }

  // ── superblock reorder (ff_rm_reorder_sipr_data / sipr_swaps) ──────────────

  [Test]
  public void Reorder_SwapsNibbleRuns_SyntheticPattern() {
    // h=2, fs=24 → bs = 2*24*2/96 = 1 nibble per run. With buf[i] = i:
    //   swap {0,63}: nibble0 (byte0 low = 0x0) ↔ nibble63 (byte31 high = 0x1).
    //   swap {1,22}: nibble1 (byte0 high = 0x0) ↔ nibble22 (byte11 low = 0xB).
    // After both, byte0 = 0xB1.
    var buf = new byte[48];
    for (var i = 0; i < buf.Length; ++i) buf[i] = (byte)i;
    var r = SiprReorder.Reorder(buf, subPacketH: 2, frameSize: 24);

    Assert.That(r[0], Is.EqualTo(0xB1));
    // The source is not mutated (Reorder copies).
    Assert.That(buf[0], Is.EqualTo(0x00));
  }

  [Test]
  public void Reorder_DegenerateFraming_ReturnsInputUnchanged() {
    var buf = new byte[] { 1, 2, 3, 4 };
    // bs = 1*1*2/96 = 0 → no-op.
    var r = SiprReorder.Reorder(buf, subPacketH: 1, frameSize: 1);
    Assert.That(r, Is.EqualTo(buf));
  }

  [Test]
  public void Reorder_TruncatedSuperblock_DoesNotThrow() {
    // bs targets nibble runs up to index 95; a short buffer must be tolerated.
    var buf = new byte[8];
    Assert.That(() => SiprReorder.Reorder(buf, subPacketH: 2, frameSize: 24), Throws.Nothing);
  }

  // ── per-frame sample counts + deterministic bounded decode ────────────────

  [TestCase(SiprCodec.SiprMode.Mode8k5, 144)]
  [TestCase(SiprCodec.SiprMode.Mode6k5, 288)]
  [TestCase(SiprCodec.SiprMode.Mode5k0, 480)]
  public void Decode_ProducesExactSampleCount(SiprCodec.SiprMode mode, int samples) {
    var c = new SiprCodec(mode);
    Assert.That(c.Decode(new byte[c.FrameBytes]).Length, Is.EqualTo(samples));
  }

  [TestCase(SiprCodec.SiprMode.Mode8k5)]
  [TestCase(SiprCodec.SiprMode.Mode6k5)]
  [TestCase(SiprCodec.SiprMode.Mode5k0)]
  public void Decode_IsBoundedAndDeterministic(SiprCodec.SiprMode mode) {
    var c = new SiprCodec(mode);
    var f = new byte[c.FrameBytes];
    for (var i = 0; i < f.Length; ++i) f[i] = (byte)(i * 37 + 11);

    var a = new SiprCodec(mode); a.Decode(f); a.Decode(f); var pa = a.Decode(f);
    var b = new SiprCodec(mode); b.Decode(f); b.Decode(f); var pb = b.Decode(f);

    Assert.That(pa.Length, Is.EqualTo(c.SamplesPerFrame));
    Assert.That(pa.Max(s => Math.Abs((int)s)), Is.LessThanOrEqualTo(32767));
    Assert.That(pa, Is.EqualTo(pb)); // state carried identically across frames
  }

  [Test]
  public void Decode_ZeroFrame_IsNearSilent() {
    var c = new SiprCodec(SiprCodec.SiprMode.Mode8k5);
    var pcm = c.Decode(new byte[c.FrameBytes]);
    Assert.That(pcm.Max(s => Math.Abs((int)s)), Is.LessThanOrEqualTo(8));
  }

  // ── truncation tolerance ──────────────────────────────────────────────────

  [Test]
  public void Decode_TruncatedFrame_DoesNotThrow_AndYieldsFullFrame() {
    var c = new SiprCodec(SiprCodec.SiprMode.Mode6k5);
    short[] pcm = null!;
    Assert.That(() => pcm = c.Decode(new byte[5]), Throws.Nothing);
    Assert.That(pcm.Length, Is.EqualTo(c.SamplesPerFrame));
  }

  [Test]
  public void DecodeStream_RaggedTail_IsPaddedAndDecoded() {
    var c = new SiprCodec(SiprCodec.SiprMode.Mode8k5);
    // one full 19-byte frame + a 5-byte tail → two frames worth of samples.
    var pcm = c.DecodeStream(new byte[19 + 5]);
    Assert.That(pcm.Length, Is.EqualTo(2 * c.SamplesPerFrame));
  }

  [Test]
  public void DecodeStream_Empty_ReturnsEmpty() {
    var c = new SiprCodec(SiprCodec.SiprMode.Mode5k0);
    Assert.That(c.DecodeStream([]).Length, Is.EqualTo(0));
  }
}
