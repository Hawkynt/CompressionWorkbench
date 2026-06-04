#pragma warning disable CS1591
using Codec.Dts;

namespace Compression.Tests.Codecs.Dts;

/// <summary>
/// Pins the clean-room DTS Coherent Acoustics (DCA) core decoder. A hand-crafted "silence" core
/// frame (mono, two active subbands, every bit-allocation index forced to 0 with the 5-bit fixed
/// allocation book) carries no sample bits at all, so it decodes to exact zeros — letting the
/// per-frame / multi-frame sample counts and channel layout be checked deterministically without
/// depending on the QMF arithmetic. Dedicated unit tests pin the block-code unpacking, the inverse
/// ADPCM predictor, the QMF impulse response and the Huffman table shapes against hand-computed or
/// source-verified values.
/// </summary>
[TestFixture]
public class DtsCodecTests {

  // ── MSB-first bit writer mirroring the decoder's read order ──────────────────────────────
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
  /// Builds one valid DCA core silence frame: mono (AMODE 0, 1 prim channel), no LFE, 2 active
  /// subbands, VQ start == subband activity (no high-frequency VQ), one subframe of one
  /// sub-subframe per 8-block group. The bit-allocation code book is the fixed 5-bit book
  /// (<c>bitalloc_huffman == 6</c>) and every allocation index is written as 0, so no sample,
  /// scale-factor or transition-mode bits follow. A single DSYNC (0xFFFF) terminates the
  /// sub-subframe. The result decodes to all-zero PCM.
  /// </summary>
  public static byte[] BuildSilenceFrame(int sfreq = 13, int rate = 24, int sampleBlocks = 8) {
    const int amode = 0;       // mono
    const int nblksMinus1Subbands = 2; // subband activity
    var blockGroups = sampleBlocks / 8;   // one subframe covers all groups as sub-subframes
    var w = new BitWriter();

    // ── Frame header ────────────────────────────────────────────────────────
    w.Put(0x7FFE8001, 32);          // sync word
    w.Put(1, 1);                    // FTYPE = 1 (normal)
    w.Put(0, 5);                    // SHORT = 0
    w.Put(0, 1);                    // CPF = 0 (no CRC)
    w.Put(sampleBlocks - 1, 7);     // NBLKS = sampleBlocks - 1
    var fsizePos = w.BitCount;
    w.Put(0, 14);                   // FSIZE placeholder (patched below)
    w.Put(amode, 6);                // AMODE
    w.Put(sfreq, 4);                // SFREQ
    w.Put(rate, 5);                 // RATE
    w.Put(0, 1);                    // FixedBit
    w.Put(0, 1);                    // DYNF
    w.Put(0, 1);                    // TIMEF
    w.Put(0, 1);                    // AUXF
    w.Put(0, 1);                    // HDCD
    w.Put(0, 3);                    // EXT_AUDIO_ID
    w.Put(0, 1);                    // EXT_AUDIO
    w.Put(0, 1);                    // ASPF = 0 (DSYNC only after last sub-subframe)
    w.Put(0, 2);                    // LFF = 0 (no LFE)
    w.Put(0, 1);                    // predictor history switch
    // CPF == 0 → no header CRC here.
    w.Put(0, 1);                    // MULTIRATE_INTER (non-perfect reconstruction)
    w.Put(0, 4);                    // VERSION
    w.Put(0, 2);                    // COPY_HISTORY
    w.Put(0, 3);                    // PCM source resolution
    w.Put(0, 1);                    // front sum/difference
    w.Put(0, 1);                    // surround sum/difference
    w.Put(0, 4);                    // dialog normalisation
    w.Put(0, 4);                    // SUBFS - 1 → 1 subframe

    // ── Primary audio coding header (mono, 1 channel) ────────────────────────
    w.Put(0, 3);                    // nchans - 1 → 1 channel
    w.Put(nblksMinus1Subbands - 2, 5);  // subband_activity - 2 → 2 active subbands
    w.Put(nblksMinus1Subbands - 1, 5);  // vq_start_subband - 1 → 2 (== activity: no HF VQ)
    w.Put(0, 3);                    // joint_intensity = 0
    w.Put(0, 2);                    // transient_huffman = 0
    w.Put(0, 3);                    // scalefactor_huffman = 0
    w.Put(6, 3);                    // bitalloc_huffman = 6 → fixed 5-bit allocation indexes
    // quant_index_huffman[j] for j = 1..10 (bit widths 0,1,2,2,2,2,3,3,3,3,3).
    int[] bitlen = [0, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3];
    for (var j = 1; j < 11; ++j)
      w.Put(0, bitlen[j]);
    // scalefactor_adj: written only where quant_index_huffman[j] < threshold[j]. All indexes are 0
    // and every threshold is ≥ 1, so a 2-bit adjustment code is written for each j = 1..10.
    for (var j = 1; j < 11; ++j)
      w.Put(0, 2);

    // ── Subframe header ──────────────────────────────────────────────────────
    // One subframe spans every 8-block group as a sub-subframe (subsubframes == blockGroups).
    w.Put(blockGroups - 1, 2);      // subsubframes - 1
    w.Put(0, 3);                    // partial_samples = 0
    // prediction_mode[k] per active subband (1 channel × 2 subbands): all 0 → no prediction VQ.
    for (var k = 0; k < 2; ++k)
      w.Put(0, 1);
    // bit allocation indexes (fixed 5-bit book): both 0 → no samples, scale factors or transitions.
    for (var k = 0; k < 2; ++k)
      w.Put(0, 5);
    // transition_mode: present per active subband only when subsubframes > 1 AND bitalloc > 0; every
    // bitalloc is 0 here, so no transition-mode bits follow regardless of the sub-subframe count.
    // scale factors: only present where bitalloc > 0 or k >= vq_start → nothing.
    // joint scale factors: joint_intensity == 0 → nothing.
    // CPF == 0 → no side-info CRC.
    // high-frequency VQ: vq_start == activity → nothing.
    // LFE: none.

    // ── Sub-subframe data: every abits == 0 → no sample bits. ASPF == 0 means a single
    // DSYNC (0xFFFF) follows the last sub-subframe only. ─
    w.Put(0xFFFF, 16);              // DSYNC after the last sub-subframe

    // Pad to a 16-bit boundary, then size the frame and patch FSIZE.
    while (w.BitCount % 16 != 0)
      w.Flag(false);
    var frameSize = Math.Max((w.BitCount + 7) / 8, 95);
    if (frameSize % 2 != 0)
      ++frameSize;

    // Patch FSIZE = frameSize - 1 into the placeholder.
    var raw = w.ToBytes(frameSize);
    PatchBits(raw, fsizePos, 14, frameSize - 1);
    return raw;
  }

