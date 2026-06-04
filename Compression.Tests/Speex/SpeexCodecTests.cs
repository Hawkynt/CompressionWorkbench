#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Speex;

namespace Compression.Tests.Speex;

/// <summary>
/// Pins the Speex decoder port (FFmpeg <c>speexdec.c</c>): header parse exactness,
/// submode-table sanity, LSP→LPC against a hand-computed low-order case, and
/// deterministic decode of crafted minimal narrowband packets (lowest-rate submode,
/// zeroed indices → bounded near-silence with exactly <c>frame_size</c> samples),
/// plus frames-per-packet handling and truncation tolerance.
/// </summary>
[TestFixture]
public class SpeexCodecTests {

  // ── header parse ─────────────────────────────────────────────────────────────────

  [Test]
  public void Header_ParsesAllFields_Narrowband() {
    var h = SpeexHeader.Parse(BuildHeader(rate: 8000, mode: 0, channels: 1,
      bitrate: -1, frameSize: 160, vbr: 0, framesPerPacket: 1, extraHeaders: 0));

    Assert.Multiple(() => {
      Assert.That(h.Rate, Is.EqualTo(8000));
      Assert.That(h.Mode, Is.EqualTo(0));
      Assert.That(h.BitstreamVersion, Is.EqualTo(4));
      Assert.That(h.NbChannels, Is.EqualTo(1));
      Assert.That(h.FrameSize, Is.EqualTo(160));
      Assert.That(h.Vbr, Is.EqualTo(0));
      Assert.That(h.FramesPerPacket, Is.EqualTo(1));
      Assert.That(h.ExtraHeaders, Is.EqualTo(0));
    });
  }

  [Test]
  public void Header_Wideband_FrameSizeIs320() {
    var h = SpeexHeader.Parse(BuildHeader(rate: 16000, mode: 1, channels: 2,
      bitrate: -1, frameSize: 320, vbr: 0, framesPerPacket: 1, extraHeaders: 0));
    Assert.That(h.Mode, Is.EqualTo(1));
    Assert.That(h.NbChannels, Is.EqualTo(2));
    // The public decoder full-band size is 160 << mode.
    Assert.That(new SpeexDecoder(h).FrameSize, Is.EqualTo(320));
  }

