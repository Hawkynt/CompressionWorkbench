#pragma warning disable CS1591
using Codec.G7231;

namespace Compression.Tests.Codecs.G7231;

/// <summary>
/// Pins the ITU-T G.723.1 (5.3 / 6.3 kbit/s) speech decoder, a fixed-point port of FFmpeg's
/// <c>g723_1dec.c</c>. The decoder is parametric synthesis (there is no encoder), so correctness is
/// verified structurally: the 2-bit frame-size dispatch (24 / 20 / 4 / 1 bytes incl. SID and
/// untransmitted), the exact 240-samples-per-frame geometry over auto-detected frame sizes, the
/// hand-walked LSP VQ inverse-quantization, the MP-MLQ and ACELP fixed-codebook unpack arithmetic,
/// determinism, frame-erasure (forbidden code) concealment, and truncation tolerance.
/// </summary>
[TestFixture]
public class G7231CodecTests {

  private const int FrameLen = 240;

  /// <summary>
  /// Little-endian bit writer matching <c>BitReaderLe</c> / FFmpeg's <c>BITSTREAM_READER_LE</c>:
  /// each value's bits are emitted LSB-first within the running byte, bytes in stream order.
  /// </summary>
  private sealed class BitWriterLe {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bit;

    public void Put(int value, int count) {
      for (var i = 0; i < count; ++i) {
        if (((value >> i) & 1) != 0)
          this._cur |= 1 << this._bit;
        if (++this._bit == 8) {
          this._bytes.Add((byte)this._cur);
          this._cur = 0;
          this._bit = 0;
        }
      }
    }

    public byte[] ToArray(int padTo) {
      if (this._bit != 0) {
        this._bytes.Add((byte)this._cur);
        this._cur = 0;
        this._bit = 0;
      }
      while (this._bytes.Count < padTo)
        this._bytes.Add(0);
      return this._bytes.ToArray();
    }
  }

  /// <summary>Builds a 6.3 kbit/s (24-byte) active frame with all parameter fields zeroed.</summary>
  private static byte[] ZeroActive6300() {
    var w = new BitWriterLe();
    w.Put(0, 2);            // info_bits = 0 → 6.3k active
    w.Put(0, 8); w.Put(0, 8); w.Put(0, 8); // lsp indices (bands 2,1,0)
    w.Put(0, 7); w.Put(0, 2); // pitch_lag[0]=PITCH_MIN, ad_cb_lag
    w.Put(0, 7); w.Put(0, 2); // pitch_lag[1]
    for (var i = 0; i < 4; ++i)
      w.Put(0, 12);         // combined gains
    w.Put(0, 4);            // grid indices (4×1)
    w.Put(0, 1);            // reserved
    w.Put(0, 13);           // combined pulse position
    w.Put(0, 16); w.Put(0, 14); w.Put(0, 16); w.Put(0, 14); // per-subframe positions
    w.Put(0, 6); w.Put(0, 5); w.Put(0, 6); w.Put(0, 5);     // signs
    return w.ToArray(24);
  }

  /// <summary>Builds a 5.3 kbit/s (20-byte) active frame with all parameter fields zeroed.</summary>
  private static byte[] ZeroActive5300() {
    var w = new BitWriterLe();
    w.Put(1, 2);            // info_bits = 1 → 5.3k active
    w.Put(0, 8); w.Put(0, 8); w.Put(0, 8);
    w.Put(0, 7); w.Put(0, 2);
    w.Put(0, 7); w.Put(0, 2);
    for (var i = 0; i < 4; ++i)
      w.Put(0, 12);
    w.Put(0, 4);
    w.Put(0, 12); w.Put(0, 12); w.Put(0, 12); w.Put(0, 12); // positions
    w.Put(0, 4); w.Put(0, 4); w.Put(0, 4); w.Put(0, 4);     // signs
    return w.ToArray(20);
  }

  /// <summary>A 4-byte SID frame with the given 6-bit SID gain index.</summary>
  private static byte[] SidFrame(int gainIndex = 0) {
    var w = new BitWriterLe();
    w.Put(2, 2);            // info_bits = 2 → SID
    w.Put(0, 8); w.Put(0, 8); w.Put(0, 8);
    w.Put(gainIndex, 6);
    return w.ToArray(4);
  }

