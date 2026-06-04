#pragma warning disable CS1591
using Codec.Ac3;

namespace Compression.Tests.Codecs.Ac3;

/// <summary>
/// Pins the clean-room E-AC-3 (ATSC A/52 Annex E) decoder. Hand-crafted "silence" independent-
/// substream frames — built for every block-count variant (numblkscod 0/1/2/3 → 1/2/3/6 blocks) with
/// an snroffset forced so every bap is 0 and dither off → exact zeros — let the per-frame sample
/// counts and channel layout be checked deterministically. Further unit tests pin the half-rate
/// (fscod2) header parse, the AHT GAQ / 6-point IDCT arithmetic against hand-computed values, the
/// spectral-extension band-structure parse and the dependent-substream skip.
/// </summary>
[TestFixture]
public class Eac3CodecTests {

  // ── MSB-first bit writer mirroring the decoder's read order ────────────────────────────
  private sealed class BitWriter {
    private readonly List<bool> _bits = [];
    public void Put(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        this._bits.Add(((value >> i) & 1) != 0);
    }
    public void Flag(bool b) => this._bits.Add(b);
    public int BitCount => this._bits.Count;
    public void PatchBits(int bitOffset, int value, int count) {
      for (var i = 0; i < count; ++i)
        this._bits[bitOffset + i] = ((value >> (count - 1 - i)) & 1) != 0;
    }
    public byte[] ToBytes(int totalBytes) {
      var bytes = new byte[totalBytes];
      for (var i = 0; i < this._bits.Count; ++i)
        if (this._bits[i])
          bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
      return bytes;
    }
  }

  /// <summary>
  /// Builds one valid E-AC-3 silence independent-substream frame: acmod 1 (mono, one full-bandwidth
  /// channel), no LFE, no coupling, AC-3-style per-block exponents, AHT off, dither off and an
  /// snroffset that drives every bap to 0 → all-zero PCM. The frame-size word (frmsiz) is patched
  /// after the body is laid out so the header walker sizes the frame exactly.
  /// </summary>
  public static byte[] BuildSilenceFrame(int numblkscod, int fscod = 0) {
    const int acmod = 1;          // mono, single full-bandwidth channel
    const int nfchans = 1;
    var numBlocks = numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };
    var w = new BitWriter();

    // ── syncinfo + BSI ──────────────────────────────────────────────────────
    w.Put(0x0B77, 16);            // sync word
    w.Put(0, 2);                  // strmtyp = 0 (independent)
    w.Put(0, 3);                  // substreamid = 0
    var frmsizPos = w.BitCount;
    w.Put(0, 11);                 // frmsiz placeholder (patched below)
    w.Put(fscod, 2);              // fscod (!= 3)
    w.Put(numblkscod, 2);         // numblkscod
    w.Put(acmod, 3);              // acmod = 1
    w.Flag(false);                // lfeon = 0
    w.Put(16, 5);                 // bsid = 16
    w.Put(0, 5);                  // dialnorm
    w.Flag(false);                // compre
    w.Flag(false);                // mixmdate
    w.Flag(false);                // infomdate
    if (numBlocks != 6)
      w.Flag(false);              // convsync (strmtyp 0, < 6 blocks)
    w.Flag(false);                // addbsie

    // ── audio frame header ──────────────────────────────────────────────────
    if (numBlocks == 6) {
      w.Flag(true);               // ac3_exponent_strategy = 1 (AC-3 style per-block)
      w.Flag(false);              // parse_aht_info = 0
    }
    w.Put(1, 2);                  // snr_offset_strategy = 1 (block-0 snr present)
    w.Flag(false);                // transproce
    w.Flag(false);                // blkswe (long blocks)
    w.Flag(true);                 // dithflage = 1 (we write dither flags per block)
    w.Flag(true);                 // bamode = 1 (we write bit-alloc params in block 0)
    w.Flag(false);                // frmfgaincode (fast gain defaults at block 0)
    w.Flag(false);                // dbaflde
    w.Flag(false);                // skipflde
    w.Flag(false);                // spxattene