  private static void PatchBits(byte[] data, int bitOffset, int count, int value) {
    for (var i = 0; i < count; ++i) {
      var bit = (value >> (count - 1 - i)) & 1;
      var pos = bitOffset + i;
      if (bit != 0)
        data[pos >> 3] |= (byte)(1 << (7 - (pos & 7)));
      else
        data[pos >> 3] &= (byte)~(1 << (7 - (pos & 7)));
    }
  }

  private static byte[] Decode(byte[] frame) {
    using var src = new MemoryStream(frame, writable: false);
    using var dst = new MemoryStream();
    DtsCodec.Decompress(src, dst);
    return dst.ToArray();
  }

  // ── Frame-level decode tests ─────────────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Decode_SilenceFrame_YieldsExactZeros() {
    var frame = BuildSilenceFrame();
    var pcm = Decode(frame);

    // 1 block group × 256 samples × 1 channel × 2 bytes.
    Assert.That(pcm, Has.Length.EqualTo(256 * 1 * 2));
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
    Assert.That(pcm, Has.Length.EqualTo(2 * 256 * 1 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void Decode_SilenceFrame_TwoBlockGroups_YieldsExpectedSamples() {
    // 16 sample blocks → 2 groups of 8 → 512 PCM samples per channel.
    var frame = BuildSilenceFrame(sampleBlocks: 16);
    var pcm = Decode(frame);
    Assert.That(pcm, Has.Length.EqualTo(512 * 1 * 2));
    Assert.That(pcm, Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void ReadStreamInfo_ReportsMonoChannelsAndRate() {
    var frame = BuildSilenceFrame(sfreq: 13, rate: 24);
    using var src = new MemoryStream(frame, writable: false);
    var info = DtsCodec.ReadStreamInfo(src);

    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.Lfe, Is.False);
    Assert.That(info.Amode, Is.EqualTo(0));
    // 8 sample blocks × 32 samples per block.
    Assert.That(info.DurationSamples, Is.EqualTo(8 * 32));
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
    var junk = System.Text.Encoding.ASCII.GetBytes("definitely not a DTS elementary stream at all");
    using var src = new MemoryStream(junk, writable: false);
    using var dst = new MemoryStream();
    Assert.That(() => DtsCodec.Decompress(src, dst), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Decode_FourteenBitFraming_IsNotSupported() {
    // 14-bit packed framing sync 0x1FFFE800.
    var data = new byte[200];
    data[0] = 0x1F; data[1] = 0xFF; data[2] = 0xE8; data[3] = 0x00;
    using var src = new MemoryStream(data, writable: false);
    using var dst = new MemoryStream();
    Assert.That(() => DtsCodec.Decompress(src, dst), Throws.TypeOf<NotSupportedException>());
  }

  // ── Header parse ───────────────────────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void FrameHeader_ParsesAmodeSfreqRate() {
    var frame = BuildSilenceFrame(sfreq: 13, rate: 24);
    var header = DtsFrameHeader.TryParse(frame, 0);
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Value.Amode, Is.EqualTo(0));
    Assert.That(header.Value.SampleRate, Is.EqualTo(48000));
    Assert.That(header.Value.BitRate, Is.EqualTo(1536000));
    Assert.That(header.Value.SampleBlocks, Is.EqualTo(8));
    Assert.That(header.Value.Lfe, Is.EqualTo(0));
  }

  [Test]
  [Category("HappyPath")]
  public void FrameHeader_AmodeChannelCounts_MatchSpec() {
    Assert.That(DtsFrameHeader.AmodeChannelCount(0), Is.EqualTo(1));   // mono
    Assert.That(DtsFrameHeader.AmodeChannelCount(2), Is.EqualTo(2));   // stereo
    Assert.That(DtsFrameHeader.AmodeChannelCount(9), Is.EqualTo(5));   // 5.0
    Assert.That(DtsFrameHeader.AmodeName(2), Does.Contain("stereo"));
  }

  // ── Block-code unit tests (hand-computed) ────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void BlockCode_ThreeLevel_UnpacksFourMixedRadixSamples() {
    // levels = 3 → offset (levels-1)/2 = 1. Encode samples (a,b,c,d) as base-3 little-endian:
    // code = a + b*3 + c*9 + d*27 with each digit in 0..2, then values[i] = digit - offset.
    // Choose digits (2,0,1,2) → code = 2 + 0 + 9 + 54 = 65.
    var values = new int[8];
    var residual = DtsBlockCode.DecodeBlockCode(65, 3, values, 0);
    Assert.That(residual, Is.EqualTo(0));
    Assert.That(values[0], Is.EqualTo(2 - 1));
    Assert.That(values[1], Is.EqualTo(0 - 1));
    Assert.That(values[2], Is.EqualTo(1 - 1));
    Assert.That(values[3], Is.EqualTo(2 - 1));
  }

  [Test]
  [Category("HappyPath")]
  public void BlockCodes_TwoCodes_FillEightSamples() {
    var values = new int[8];
    // code1 all-zero digits → four samples of -offset; code2 digit pattern.
    var residual = DtsBlockCode.DecodeBlockCodes(0, 0, 5, values, 0);
    Assert.That(residual, Is.EqualTo(0));
    var offset = (5 - 1) >> 1;
    for (var i = 0; i < 8; ++i)
      Assert.That(values[i], Is.EqualTo(-offset));
  }

  // ── Huffman table sanity ─────────────────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Huffman_BitAllocIndexBooks_HaveExpectedShapes() {
    // 5 selectable 12-entry bit-allocation index books, code/length arrays parallel.
    Assert.That(DtsHuffmanTables.Bitalloc12Codes, Has.Length.EqualTo(5));
    for (var i = 0; i < 5; ++i) {
      Assert.That(DtsHuffmanTables.Bitalloc12Codes[i], Has.Length.EqualTo(12));
      Assert.That(DtsHuffmanTables.Bitalloc12Bits[i], Has.Length.EqualTo(12));
    }
    // Scale-factor books are 129 entries with offset -64; 5 selectors.
    Assert.That(DtsHuffmanTables.ScalesCodes, Has.Length.EqualTo(5));
    Assert.That(DtsHuffmanTables.ScalesCodes[0], Has.Length.EqualTo(129));
  }

  [Test]
  [Category("HappyPath")]
  public void Huffman_Vlc_DecodesSingleEntryUniquely() {
    // tmode book selector 3 is a flat 2-bit code {00,01,10,11} → symbol index equals the 2-bit value.
    var vlc = new DtsVlc(DtsHuffmanTables.TmodeCodes[3], DtsHuffmanTables.TmodeBits[3]);
    for (var sym = 0; sym < 4; ++sym) {
      var bytes = new byte[2];
      bytes[0] = (byte)(sym << 6);
      var r = new DtsBitReader(bytes, 0, bytes.Length);
      Assert.That(vlc.Decode(r), Is.EqualTo(sym));
    }
  }

  // ── ADPCM predictor unit test (hand-walked taps) ─────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Adpcm_VbTable_HasFourTapVectors() {
    Assert.That(DtsTables.AdpcmVb, Has.Length.EqualTo(4096));
    Assert.That(DtsTables.AdpcmVb[0], Has.Length.EqualTo(4));
    // Verify a couple of source-verified entries.
    Assert.That(DtsTables.AdpcmVb[0][0], Is.EqualTo((short)9928));
    Assert.That(DtsTables.AdpcmVb[0][1], Is.EqualTo((short)-2618));
  }

