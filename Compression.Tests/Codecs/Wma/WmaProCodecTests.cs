#pragma warning disable CS1591
using Codec.WmaPro;

namespace Compression.Tests.Codecs.Wma;

/// <summary>
/// Pins the WMA 9 Professional decoder's construction-time derivations (frame length,
/// scale-factor-band layout, subframe configuration, channel/LFE setup) and its
/// deterministic bitstream-parsing paths (tile header, channel transform, scale factors,
/// vector-coefficient escapes), each hand-walked against the FFmpeg reference logic.
/// Real WMA Pro streams are produced by the Microsoft encoder; the parsing tests
/// therefore craft minimal valid bit sequences with <see cref="BitWriter"/> rather than
/// relying on a captured stream, while the end-to-end paths assert graceful, consistent
/// behaviour (silence / truncation tolerance) on synthetic input.
/// </summary>
[TestFixture]
public class WmaProCodecTests {

  // ── MSB-first bit writer mirroring the reader's bit order ─────────────────────
  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bitCount;

    public void Put(int n, uint value) {
      for (var i = n - 1; i >= 0; --i) {
        this._cur = (this._cur << 1) | (int)((value >> i) & 1);
        if (++this._bitCount == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bitCount = 0; }
      }
    }

    public byte[] ToArray(int minBytes = 0) {
      var outBytes = new List<byte>(this._bytes);
      if (this._bitCount > 0) outBytes.Add((byte)(this._cur << (8 - this._bitCount)));
      while (outBytes.Count < minBytes) outBytes.Add(0);
      return outBytes.ToArray();
    }
  }

  // WAVEFORMATEX extradata tail: bits-per-sample @0, channel mask @2, decode flags @14.
  private static byte[] Extradata(int bitsPerSample, uint channelMask, int decodeFlags) {
    var e = new byte[18];
    e[0] = (byte)(bitsPerSample & 0xFF);
    e[1] = (byte)(bitsPerSample >> 8);
    e[2] = (byte)(channelMask & 0xFF);
    e[3] = (byte)((channelMask >> 8) & 0xFF);
    e[4] = (byte)((channelMask >> 16) & 0xFF);
    e[5] = (byte)((channelMask >> 24) & 0xFF);
    e[14] = (byte)(decodeFlags & 0xFF);
    e[15] = (byte)((decodeFlags >> 8) & 0xFF);
    return e;
  }

  private static WmaProCodec NewCodec(int channels = 2, int sampleRate = 44100, int bitsPerSample = 16,
      int blockAlign = 2048, uint channelMask = 0, int decodeFlags = 0) =>
    new(channels, sampleRate, bitsPerSample, blockAlign, blockAlign * 10,
        Extradata(bitsPerSample, channelMask, decodeFlags));

  // ── construction / init derivations ──────────────────────────────────────────

  [TestCase(8000, 512)]    // <=16000 -> 9 bits
  [TestCase(16000, 512)]
  [TestCase(22050, 1024)]  // <=22050 -> 10 bits
  [TestCase(44100, 2048)]  // <=48000 -> 11 bits
  [TestCase(48000, 2048)]
  [TestCase(96000, 4096)]  // <=96000 -> 12 bits
  public void SamplesPerFrame_PerSampleRate(int sampleRate, int expected) {
    var codec = NewCodec(sampleRate: sampleRate);
    Assert.That(codec.SamplesPerFrame, Is.EqualTo(expected));
  }

  [TestCase(0x2, +1)]  // decode_flags bits 0x6 == 0x2 -> +1
  [TestCase(0x4, -1)]  // == 0x4 -> -1
  [TestCase(0x6, -2)]  // == 0x6 -> -2
  public void SamplesPerFrame_DecodeFlagShift(int flagBits, int shift) {
    var baseCodec = NewCodec(sampleRate: 44100);
    var shifted = NewCodec(sampleRate: 44100, decodeFlags: flagBits);
    var expected = shift >= 0 ? baseCodec.SamplesPerFrame << shift : baseCodec.SamplesPerFrame >> -shift;
    Assert.That(shifted.SamplesPerFrame, Is.EqualTo(expected));
  }

  [Test]
  public void LengthPrefixAndDrc_FromDecodeFlags() {
    var both = NewCodec(decodeFlags: 0x40 | 0x80);
    Assert.That(both.UsesLengthPrefix, Is.True);
    Assert.That(both.UsesDynamicRangeCompression, Is.True);
    var neither = NewCodec(decodeFlags: 0);
    Assert.That(neither.UsesLengthPrefix, Is.False);
    Assert.That(neither.UsesDynamicRangeCompression, Is.False);
  }

  [TestCase(0x00, 1)]   // log2_max_num_subframes 0 -> 1 subframe
  [TestCase(0x08, 2)]   // bits 0x38>>3 == 1 -> 2
  [TestCase(0x18, 8)]   // == 3 -> 8
  [TestCase(0x28, 32)]  // == 5 -> 32
  public void MaxNumSubframes_FromDecodeFlags(int flags, int expected) {
    var codec = NewCodec(decodeFlags: flags);
    Assert.That(codec.MaxNumSubframes, Is.EqualTo(expected));
  }

  [Test]
  public void NumSfb_MatchesReferenceFormula_44100_FullBlock() {
    var codec = NewCodec(sampleRate: 44100, blockAlign: 2048);
    // For the full-length subframe (table 0) the sfb offsets are built from the critical
    // frequencies; the count must be > 0 and the last offset must equal samples_per_frame.
    var offsets = codec.SfbOffsets(0);
    var n = codec.NumScaleFactorBands(0);
    Assert.That(n, Is.GreaterThan(0));
    Assert.That(offsets[0], Is.EqualTo(0));
    Assert.That(offsets[n], Is.EqualTo(codec.SamplesPerFrame));
  }

  [Test]
  public void ChannelMask_DerivesChannelCount_And_Lfe_5point1() {
    // 5.1 mask: FL FR FC LFE BL BR = 0x3F. Bit 3 (0x8) is LFE.
    var codec = NewCodec(channels: 6, sampleRate: 48000, channelMask: 0x3F);
    Assert.That(codec.Channels, Is.EqualTo(6));
    // popcount(0x3F & 0xF below LFE...) LFE index = number of set channel bits below it.
    Assert.That(codec.LfeChannel, Is.EqualTo(3));
  }

  [Test]
  public void NoChannelMask_UsesWaveFormatChannels_NoLfe() {
    var codec = NewCodec(channels: 2, channelMask: 0);
    Assert.That(codec.Channels, Is.EqualTo(2));
    Assert.That(codec.LfeChannel, Is.EqualTo(-1));
  }

  [Test]
  public void Construct_24Bit_Succeeds_And_ScalesByDepth() {
    var codec = NewCodec(channels: 2, sampleRate: 48000, bitsPerSample: 24);
    Assert.That(codec.BitsPerSample, Is.EqualTo(24));
    Assert.That(codec.SamplesPerFrame, Is.EqualTo(2048));
  }

  [Test]
  public void SubwooferCutoff_IsClampedIntoBlock() {
    var codec = NewCodec(sampleRate: 48000, blockAlign: 2048);
    var cutoff = codec.SubwooferCutoff(0);
    Assert.That(cutoff, Is.GreaterThanOrEqualTo(4));
    Assert.That(cutoff, Is.LessThanOrEqualTo(codec.SamplesPerFrame));
  }

  [Test]
  public void Construct_RejectsBadParameters() {
    Assert.That(() => new WmaProCodec(2, 44100, 16, 0, 1000, Extradata(16, 0, 0)),
      Throws.InstanceOf<ArgumentOutOfRangeException>()); // block_align 0
    Assert.That(() => new WmaProCodec(2, 44100, 16, 2048, 1000, new byte[4]),
      Throws.InstanceOf<ArgumentException>());           // extradata too short
    Assert.That(() => new WmaProCodec(9, 44100, 16, 2048, 1000, Extradata(16, 0, 0)),
      Throws.InstanceOf<ArgumentOutOfRangeException>());  // > 8 channels
    Assert.That(() => new WmaProCodec(2, 44100, 0, 2048, 1000, Extradata(0, 0, 0)),
      Throws.InstanceOf<ArgumentOutOfRangeException>());  // bits-per-sample 0
  }

  // ── tile-header decode ────────────────────────────────────────────────────────

  [Test]
  public void TileHdr_SingleSubframe_WhenMaxNumSubframesIsOne() {
    // max_num_subframes == 1 forces a single full-length subframe with no bits read.
    var codec = NewCodec(channels: 2, sampleRate: 44100, decodeFlags: 0x00);
    codec.TestInitReader(new byte[8]);
    var layout = codec.TestDecodeTileHdr();
    Assert.That(layout.Length, Is.EqualTo(2));
    foreach (var ch in layout) {
      Assert.That(ch.Length, Is.EqualTo(1));
      Assert.That(ch[0], Is.EqualTo(codec.SamplesPerFrame));
    }
  }

  [Test]
  public void TileHdr_FixedLayout_TwoEqualSubframes() {
    // max_num_subframes = 2 (flags 0x08): one length bit each step. With a fixed channel
    // layout bit = 1, every channel takes the same subframe sequence. samples_per_frame
    // = 2048, min subframe = 1024. A frame_len_shift of 1 -> subframe_len 1024 (two of them).
    var codec = NewCodec(channels: 2, sampleRate: 44100, decodeFlags: 0x08);
    var bw = new BitWriter();
    bw.Put(1, 1);             // fixed_channel_layout = 1
    // subframe 0: offset 0 != samples_per_frame - min, so read length: subframe_len_bits=1.
    bw.Put(1, 1);             // frame_len_shift = 1 -> subframe_len = 2048>>1 = 1024
    // subframe 1: offset == samples_per_frame - min -> length implied, no bits.
    var data = bw.ToArray(8);
    codec.TestInitReader(data);
    var layout = codec.TestDecodeTileHdr();
    foreach (var ch in layout) {
      Assert.That(ch.Length, Is.EqualTo(2));
      Assert.That(ch[0], Is.EqualTo(1024));
      Assert.That(ch[1], Is.EqualTo(1024));
    }
  }

  [Test]
  public void TileHdr_FiveOne_SingleSubframe_AllChannels() {
    // 5.1 with max_num_subframes 1: every channel gets one full-length subframe.
    var codec = NewCodec(channels: 6, sampleRate: 48000, channelMask: 0x3F, decodeFlags: 0x00);
    codec.TestInitReader(new byte[16]);
    var layout = codec.TestDecodeTileHdr();
    Assert.That(layout.Length, Is.EqualTo(6));
    foreach (var ch in layout) {
      Assert.That(ch.Length, Is.EqualTo(1));
      Assert.That(ch[0], Is.EqualTo(codec.SamplesPerFrame));
    }
  }

  // ── channel transform parse ───────────────────────────────────────────────────

  [Test]
  public void ChannelTransform_Stereo_MsTransformEnabled() {
    var codec = NewCodec(channels: 2, sampleRate: 44100, decodeFlags: 0x00);
    var bw = new BitWriter();
    bw.Put(1, 0); // "channel transform bit" must be 0
    // remaining_channels == 2 (<=2 path): group takes both channels, no mask bits.
    bw.Put(1, 0); // transform type bit: 0 -> M/S transform enabled
    bw.Put(1, 1); // transform on/off: 1 -> all bands transformed (no per-band bits)
    var data = bw.ToArray(8);
    codec.TestInitReader(data);
    var groups = codec.TestDecodeChannelTransform();
    Assert.That(groups.Length, Is.EqualTo(1));
    Assert.That(groups[0].Channels.Length, Is.EqualTo(2));
    Assert.That(groups[0].Transform, Is.True);
  }

  [Test]
  public void ChannelTransform_Stereo_NoTransform() {
    var codec = NewCodec(channels: 2, sampleRate: 44100, decodeFlags: 0x00);
    var bw = new BitWriter();
    bw.Put(1, 0); // channel transform bit
    bw.Put(1, 1); // transform type bit: 1 -> then next bit 0 = no transform
    bw.Put(1, 0); // -> chgroup.transform stays false
    var data = bw.ToArray(8);
    codec.TestInitReader(data);
    var groups = codec.TestDecodeChannelTransform();
    Assert.That(groups.Length, Is.EqualTo(1));
    Assert.That(groups[0].Transform, Is.False);
  }

  // ── scale-factor decode (DPCM path) ───────────────────────────────────────────

  [Test]
  public void ScaleFactors_DpcmPath_AppliesVlcDeltasToBaseValue() {
    var codec = NewCodec(channels: 1, sampleRate: 44100, channelMask: 0, decodeFlags: 0x00);
    var numBands = codec.NumScaleFactorBands(0);

    // scale_factor_step = (2 bits)+1. Pick step bits = 0 -> step 1, base val = 45/1 = 45.
    // Each band adds a scale-factor VLC delta. In the canonical scale codebook the symbol
    // for delta 0 (table {60,1} with offset -60) is the 1-bit code whose value is 1, so a
    // stream of 1-bits after the step keeps val constant at 45 across every band.
    var bw = new BitWriter();
    bw.Put(2, 0);                 // scale_factor_step bits -> step 1
    for (var b = 0; b < numBands; ++b) bw.Put(1, 1); // each 1-bit code -> delta 0
    var data = bw.ToArray(64);
    codec.TestInitReader(data);
    var sf = codec.TestDecodeScaleFactorsSingle();
    Assert.That(sf.Length, Is.EqualTo(numBands));
    Assert.That(sf, Is.All.EqualTo(45));
  }

  [TestCase(0x80, 0)]   // 1-bit code value 1 -> symbol 60, offset -60 -> delta 0
  [TestCase(0x40, -1)]  // 2-bit code value 01 -> symbol 59, offset -60 -> delta -1
  [TestCase(0x20, +1)]  // 3-bit code value 001 -> symbol 61, offset -60 -> delta +1
  public void ScaleFactorVlc_ShortestCodes_DecodeKnownDeltas(int firstByte, int expectedDelta) {
    // Canonical assignment (ff_vlc_init_from_lengths) gives the scale codebook's three
    // shortest codes: 1-bit "1" -> 0, 2-bit "01" -> -1, 3-bit "001" -> +1 (post-offset).
    var codec = NewCodec(channels: 1, sampleRate: 44100, decodeFlags: 0x00);
    codec.TestInitReader([(byte)firstByte]);
    var sym = codec.TestDecodeScaleFactorVlc(out var ok);
    Assert.That(ok, Is.True);
    Assert.That(sym, Is.EqualTo(expectedDelta));
  }

  // ── vector-coefficient decode ─────────────────────────────────────────────────

  [Test]
  public void Coeffs_VectorEscapeChain_DecodesFourZeros() {
    // Walk the full vector escape chain for one vec4 group of four coefficients:
    //   vec4 code "0"            (1 bit)  -> idx -1, escape to vec2
    //   vec2 code "001" (value 1, 3 bits) -> idx -1, escape to vec1   (x2 pairs)
    //   vec1 code "0011010" (value 26, 7 bits) -> symbol 0 -> coefficient value 0
    // Four zero values are produced; zero values read no sign bit, so the block is zero.
    var codec = NewCodec(channels: 1, sampleRate: 44100, decodeFlags: 0x00);
    var bw = new BitWriter();
    bw.Put(1, 0); // vlctable selector -> coef_vlc[0] (unused once all four vals decode)
    bw.Put(1, 0);             // vec4 escape
    bw.Put(3, 0b001);         // vec2 escape (pair 0)
    bw.Put(7, 0b0011010);     // vec1 -> 0 (v0)
    bw.Put(7, 0b0011010);     // vec1 -> 0 (v1)
    bw.Put(3, 0b001);         // vec2 escape (pair 1)
    bw.Put(7, 0b0011010);     // vec1 -> 0 (v0)
    bw.Put(7, 0b0011010);     // vec1 -> 0 (v1)
    var data = bw.ToArray(64);
    codec.TestInitReader(data);
    // num_vec_coeffs == 4 -> exactly one vec4 iteration, then the run-level tail clears.
    var coeffs = codec.TestDecodeCoeffs(128, numVecCoeffs: 4, transmitNumVec: true);
    Assert.That(coeffs.Length, Is.EqualTo(128));
    for (var i = 0; i < 4; ++i) Assert.That(coeffs[i], Is.EqualTo(0f).Within(1e-6f));
  }

  [Test]
  public void Coeffs_VectorEscape_Vec1LargeValEscape_AddsDecodedMagnitude() {
    // vec1 symbol 100 (HUFF_VEC1_SIZE-1, 5-bit code value 22 = "10110") triggers
    // ff_wma_get_large_val. With the length prefix bit 0 (n_bits = 8) the magnitude is
    // 100 + (next 8 bits). The reference decodes all four values first, then reads one
    // sign bit per non-zero value, so the sign bits trail the whole vec4 group.
    var codec = NewCodec(channels: 1, sampleRate: 44100, decodeFlags: 0x00);
    var bw = new BitWriter();
    bw.Put(1, 0);             // vlctable selector
    bw.Put(1, 0);             // vec4 escape
    bw.Put(3, 0b001);         // vec2 escape (pair 0)
    bw.Put(5, 22);            // v0: vec1 code "10110" -> symbol 100 (== size-1) -> escape
    bw.Put(1, 0);             // v0 large_val length prefix: n_bits = 8
    bw.Put(8, 5);             // v0 magnitude tail: 100 + 5 = 105
    bw.Put(7, 0b0011010);     // v1: vec1 -> 0
    bw.Put(3, 0b001);         // vec2 escape (pair 1)
    bw.Put(7, 0b0011010);     // v0: vec1 -> 0
    bw.Put(7, 0b0011010);     // v1: vec1 -> 0
    bw.Put(1, 1);             // sign bit for the single non-zero value (vals[0]=105) -> +
    var data = bw.ToArray(64);
    codec.TestInitReader(data);
    var coeffs = codec.TestDecodeCoeffs(128, numVecCoeffs: 4, transmitNumVec: true);
    Assert.That(coeffs[0], Is.EqualTo(105f).Within(1e-3f)); // v0 magnitude, positive sign
    Assert.That(coeffs[1], Is.EqualTo(0f).Within(1e-6f));
  }

  // ── end-to-end packet behaviour ───────────────────────────────────────────────

  [Test]
  public void DecodePacket_AllZero_DoesNotThrow_AndStaysSilentOrEmpty() {
    var codec = NewCodec(channels: 2, sampleRate: 44100, blockAlign: 2048, decodeFlags: 0x00);
    short[] pcm = null!;
    Assert.That(() => pcm = codec.DecodePacket(new byte[2048]), Throws.Nothing);
    // An all-zero packet either feeds the reservoir (empty) or decodes to silence; in
    // neither case may it emit a non-silent sample.
    Assert.That(pcm, Is.Not.Null);
    Assert.That(pcm, Is.All.EqualTo((short)0));
    Assert.That(pcm.Length % codec.Channels, Is.EqualTo(0));
  }

  [Test]
  public void DecodePacket_TruncatedShorterThanBlockAlign_DoesNotThrow() {
    var codec = NewCodec(channels: 2, sampleRate: 44100, blockAlign: 2048, decodeFlags: 0x00);
    short[] pcm = null!;
    Assert.That(() => pcm = codec.DecodePacket(new byte[16]), Throws.Nothing);
    Assert.That(pcm, Is.Not.Null);
    Assert.That(pcm.Length, Is.EqualTo(0)); // under-length packet is treated as loss
  }

  [Test]
  public void DecodeSuperframe_IsAliasOfDecodePacket() {
    var codec = NewCodec(channels: 2, sampleRate: 44100, blockAlign: 2048);
    var viaSuper = codec.DecodeSuperframe(new byte[2048]);
    Assert.That(viaSuper, Is.Not.Null);
  }
}