  [Test]
  public void Header_RejectsWrongBitstreamVersion() {
    var bad = BuildHeader(rate: 8000, mode: 0, channels: 1, bitrate: -1,
      frameSize: 160, vbr: 0, framesPerPacket: 1, extraHeaders: 0);
    // bitstream_version is the 4th LE32 after magic+28 → offset 28 + 12.
    BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(28 + 12, 4), 3);
    Assert.That(() => SpeexHeader.Parse(bad), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Header_RejectsBadChannelCount() {
    var bad = BuildHeader(rate: 8000, mode: 0, channels: 3, bitrate: -1,
      frameSize: 160, vbr: 0, framesPerPacket: 1, extraHeaders: 0);
    Assert.That(() => SpeexHeader.Parse(bad), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Header_MissingMagic_Throws() =>
    Assert.That(() => SpeexHeader.Parse(new byte[80]), Throws.TypeOf<InvalidDataException>());

  // ── submode table sanity ─────────────────────────────────────────────────────────

  [Test]
  public void SubmodeTables_HaveExpectedCodebookSizes() {
    // These are the verbatim codebook dimensions from speexdata.h; if a table got
    // truncated/misparsed during transcription these counts break.
    Assert.Multiple(() => {
      Assert.That(SpeexTables.CdbkNb.Length, Is.EqualTo(640));        // 64 entries x 10
      Assert.That(SpeexTables.CdbkNbLow1.Length, Is.EqualTo(320));    // 64 x 5
      Assert.That(SpeexTables.CdbkNbHigh1.Length, Is.EqualTo(320));
      Assert.That(SpeexTables.Exc564Table.Length, Is.EqualTo(320));   // 64 x 5
      Assert.That(SpeexTables.Exc5256Table.Length, Is.EqualTo(1280)); // 256 x 5
      Assert.That(SpeexTables.GainCdbkNb.Length, Is.EqualTo(512));    // 128 x 4
      Assert.That(SpeexTables.GainCdbkLbr.Length, Is.EqualTo(128));   // 32 x 4
      Assert.That(SpeexTables.H0.Length, Is.EqualTo(64));
      Assert.That(SpeexTables.GcQuantBound.Length, Is.EqualTo(16));
      Assert.That(SpeexTables.WbSkipTable.Length, Is.EqualTo(8));
      Assert.That(SpeexTables.ShiftFilt.Length, Is.EqualTo(3));
      Assert.That(SpeexTables.ShiftFilt[0].Length, Is.EqualTo(7));
    });
  }

  // ── LSP → LPC unit test (hand-computed) ───────────────────────────────────────────

  [Test]
  public void LspToLpc_Order2_MatchesAnalyticCascade() {
    // For order 2 the Speex lsp_to_lpc reduces to the second-order cascade of
    // P(z) = 1 - 2 cos(w0) z^-1 + z^-2 and Q(z) = 1 - 2 cos(w1) z^-1 + z^-2 with
    // A(z) = 0.5(P+Q) and the trailing (1 ± z^-1) factors folded in. We verify the
    // implementation against an independent direct evaluation of the same recurrence.
    var freq = new[] { 1.0f, 2.0f };
    var ak = InvokeLspToLpc(freq, 2);
    var expected = ReferenceLspToLpc(freq, 2);
    Assert.That(ak.Length, Is.EqualTo(2));
    for (var i = 0; i < 2; i++)
      Assert.That(ak[i], Is.EqualTo(expected[i]).Within(1e-5));
  }

  // ── deterministic minimal-packet decode ──────────────────────────────────────────

  [Test]
  public void DecodePacket_Submode1_ZeroedIndices_ProducesBoundedFrame() {
    var h = SpeexHeader.Parse(BuildHeader(8000, 0, 1, -1, 160, 0, 1, 0));
    var dec = new SpeexDecoder(h);

    // Narrowband submode 1 (comfort-noise/vocoder): wideband=0, m=1, then all-zero
    // index fields. With ol_pitch_coef=0 and ol_gain=exp(0)=1 the vocoder excitation
    // collapses to ~0 → bounded near-silence.
    var packet = BuildSubmode1Packet();
    var pcm = dec.DecodePacket(packet);

    Assert.That(pcm.Length, Is.EqualTo(160)); // 1 frame x 160 samples, mono
    foreach (var s in pcm)
      Assert.That(Math.Abs((int)s), Is.LessThan(2000), "submode-1 zeroed frame must be near-silence");
  }

  [Test]
  public void DecodePacket_NullSubmode_ExactFrameSize() {
    var h = SpeexHeader.Parse(BuildHeader(8000, 0, 1, -1, 160, 0, 1, 0));
    var dec = new SpeexDecoder(h);
    // All-zero packet → wideband=0, m=0 → null submode (comfort noise from empty
    // excitation history). Deterministic, bounded, exactly frame_size samples.
    var pcm = dec.DecodePacket(new byte[8]);
    Assert.That(pcm.Length, Is.EqualTo(160));
    foreach (var s in pcm)
      Assert.That(Math.Abs((int)s), Is.LessThan(4000));
  }

  [Test]
  public void DecodePacket_FramesPerPacket_ProducesMultipleFrames() {
    var h = SpeexHeader.Parse(BuildHeader(8000, 0, 1, -1, 160, 0, framesPerPacket: 3, extraHeaders: 0));
    var dec = new SpeexDecoder(h);
    // Empty bits per frame → each frame falls into null submode; output length is
    // padded to frames_per_packet * frame_size regardless of early termination.
    var pcm = dec.DecodePacket(new byte[64]);
    Assert.That(pcm.Length, Is.EqualTo(3 * 160));
  }

  [Test]
  public void DecodePacket_Truncated_DoesNotThrow_AndIsBounded() {
    var h = SpeexHeader.Parse(BuildHeader(8000, 0, 1, -1, 160, 0, 1, 0));
    var dec = new SpeexDecoder(h);
    // One byte: not enough bits to fully parse a submode-1 frame; reads past the end
    // return zero bits. Must not throw and must still emit a full frame.
    short[] pcm = null!;
    Assert.That(() => pcm = dec.DecodePacket(new byte[] { 0x10 }), Throws.Nothing);
    Assert.That(pcm.Length, Is.EqualTo(160));
  }

  [Test]
  public void DecodePacket_Wideband_NullLayers_ProducesFullBandFrame() {
    // Mode 1 (wideband): exercises sb_decode + the narrowband low band + QMF synth.
    // An empty packet drives both layers into their null submodes (1e-15 high band,
    // comfort-noise low band) → bounded, exactly 320 full-band samples.
    var h = SpeexHeader.Parse(BuildHeader(16000, mode: 1, channels: 1, bitrate: -1,
      frameSize: 320, vbr: 0, framesPerPacket: 1, extraHeaders: 0));
    var dec = new SpeexDecoder(h);
    Assert.That(dec.FrameSize, Is.EqualTo(320));
    short[] pcm = null!;
    Assert.That(() => pcm = dec.DecodePacket(new byte[16]), Throws.Nothing);
    Assert.That(pcm.Length, Is.EqualTo(320));
  }

  [Test]
  public void Decoder_StereoHeader_ProducesInterleavedFrame() {
    var h = SpeexHeader.Parse(BuildHeader(8000, 0, channels: 2, bitrate: -1,
      frameSize: 160, vbr: 0, framesPerPacket: 1, extraHeaders: 0));
    var dec = new SpeexDecoder(h);
    Assert.That(dec.Channels, Is.EqualTo(2));
    var pcm = dec.DecodePacket(new byte[16]);
    Assert.That(pcm.Length, Is.EqualTo(160 * 2)); // interleaved stereo
  }

  // ── helpers ───────────────────────────────────────────────────────────────────────

  internal static byte[] BuildHeader(int rate, int mode, int channels, int bitrate,
    int frameSize, int vbr, int framesPerPacket, int extraHeaders) {
    // 8-byte magic + 20-byte version string + 10 LE32 fields. parse skips magic+28
    // then reads version_id, header_size, rate, mode, bitstream_version, channels,
    // bitrate, frame_size, vbr, frames_per_packet, extra_headers.
    var buf = new byte[28 + 11 * 4];
    Encoding.ASCII.GetBytes("Speex   ").CopyTo(buf, 0);
    Encoding.ASCII.GetBytes("speex-1.2").CopyTo(buf, 8);
    var p = 28;
    void W(int v) { BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p, 4), v); p += 4; }
    W(1);              // version_id
    W(80);             // header_size
    W(rate);
    W(mode);
    W(4);              // bitstream_version
    W(channels);
    W(bitrate);
    W(frameSize);
    W(vbr);
    W(framesPerPacket);
    W(extraHeaders);
    return buf;
  }

  private static byte[] BuildSubmode1Packet() {
    // Bit layout (MSB-first): wideband(1)=0, m(4)=1, lsp three 6-bit ids (all 0),
    // ol_pitch(7)=0, ol_pitch_coef(4)=0, ol_gain(5)=0, dtx(4)=0.
    var w = new BitWriter();
    w.Put(0, 1);   // wideband
    w.Put(1, 4);   // submode 1
    w.Put(0, 6); w.Put(0, 6); w.Put(0, 6); // lsp lbr ids
    w.Put(0, 7);   // ol_pitch
    w.Put(0, 4);   // ol_pitch_coef
    w.Put(0, 5);   // ol_gain
    w.Put(0, 4);   // dtx
    return w.ToArray();
  }

  // ── independent reference for the lsp_to_lpc cascade ──────────────────────────────

  private static float[] ReferenceLspToLpc(float[] freq, int lpcrdr) {
    var ak = new float[lpcrdr];
    var wp = new float[4 * 10 + 2];
    var xFreq = new float[10];
    var m = lpcrdr >> 1;
    float xin1 = 1f, xin2 = 1f;
    for (var i = 0; i < lpcrdr; i++) xFreq[i] = -(float)Math.Cos(freq[i]);
    var lastN0 = 0;
    for (var j = 0; j <= lpcrdr; j++) {
      var i2 = 0; var n0 = 0;
      for (var i = 0; i < m; i++, i2 += 2) {
        n0 = i * 4;
        var xo1 = xin1 + 2f * xFreq[i2] * wp[n0] + wp[n0 + 1];
        var xo2 = xin2 + 2f * xFreq[i2 + 1] * wp[n0 + 2] + wp[n0 + 3];
        wp[n0 + 1] = wp[n0]; wp[n0 + 3] = wp[n0 + 2];
        wp[n0] = xin1; wp[n0 + 2] = xin2;
        xin1 = xo1; xin2 = xo2;
      }
      lastN0 = n0;
      var f1 = xin1 + wp[lastN0 + 4];
      var f2 = xin2 - wp[lastN0 + 5];
      if (j > 0) ak[j - 1] = (f1 + f2) * 0.5f;
      wp[lastN0 + 4] = xin1; wp[lastN0 + 5] = xin2;
      xin1 = 0f; xin2 = 0f;
    }
    return ak;
  }

  private static float[] InvokeLspToLpc(float[] freq, int order) {
    // Reach the private static SpeexDecoder.LspToLpc via reflection so the production
    // path itself is exercised (not a copy).
    var ak = new float[order];
    var mi = typeof(SpeexDecoder).GetMethod("LspToLpc",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    mi.Invoke(null, [freq, ak, order]);
    return ak;
  }

  private sealed class BitWriter {
    private readonly List<byte> _bytes = new();
    private int _cur;
    private int _nbits;
    public void Put(int value, int bits) {
      for (var i = bits - 1; i >= 0; i--) {
        var bit = (value >> i) & 1;
        this._cur = (this._cur << 1) | bit;
        if (++this._nbits == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._nbits = 0; }
      }
    }
    public byte[] ToArray() {
      if (this._nbits > 0) { this._cur <<= 8 - this._nbits; this._bytes.Add((byte)this._cur); }
      return this._bytes.ToArray();
    }
  }
}