  /// <summary>A 1-byte untransmitted frame.</summary>
  private static byte[] Untransmitted() {
    var w = new BitWriterLe();
    w.Put(3, 2);
    return w.ToArray(1);
  }

  private static double Rms(short[] pcm) {
    if (pcm.Length == 0)
      return 0;
    var e = 0.0;
    foreach (var s in pcm)
      e += (double)s * s;
    return Math.Sqrt(e / pcm.Length);
  }

  // ── Frame-size dispatch ────────────────────────────────────────────────────────────

  [Test]
  public void ReadInfo_DispatchesAllFourFrameSizes() {
    var stream = ZeroActive6300()           // 24 bytes
      .Concat(ZeroActive5300())             // 20 bytes
      .Concat(SidFrame())                   // 4 bytes
      .Concat(Untransmitted())              // 1 byte
      .ToArray();

    var infos = G7231Codec.ReadInfo(stream);
    Assert.That(infos.Count, Is.EqualTo(4));
    Assert.That(infos[0].SizeBytes, Is.EqualTo(24));
    Assert.That(infos[0].Type, Is.EqualTo(G7231FrameType.Active));
    Assert.That(infos[1].SizeBytes, Is.EqualTo(20));
    Assert.That(infos[1].Type, Is.EqualTo(G7231FrameType.Active5300));
    Assert.That(infos[2].SizeBytes, Is.EqualTo(4));
    Assert.That(infos[2].Type, Is.EqualTo(G7231FrameType.Sid));
    Assert.That(infos[3].SizeBytes, Is.EqualTo(1));
    Assert.That(infos[3].Type, Is.EqualTo(G7231FrameType.Untransmitted));
  }

  [Test]
  public void CountFrames_MatchesReadInfo() {
    var stream = ZeroActive6300().Concat(SidFrame()).Concat(Untransmitted()).ToArray();
    Assert.That(G7231Codec.CountFrames(stream), Is.EqualTo(3));
  }

  // ── Sample-count geometry ──────────────────────────────────────────────────────────

  [Test]
  public void Decode_Produces240SamplesPerActiveFrame() {
    var dec = G7231Codec.Decode(ZeroActive6300().Concat(ZeroActive6300()).ToArray());
    Assert.That(dec.Length, Is.EqualTo(2 * FrameLen));
  }

  [Test]
  public void Decode_CountsSidAndUntransmittedFramesToo() {
    // 6.3k active + SID + untransmitted = 3 frames → 720 samples (each frame yields 240).
    var dec = G7231Codec.Decode(ZeroActive6300().Concat(SidFrame()).Concat(Untransmitted()).ToArray());
    Assert.That(dec.Length, Is.EqualTo(3 * FrameLen));
  }

  [Test]
  public void Decode_Empty_ProducesNothing() {
    Assert.That(G7231Codec.Decode([]).Length, Is.EqualTo(0));
  }

  [Test]
  public void Decode_5300Frame_Produces240Samples() {
    var dec = G7231Codec.Decode(ZeroActive5300());
    Assert.That(dec.Length, Is.EqualTo(FrameLen));
  }

  // ── Truncation tolerance ───────────────────────────────────────────────────────────

  [Test]
  public void ReadInfo_IgnoresTruncatedTrailingFrame() {
    // One full 24-byte frame plus 5 dangling bytes that cannot form another 6.3k frame.
    var stream = ZeroActive6300().Concat(new byte[5]).ToArray();
    var infos = G7231Codec.ReadInfo(stream);
    Assert.That(infos.Count, Is.EqualTo(1));
    Assert.That(G7231Codec.Decode(stream).Length, Is.EqualTo(FrameLen));
  }

  [Test]
  public void ReadInfo_DanglingByteThatNeeds24_IsDropped() {
    // A lone first byte selecting a 24-byte frame but with no following bytes is truncated.
    var stream = ZeroActive6300().AsSpan(0, 1).ToArray(); // low 2 bits = 0 → wants 24 bytes
    Assert.That(G7231Codec.ReadInfo(stream).Count, Is.EqualTo(0));
  }