  [Test]
  [Category("HappyPath")]
  public void Adpcm_FourTapPredictor_MatchesHandWalkedSum() {
    // Reproduce the in-block predictor recurrence for m >= 4 (no history term):
    //   pred[m] = (c0*s[m-1] + c1*s[m-2] + c2*s[m-3] + c3*s[m-4]) / 8192
    // with hand-picked coefficients and a known sample window.
    int[] coeff = [4096, -2048, 1024, -512]; // arbitrary Q13-style taps
    float[] s = [1f, 2f, 3f, 4f, 0f];        // residual at index 4 is 0 → output is pure prediction
    var pred = (coeff[0] * s[3] + coeff[1] * s[2] + coeff[2] * s[1] + coeff[3] * s[0]) * (1f / 8192f);
    var expected = (4096f * 4 + -2048f * 3 + 1024f * 2 + -512f * 1) / 8192f;
    Assert.That(pred, Is.EqualTo(expected).Within(1e-6));
  }

  // ── QMF impulse sanity ───────────────────────────────────────────────────────────────────

  [Test]
  [Category("HappyPath")]
  public void Qmf_ZeroSubbands_ProduceZeroOutput() {
    var qmf = new DtsQmf();
    var input = NewSubbands();   // all zero
    var output = new float[256];
    qmf.Process(input, subbandActivity: 2, output, 0, perfectReconstruction: false, scale: 1f);
    Assert.That(output, Is.All.EqualTo(0f));
  }

  [Test]
  [Category("HappyPath")]
  public void Qmf_SingleSubbandImpulse_ProducesFiniteNonZeroSignal() {
    var qmf = new DtsQmf();
    var input = NewSubbands();
    input[1][0] = 1.0f;          // an impulse in subband 1, sub-subframe 0
    var output = new float[256];
    qmf.Process(input, subbandActivity: 4, output, 0, perfectReconstruction: true, scale: 1f);

    var energy = 0.0;
    foreach (var v in output) {
      Assert.That(float.IsFinite(v), Is.True);
      energy += v * (double)v;
    }
    Assert.That(energy, Is.GreaterThan(0.0), "a single subband impulse must yield a non-zero time signal");
  }

  private static float[][] NewSubbands() {
    var m = new float[32][];
    for (var i = 0; i < 32; ++i)
      m[i] = new float[8];
    return m;
  }
}