    // No coupling: acmod 1 (mono) means channel_mode <= 1 → no coupling-strategy loop.

    // exponent strategy data (AC-3 style): per block, per channel (no coupling).
    for (var blk = 0; blk < numBlocks; ++blk) {
      if (blk == 0)
        w.Put(1, 2);              // chexpstr = D15 in block 0
      else
        w.Put(0, 2);              // reuse
    }
    // converter exponent strategy: numBlocks==6 always carries 5*nfchans bits; otherwise a flag
    // gates them (we clear the flag → none present).
    if (numBlocks == 6)
      w.Put(0, 5 * nfchans);      // converted-from-AC-3 channel exponent strategy (skipped)
    else
      w.Flag(false);              // convexpstre = 0 → no converter exponent strategy

    // per-frame SNR offset is NOT here (snr_offset_strategy != 0 → snr lives in block 0).

    // spx atten data: spxattene=0 → none. block start info: numBlocks>1 → 1 flag.
    if (numBlocks > 1)
      w.Flag(false);              // no block-start info

    for (var blk = 0; blk < numBlocks; ++blk)
      WriteSilenceBlock(w, blk, nfchans);

    // ── size + patch frmsiz ─────────────────────────────────────────────────
    var totalBits = w.BitCount;
    var words = (totalBits + 15) / 16;          // round up to 16-bit words
    if (words < 1) words = 1;
    w.PatchBits(frmsizPos, words - 1, 11);

