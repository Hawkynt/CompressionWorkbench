#pragma warning disable CS1591
using Codec.Xma;

namespace Compression.Tests.Codecs.Wma;

/// <summary>
/// Pins the Microsoft XMA1/XMA2 framing parser and decode orchestrator: the XMA2
/// packet header layout (frame count, previous-frame bit count, packet-skip count), the
/// XMA1/XMA2 extradata stream-configuration (number of WMA Pro elementary streams and
/// their per-stream channel counts), and the graceful fallback the orchestrator takes
/// when a synthetic / unsupported bitstream can't be driven through the per-stream WMA Pro
/// decode. Hand-built packets exercise the bit-exact header arithmetic.
/// </summary>
[TestFixture]
public class XmaCodecTests {

  // MSB-first bit writer for crafting packet headers.
  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur, _n;
    public void Put(int bits, uint value) {
      for (var i = bits - 1; i >= 0; --i) {
        this._cur = (this._cur << 1) | (int)((value >> i) & 1);
        if (++this._n == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._n = 0; }
      }
    }
    public byte[] ToArray(int minBytes) {
      var outBytes = new List<byte>(this._bytes);
      if (this._n > 0) outBytes.Add((byte)(this._cur << (8 - this._n)));
      while (outBytes.Count < minBytes) outBytes.Add(0);
      return outBytes.ToArray();
    }
  }

  // ── XMA2 packet header parse ──────────────────────────────────────────────

  [Test]
  public void Header_Xma2_ReadsFrameCountPrevBitsAndSkip() {
    // log2_frame_size = log2(2048) + 4 = 15. XMA2 header: numFrames(6), prevBits(15),
    // reserved(3), skipPackets(8).
    var bw = new BitWriter();
    bw.Put(6, 5);           // 5 frames open in this packet
    bw.Put(15, 1234);       // num_bits_prev_frame
    bw.Put(3, 0);           // reserved
    bw.Put(8, 7);           // skip_packets
    var pkt = bw.ToArray(XmaPacket.PacketSize);

    var h = XmaPacket.ParseHeader(pkt, isXma2: true, XmaPacket.PacketSize);
    Assert.That(h.NumFrames, Is.EqualTo(5));
    Assert.That(h.NumBitsPrevFrame, Is.EqualTo(1234));
    Assert.That(h.SkipPackets, Is.EqualTo(7));
    Assert.That(h.HeaderBits, Is.EqualTo(6 + 15 + 3 + 8));
  }

  [Test]
  public void Header_Xma1_ReadsSequenceThenPrevBitsAndSkip() {
    // XMA1 header: seq(4), reserved(2), prevBits(15), reserved(3), skipPackets(8).
    var bw = new BitWriter();
    bw.Put(4, 9);           // sequence number (discarded)
    bw.Put(2, 0);
    bw.Put(15, 64);         // num_bits_prev_frame
    bw.Put(3, 0);
    bw.Put(8, 3);           // skip_packets
    var pkt = bw.ToArray(XmaPacket.PacketSize);

    var h = XmaPacket.ParseHeader(pkt, isXma2: false, XmaPacket.PacketSize);
    Assert.That(h.NumFrames, Is.EqualTo(0)); // XMA1 has no frame count field
    Assert.That(h.NumBitsPrevFrame, Is.EqualTo(64));
    Assert.That(h.SkipPackets, Is.EqualTo(3));
    Assert.That(h.HeaderBits, Is.EqualTo(4 + 2 + 15 + 3 + 8));
  }

  // ── XMA2WAVEFORMATEX stream config (channel split) ────────────────────────

  [Test]
  public void StreamConfig_Xma2WaveformatEx_SplitsChannels2chThen1ch() {
    // 34-byte extradata, 5 channels → streams of 2,2,1.
    var cfg = XmaPacket.ParseStreamConfig(new byte[34], isXma2: true, declaredChannels: 5);
    Assert.That(cfg.IsXma2, Is.True);
    Assert.That(cfg.NumStreams, Is.EqualTo(3));
    Assert.That(cfg.StreamChannels, Is.EqualTo(new[] { 2, 2, 1 }));
    Assert.That(cfg.TotalChannels, Is.EqualTo(5));
  }

  [Test]
  public void StreamConfig_Xma2WaveformatEx_StereoIsOneStream() {
    var cfg = XmaPacket.ParseStreamConfig(new byte[34], isXma2: true, declaredChannels: 2);
    Assert.That(cfg.NumStreams, Is.EqualTo(1));
    Assert.That(cfg.StreamChannels, Is.EqualTo(new[] { 2 }));
  }

  [Test]
  public void StreamConfig_Xma2Waveformat_ReadsNumStreamsAndPerStreamChannels() {
    // Non-EX XMA2WAVEFORMAT: extradata[0] != 3 → num_streams at [9], channels at
    // [32 + 8 + 4*n]. Build 2 streams of 2 and 1 channels.
    var e = new byte[64];
    e[0] = 4;          // not the legacy v3 marker
    e[9] = 2;          // num_streams
    e[32 + 8 + 0] = 2; // stream 0 channels
    e[32 + 8 + 4] = 1; // stream 1 channels
    var cfg = XmaPacket.ParseStreamConfig(e, isXma2: true, declaredChannels: 3);
    Assert.That(cfg.NumStreams, Is.EqualTo(2));
    Assert.That(cfg.StreamChannels, Is.EqualTo(new[] { 2, 1 }));
    Assert.That(cfg.TotalChannels, Is.EqualTo(3));
  }

  [Test]
  public void StreamConfig_Xma1Waveformat_ReadsPerStreamChannels() {
    // XMA1: num_streams at [4]; per-stream channels at [8 + 20*n + 17].
    var e = new byte[64];
    e[4] = 2;             // num_streams
    e[8 + 20 * 0 + 17] = 2;
    e[8 + 20 * 1 + 17] = 2;
    var cfg = XmaPacket.ParseStreamConfig(e, isXma2: false, declaredChannels: 4);
    Assert.That(cfg.IsXma2, Is.False);
    Assert.That(cfg.NumStreams, Is.EqualTo(2));
    Assert.That(cfg.StreamChannels, Is.EqualTo(new[] { 2, 2 }));
  }

  // ── orchestrator ──────────────────────────────────────────────────────────

  [Test]
  public void Codec_ExposesParsedConfig() {
    var codec = new XmaCodec(new byte[34], isXma2: true, sampleRate: 44100, declaredChannels: 4);
    Assert.That(codec.Channels, Is.EqualTo(4));
    Assert.That(codec.Config.NumStreams, Is.EqualTo(2));
    Assert.That(codec.SampleRate, Is.EqualTo(44100));
  }

  [Test]
  public void TryDecode_TooSmallInput_FallsBackGracefully() {
    var codec = new XmaCodec(new byte[34], isXma2: true, sampleRate: 44100, declaredChannels: 2);
    var ok = codec.TryDecode(new byte[100], out var pcm);
    Assert.That(ok, Is.False);
    Assert.That(pcm, Is.Empty);
  }

  [Test]
  public void TryDecode_SyntheticPackets_DoesNotThrow_AndFallsBackOrDecodes() {
    // A blob of zeroed 2KB packets is not a valid XMA stream; the orchestrator must not
    // throw and must report graceful fallback (false) rather than fabricate PCM.
    var codec = new XmaCodec(new byte[34], isXma2: true, sampleRate: 44100, declaredChannels: 2);
    var blob = new byte[XmaPacket.PacketSize * 3];
    bool ok = true;
    Assert.DoesNotThrow(() => ok = codec.TryDecode(blob, out _));
    Assert.That(ok, Is.False);
  }

  [Test]
  public void Codec_RejectsZeroStreamConfig() {
    // XMA1 extradata declaring zero streams is invalid.
    var e = new byte[64]; // e[4] == 0 → 0 streams
    Assert.Throws<InvalidDataException>(() =>
      new XmaCodec(e, isXma2: false, sampleRate: 44100, declaredChannels: 2));
  }
}