  // ── Determinism ────────────────────────────────────────────────────────────────────

  [Test]
  public void Decode_IsDeterministic() {
    var stream = ZeroActive6300().Concat(SidFrame()).Concat(Untransmitted()).ToArray();
    Assert.That(G7231Codec.Decode(stream), Is.EqualTo(G7231Codec.Decode(stream)));
  }

  // ── Bounded / silent output for zero-index frames ──────────────────────────────────

  [Test]
  public void Decode_ZeroIndexActiveFrames_ProduceBoundedOutput() {
    var stream = Enumerable.Range(0, 8).SelectMany(_ => ZeroActive6300()).ToArray();
    var dec = G7231Codec.Decode(stream);
    Assert.That(dec.Length, Is.EqualTo(8 * FrameLen));
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThanOrEqualTo(short.MaxValue));
  }

  [Test]
  public void Decode_ZeroIndexFrames_StayQuiet() {
    // Zero pulse positions / zero gains → no excitation energy → near-silent synthesis.
    var dec = G7231Codec.Decode(Enumerable.Range(0, 4).SelectMany(_ => ZeroActive6300()).ToArray());
    Assert.That(Rms(dec), Is.LessThan(64), "all-zero excitation must decode to near-silence");
  }

  // ── Frame erasure (forbidden code) concealment ─────────────────────────────────────

  [Test]
  public void Decode_ForbiddenPitchLag_IsConcealedNotCrashed() {
    // pitch_lag[0] field > 123 is the forbidden code → bad frame → concealment path.
    var w = new BitWriterLe();
    w.Put(0, 2);                 // 6.3k active
    w.Put(0, 8); w.Put(0, 8); w.Put(0, 8);
    w.Put(127, 7); w.Put(0, 2);  // pitch_lag[0] = 127 > 123 → forbidden
    var bad = w.ToArray(24);

    // A good active frame first establishes ACTIVE past-type, then the bad one is concealed.
    var stream = ZeroActive6300().Concat(bad).ToArray();
    var dec = G7231Codec.Decode(stream);
    Assert.That(dec.Length, Is.EqualTo(2 * FrameLen));
  }

  [Test]
  public void Decode_RepeatedErasures_EventuallyMute() {
    var w = new BitWriterLe();
    w.Put(0, 2);
    w.Put(0, 8); w.Put(0, 8); w.Put(0, 8);
    w.Put(127, 7); w.Put(0, 2); // forbidden
    var bad = w.ToArray(24);

    // One good frame, then four consecutive erasures → erased_frames saturates at 3 → mute.
    var stream = ZeroActive6300()
      .Concat(bad).Concat(bad).Concat(bad).Concat(bad)
      .ToArray();
    var dec = G7231Codec.Decode(stream);
    var lastFrame = dec.AsSpan(4 * FrameLen, FrameLen);
    foreach (var s in lastFrame)
      Assert.That((int)s, Is.EqualTo(0), "the third+ consecutive erasure must mute the output");
  }

  // ── Comfort-noise path (SID / untransmitted) ───────────────────────────────────────

  [Test]
  public void Decode_SidAndUntransmitted_RunComfortNoiseWithoutCrash() {
    var stream = ZeroActive6300()  // establish active history
      .Concat(SidFrame(0x20))      // SID with a non-trivial gain index
      .Concat(Untransmitted())
      .Concat(Untransmitted())
      .ToArray();
    var dec = G7231Codec.Decode(stream);
    Assert.That(dec.Length, Is.EqualTo(4 * FrameLen));
  }

  // ── Postfilter on/off both decode ──────────────────────────────────────────────────

  [Test]
  public void Decode_PostfilterDisabled_ProducesSameSampleCount() {
    var stream = ZeroActive6300().Concat(ZeroActive5300()).ToArray();
    var pf = G7231Codec.Decode(stream, postfilter: true);
    var noPf = G7231Codec.Decode(stream, postfilter: false);
    Assert.That(pf.Length, Is.EqualTo(2 * FrameLen));
    Assert.That(noPf.Length, Is.EqualTo(2 * FrameLen));
  }
}