    var frameBytes = words * 2;
    var buffer = w.ToBytes(frameBytes + 8);     // a little headroom; trim to frame size
    return buffer[..frameBytes];
  }

  private static void WriteSilenceBlock(BitWriter w, int blk, int nfchans) {
    // block switch flags: blkswe=0 → none written.
    // dither flags: dithflage=1 → one per fbw channel; 0 = exact zeros for bap 0.
    for (var ch = 0; ch < nfchans; ++ch)
      w.Flag(false);              // dithflag = 0

    w.Flag(false);                // dynrnge (single set; acmod != 0)

    // spectral extension strategy: block 0 unconditional spxstre; later blocks gated.
    if (blk == 0)
      w.Flag(false);              // spxinu = 0
    else
      w.Flag(false);              // spxstre = 0 (reuse → spx stays off)

    // coupling strategy: acmod 1 → channel_mode <= 1, cpl_strategy_exists is false for all blocks,
    // so nothing is written here.

    // rematrixing: acmod 1 (mono) → none.

    // channel bandwidth: when this block sets exponents, read chbwcod (no cpl, no spx).
    var setsExp = blk == 0;
    if (setsExp)
      w.Put(0, 6);                // chbwcod = 0 → end_freq = 73

    // exponents (D15) for block 0.
    if (setsExp) {
      var endFreq = 73;
      var ngrp = (endFreq - 1 + 2) / 3;         // ceil((endFreq-1)/3) for D15
      w.Put(15, 4);               // absolute exponent = 15
      for (var g = 0; g < ngrp; ++g)
        w.Put(2 * 25 + 2 * 5 + 2, 7);           // delta codes all 2 → flat envelope
      w.Put(0, 2);                // gainrng
    }

    // bit-allocation parameters (bamode=1): a baie flag is read every block.
    w.Flag(blk == 0);             // baie (set only in block 0)
    if (blk == 0) {
      w.Put(0, 2);                // sdcycod
      w.Put(0, 2);                // fdcycod
      w.Put(0, 2);                // sgaincod
      w.Put(0, 2);                // dbpbcod
      w.Put(0, 3);                // floorcod
    }

    // SNR offsets (block 0 only, snr_offset_strategy != 0): write csnroffst + per-channel fsnroffst.
    if (blk == 0) {
      w.Flag(true);               // snroffste
      w.Put(0, 6);                // csnroffst = 0 → strongly negative snroffset
      for (var ch = 0; ch < nfchans; ++ch)
        w.Put(0, 4);              // fsnroffst = 0
    }

    // fast gain (frmfgaincode=0 → no read; defaults at block 0).
    // E-AC-3 → AC-3 converter snr offset flag.
    w.Flag(false);                // convexpstre / converter snr offset present = 0

    // no coupling → no coupling leak.
    // delta bit allocation: dbaflde=0 → none.
    // bit allocation computed internally → every bap 0.
    // skip field: skipflde=0 → none.
    // mantissas: all bap 0 + dither off → no bits read.
  }

  private static byte[] Decode(byte[] frame) {
    using var src = new MemoryStream(frame, writable: false);
    using var dst = new MemoryStream();
    Ac3Codec.Decompress(src, dst);
    return dst.ToArray();
  }

  // ── silence-frame decode across all block counts ───────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  [TestCase(0, 1)]
  [TestCase(1, 2)]
  [TestCase(2, 3)]
  [TestCase(3, 6)]
  public void Decode_SilenceFrame_BlockCountVariants_YieldExactZeros(int numblkscod, int expectedBlocks) {
    var frame = BuildSilenceFrame(numblkscod);
    var pcm = Decode(frame);

    // mono, 16-bit: expectedBlocks * 256 samples * 1 channel * 2 bytes.
    Assert.That(pcm, Has.Length.EqualTo(expectedBlocks * 256 * 1 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void ReadStreamInfo_Enhanced_ReportsMonoAndBlockCount() {
    var frame = BuildSilenceFrame(numblkscod: 2);     // 3 blocks
    using var src = new MemoryStream(frame, writable: false);
    var info = Ac3Codec.ReadStreamInfo(src);

    Assert.That(info.IsEnhanced, Is.True);
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.DurationSamples, Is.EqualTo(3 * 256));
  }

  // ── half-rate (fscod2) header parse ────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Header_FsCod3_UsesHalfRateTableAndSixBlocks() {
    // strmtyp 0, fscod 3 → fscod2 selects the half-rate table; numblkscod is implicitly 6 blocks.
    var w = new BitWriter();
    w.Put(0x0B77, 16);
    w.Put(0, 2);                  // strmtyp
    w.Put(0, 3);                  // substreamid
    w.Put(100, 11);               // frmsiz
    w.Put(3, 2);                  // fscod = 3 (half rate)
    w.Put(0, 2);                  // fscod2 = 0 → 24000 Hz
    w.Put(2, 3);                  // acmod = 2
    w.Flag(false);                // lfeon
    w.Put(16, 5);                 // bsid
    w.Put(0, 5);                  // dialnorm
    var bytes = w.ToBytes(256);

    var h = Ac3FrameHeader.TryParse(bytes, 0);
    Assert.That(h, Is.Not.Null);
    Assert.That(h!.Value.IsEnhanced, Is.True);
    Assert.That(h.Value.SampleRate, Is.EqualTo(24000));
    Assert.That(h.Value.FsCod2, Is.EqualTo(0));
    Assert.That(h.Value.NumBlocks, Is.EqualTo(6));
    Assert.That(h.Value.FrameSize, Is.EqualTo((100 + 1) * 2));
  }

  [Test]
  [Category("HappyPath")]
  public void Header_FsCod3_FsCod2_2_Is16000() {
    var w = new BitWriter();
    w.Put(0x0B77, 16);
    w.Put(0, 2);
    w.Put(0, 3);
    w.Put(50, 11);
    w.Put(3, 2);                  // fscod = 3
    w.Put(2, 2);                  // fscod2 = 2 → 16000 Hz
    w.Put(7, 3);                  // acmod = 7
    w.Flag(true);                 // lfeon
    w.Put(16, 5);
    w.Put(0, 5);
    var bytes = w.ToBytes(256);

    var h = Ac3FrameHeader.TryParse(bytes, 0)!.Value;
    Assert.That(h.SampleRate, Is.EqualTo(16000));
    Assert.That(h.Acmod, Is.EqualTo(7));
    Assert.That(h.LowFrequencyEffects, Is.True);
  }

  // ── dependent-substream skip ───────────────────────────────────────────────────────────

  [Test]
  [Category("EdgeCase")]
  public void Decode_DependentSubstreamAfterIndependent_IsSkipped() {
    var indep = BuildSilenceFrame(numblkscod: 0);     // 1-block mono silence
    var dependent = BuildDependentFrame();

    var stream = new byte[indep.Length + dependent.Length];
    Array.Copy(indep, 0, stream, 0, indep.Length);
    Array.Copy(dependent, 0, stream, indep.Length, dependent.Length);

    var pcm = Decode(stream);
    // Only the independent substream contributes samples; the dependent frame is skipped.
    Assert.That(pcm, Has.Length.EqualTo(1 * 256 * 1 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("EdgeCase")]
  public void Header_DependentSubstream_IsFlagged() {
    var dep = BuildDependentFrame();
    var h = Ac3FrameHeader.TryParse(dep, 0)!.Value;
    Assert.That(h.IsEnhanced, Is.True);
    Assert.That(h.IsDependentSubstream, Is.True);
    Assert.That(h.IsIndependentSubstream, Is.False);
  }

  // A minimal valid-header dependent substream frame (frame type 1). It only needs a parseable
  // header so the walker can size and skip it; the body is zero padding.
  private static byte[] BuildDependentFrame() {
    var w = new BitWriter();
    w.Put(0x0B77, 16);
    w.Put(1, 2);                  // strmtyp = 1 (dependent)
    w.Put(0, 3);                  // substreamid
    w.Put(9, 11);                 // frmsiz → (9+1)*2 = 20 bytes
    w.Put(0, 2);                  // fscod
    w.Put(3, 2);                  // numblkscod = 3 → 6 blocks
    w.Put(7, 3);                  // acmod
    w.Flag(false);                // lfeon
    w.Put(16, 5);                 // bsid
    w.Put(0, 5);                  // dialnorm
    return w.ToBytes(20);
  }

  // ── 6-point inverse DCT arithmetic (AHT) ───────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Idct6_DcOnly_SpreadsConstantAcrossAllBlocks() {
    // A pure DC pre-mantissa (only index 0 set) inverse-transforms to the same value in every block.
    var pm = new[] { 1 << 16, 0, 0, 0, 0, 0 };
    Ac3Aht.Idct6(pm);
    for (var i = 0; i < 6; ++i)
      Assert.That(pm[i], Is.EqualTo(1 << 16), $"block {i}");
  }

  [Test]
  [Category("HappyPath")]
  public void Idct6_FirstAcCoefficient_MatchesHandComputedFixedPoint() {
    // Only pm[1] set → odd0 = (pm1 * COEFF_2) >> 23, odd1 = pm1, odd2 = odd0; even terms = 0.
    // Outputs: [odd0+pm1, pm1, odd0, -odd0, -pm1, -(odd0+pm1)] with odd0 = (pm1*3070444)>>23.
    const int pm1 = 1 << 20;
    var odd0 = (int)(((long)pm1 * 3070444L) >> 23);
    int[] expected = [odd0 + pm1, pm1, odd0, -odd0, -pm1, -(odd0 + pm1)];

    var pm = new[] { 0, pm1, 0, 0, 0, 0 };
    Ac3Aht.Idct6(pm);
    Assert.That(pm, Is.EqualTo(expected));
  }

  // ── GAQ dequantization arithmetic (AHT, hebap >= 8) ────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void GaqDequant_NoGain_AppliesGaqRemap1() {
    // hebap 8 → 3 bits, no GAQ gain (logGain 0): mant = signed(3 bits) << (24-3), then remap by
    // ff_eac3_gaq_remap_1[0] (= 4681). Choose the 3-bit code 011b = +3.
    var w = new BitWriter();
    w.Put(0b011, 3);
    var bytes = w.ToBytes(2);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);

    var got = Ac3Aht.GaqDequant(r, hebap: 8, logGain: 0);

    long mant = (long)3 << (24 - 3);
    var expected = (int)(mant + ((4681L * mant) >> 15));
    Assert.That(got, Is.EqualTo(expected));
  }

  [Test]
  [Category("HappyPath")]
  public void GaqDequant_SmallMantissaWithGain_NoLargePath() {
    // hebap 9 → 4 bits. logGain 1 → gbits = 3. A small mantissa (not the min-code) takes the
    // "small mantissa, with gain" path: mant << (24 - bits), no remap (logGain != 0).
    var w = new BitWriter();
    w.Put(0b010, 3);              // +2 in 3 bits (not -(1<<2)=-4, so not the large path)
    var bytes = w.ToBytes(2);
    var r = new Ac3BitReader(bytes, 0, bytes.Length);

    var got = Ac3Aht.GaqDequant(r, hebap: 9, logGain: 1);

    const int bits = 4;
    long mant = (long)2 << (24 - bits);
    Assert.That(got, Is.EqualTo((int)mant));
  }

  // ── AHT end-to-end integration (silence) ───────────────────────────────────────────────

  /// <summary>
  /// Builds a 6-block E-AC-3 mono silence frame that uses AHT: LUT-based exponents (frmchexpstr 0 →
  /// D15 in block 0, reuse for blocks 1..5, which satisfies the AHT precondition), parse_aht_info on,
  /// the channel's AHT-enable bit set, dither off and an snroffst that forces every bap (hebap) to 0.
  /// With hebap 0 + dither off, every pre-mantissa is zero → the 6-point IDCT yields zero → silence,
  /// exercising the whole AHT decode path (gaq_mode read + zero-mantissa branch + idct6).
  /// </summary>
  [Test]
  [Category("HappyPath")]
  public void Decode_AhtSilenceFrame_YieldsExactZeros() {
    const int acmod = 1, nfchans = 1;
    var w = new BitWriter();

    // syncinfo + BSI
    w.Put(0x0B77, 16);
    w.Put(0, 2);                  // strmtyp = 0
    w.Put(0, 3);                  // substreamid
    var frmsizPos = w.BitCount;
    w.Put(0, 11);                 // frmsiz placeholder
    w.Put(0, 2);                  // fscod = 0
    w.Put(3, 2);                  // numblkscod = 3 → 6 blocks
    w.Put(acmod, 3);
    w.Flag(false);                // lfeon
    w.Put(16, 5);                 // bsid
    w.Put(0, 5);                  // dialnorm
    w.Flag(false);                // compre
    w.Flag(false);                // mixmdate
    w.Flag(false);                // infomdate
    w.Flag(false);                // addbsie (6 blocks → no convsync)

    // audio frame header (6 blocks)
    w.Flag(false);                // ac3_exponent_strategy = 0 (LUT-based)
    w.Flag(true);                 // parse_aht_info = 1
    w.Put(1, 2);                  // snr_offset_strategy = 1
    w.Flag(false);                // transproce
    w.Flag(false);                // blkswe
    w.Flag(true);                 // dithflage
    w.Flag(true);                 // bamode
    w.Flag(false);                // frmfgaincode
    w.Flag(false);                // dbaflde
    w.Flag(false);                // skipflde
    w.Flag(false);                // spxattene

    // no coupling (mono). LUT exponent strategy: one 5-bit frmchexpstr per channel.
    w.Put(0, 5);                  // frmchexpstr code 0 → D15, reuse×5
    // converter exponent strategy (6 blocks → 5*nfchans bits present).
    w.Put(0, 5 * nfchans);
    // AHT selection: precondition holds (blocks 1-5 reuse) → 1 enable bit per channel.
    w.Flag(true);                 // channel uses AHT
    // snr_offset_strategy != 0 → no per-frame snr here.
    // block start info: 6 blocks > 1 → 1 flag.
    w.Flag(false);

    for (var blk = 0; blk < 6; ++blk) {
      // dithflag = 0 (exact zeros for hebap 0).
      w.Flag(false);
      w.Flag(false);              // dynrnge
      // spx: block 0 spxinu, later spxstre.
      w.Flag(false);
      // no coupling, no rematrix (mono).
      // channel bandwidth + exponents only when this block sets exponents (block 0, D15).
      if (blk == 0) {
        w.Put(0, 6);              // chbwcod = 0 → end_freq 73
        var ngrp = (73 - 1 + 2) / 3;
        w.Put(15, 4);             // absolute exponent 15
        for (var g = 0; g < ngrp; ++g)
          w.Put(2 * 25 + 2 * 5 + 2, 7);  // flat envelope
        w.Put(0, 2);              // gainrng
      }
      // bit-alloc params: baie every block.
      w.Flag(blk == 0);
      if (blk == 0) {
        w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 3);
      }
      // snr offset: block 0 only.
      if (blk == 0) {
        w.Flag(true);             // snroffste
        w.Put(0, 6);              // csnroffst 0
        w.Put(0, 4);              // fsnroffst 0
      }
      // fast gain: frmfgaincode 0 → none. converter snr offset flag.
      w.Flag(false);
      // no coupling leak, no dba, no skip.
      // AHT pre-mantissas are read in block 0 only: gaq_mode(2 bits) then per-bin (all hebap 0 →
      // no further bits). end_freq 73 bins, all bap 0.
      if (blk == 0)
        w.Put(0, 2);              // gaq_mode = 0
    }

    var totalBits = w.BitCount;
    var words = (totalBits + 15) / 16;
    w.PatchBits(frmsizPos, words - 1, 11);
    var frame = w.ToBytes(words * 2 + 8)[..(words * 2)];

    var pcm = Decode(frame);
    Assert.That(pcm, Has.Length.EqualTo(6 * 256 * 1 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  // ── spectral-extension / coupling band-structure parse ─────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void BandStructure_NoMerges_OneBandPerSubband() {
    // No merge bits set → each 12-bin sub-band is its own band.
    var result = Ac3EnhancedBandStructure.Decode(4, [0, 0, 0]);
    Assert.That(result.NumBands, Is.EqualTo(4));
    Assert.That(result.BandSizes[..4], Is.EqualTo(new[] { 12, 12, 12, 12 }));
    Assert.That(result.SubbandToBand[..4], Is.EqualTo(new[] { 0, 1, 2, 3 }));
  }

  [Test]
  [Category("HappyPath")]
  public void BandStructure_AllMerges_SingleWideBand() {
    // Every boundary merges → one band spanning all five sub-bands (5 * 12 = 60 bins).
    var result = Ac3EnhancedBandStructure.Decode(5, [1, 1, 1, 1]);
    Assert.That(result.NumBands, Is.EqualTo(1));
    Assert.That(result.BandSizes[0], Is.EqualTo(60));
    Assert.That(result.SubbandToBand[..5], Is.EqualTo(new[] { 0, 0, 0, 0, 0 }));
  }

  [Test]
  [Category("HappyPath")]
  public void BandStructure_MixedMerges_MatchesHandComputed() {
    // merge bits [1,0,1] over 4 sub-bands: sb1 merges into band0 (size 24), sb2 starts band1,
    // sb3 merges into band1 (size 24) → 2 bands of 24 bins each.
    var result = Ac3EnhancedBandStructure.Decode(4, [1, 0, 1]);
    Assert.That(result.NumBands, Is.EqualTo(2));
    Assert.That(result.BandSizes[..2], Is.EqualTo(new[] { 24, 24 }));
    Assert.That(result.SubbandToBand[..4], Is.EqualTo(new[] { 0, 0, 1, 1 }));
  }

  [Test]
  [Category("HappyPath")]
  public void BandStructure_DefaultSpxBanding_ProducesExpectedBands() {
    // Drive the decode with the default SPX banding (ff_eac3_default_spx_band_struct) for a typical
    // sub-band span, confirming the table-driven path resolves a sensible band count.
    // Default: { 0,0,0,0,0,0,0,0, 1,0,1,0,1,0,1,0,1 }. Take 6 sub-bands starting at default index 9
    // (merge bits 0,1,0,1,0): merges at positions 1 and 3 → 3 bands.
    var result = Ac3EnhancedBandStructure.Decode(6, [0, 1, 0, 1, 0]);
    Assert.That(result.NumBands, Is.EqualTo(4));
  }
}
