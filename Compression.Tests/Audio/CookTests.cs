#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Cook;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Cook / RealAudio G2 decoder (<see cref="CookCodec"/>). Bit-exact cross-checks
/// against FFmpeg are unavailable here, so these tests pin determinism + structure:
/// extradata/header parse exactness, exact per-frame sample counts, the two-frame discard
/// (silence) prelude, bounded amplitude with deterministic repeatability, truncation
/// tolerance, and rejection of unsupported cook variants.
/// </summary>
[TestFixture]
public class CookTests {

  private const int Mono = 0x1000001;
  private const int JointStereo = 0x1000003;

  // ── extradata / header parse ──────────────────────────────────────────────

  [Test]
  public void MonoExtradata_ParsesSamplesPerChannelAndChannels() {
    var codec = new CookCodec(MonoInfo(samplesPerFrame: 1024, subbands: 20, blockAlign: 96));
    Assert.That(codec.SamplesPerChannel, Is.EqualTo(1024));
    Assert.That(codec.Channels, Is.EqualTo(1));
  }

  [Test]
  public void MonoExtradata_SamplesPerChannelTracksSamplesPerFrame() {
    var codec = new CookCodec(MonoInfo(samplesPerFrame: 512, subbands: 20, blockAlign: 60));
    Assert.That(codec.SamplesPerChannel, Is.EqualTo(512));
  }

  [Test]
  public void JointStereoExtradata_ParsesTwoChannels() {
    var codec = new CookCodec(JointStereoInfo(samplesPerFrame: 2048, subbands: 20,
      jsSubbandStart: 5, jsVlcBits: 5, blockAlign: 120));
    // Joint stereo: samples_per_frame / channels.
    Assert.That(codec.SamplesPerChannel, Is.EqualTo(1024));
    Assert.That(codec.Channels, Is.EqualTo(2));
  }

  [Test]
  public void ShortExtradata_Throws() {
    var info = MonoInfo(1024, 20, 96);
    info.Extradata = new byte[4];
    Assert.That(() => new CookCodec(info), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void UnsupportedSamplesPerChannel_Throws() {
    // 700 samples is not 256/512/1024 → unsupported.
    Assert.That(() => new CookCodec(MonoInfo(samplesPerFrame: 700, subbands: 20, blockAlign: 96)),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void UnknownCookVersion_Throws() {
    var info = MonoInfo(1024, 20, 96);
    BinaryPrimitives.WriteUInt32BigEndian(info.Extradata.AsSpan(0), 0xDEADBEEF);
    Assert.That(() => new CookCodec(info), Throws.TypeOf<NotSupportedException>());
  }

  // ── per-frame sample counts ───────────────────────────────────────────────

  [Test]
  public void Decode_ProducesExactSamplesPerFrame_Mono() {
    var codec = new CookCodec(MonoInfo(1024, 20, 96));
    var pcm = codec.Decode(new byte[96]);
    Assert.That(pcm.Length, Is.EqualTo(1024)); // 1024 samples * 1 channel
  }

  [Test]
  public void Decode_JointStereo_DecodesExactLengthOrCleanlyRejects() {
    // Crafted (non-encoder) joint-stereo bits may legitimately hit the reference's
    // invalid-decouple guard; faithful behavior is then a clean InvalidDataException
    // (mirroring AVERROR_INVALIDDATA), never a crash or wrong-length output.
    var codec = new CookCodec(JointStereoInfo(2048, 20, 5, 5, 120));
    try {
      var pcm = codec.Decode(new byte[120]);
      Assert.That(pcm.Length, Is.EqualTo(1024 * 2)); // 1024 per channel * 2 channels, interleaved
    } catch (InvalidDataException) {
      Assert.Pass("Invalid joint-stereo bitstream cleanly rejected.");
    }
  }

  [Test]
  public void DecodeStream_LengthIsFramesTimesSamplesPerFrame() {
    var codec = new CookCodec(MonoInfo(1024, 20, 96));
    var concat = new byte[96 * 4];
    var pcm = codec.DecodeStream(concat);
    Assert.That(pcm.Length, Is.EqualTo(4 * 1024));
  }

  // ── discard prelude + bounded deterministic decode ────────────────────────

  [Test]
  public void FirstTwoFrames_AreSilence() {
    var codec = new CookCodec(MonoInfo(1024, 20, 96));
    var f = Pattern(96);
    Assert.That(codec.Decode(f).All(s => s == 0), Is.True);
    Assert.That(codec.Decode(f).All(s => s == 0), Is.True);
  }

  [Test]
  public void ThirdFrame_IsBoundedAndDeterministic() {
    var f = Pattern(96);

    var a = new CookCodec(MonoInfo(1024, 20, 96));
    a.Decode(f); a.Decode(f);
    var pcmA = a.Decode(f);

    var b = new CookCodec(MonoInfo(1024, 20, 96));
    b.Decode(f); b.Decode(f);
    var pcmB = b.Decode(f);

    Assert.That(pcmA.Length, Is.EqualTo(1024));
    // 16-bit PCM is inherently bounded; assert it and exact repeatability.
    Assert.That(pcmA.Max(s => Math.Abs((int)s)), Is.LessThanOrEqualTo(32767));
    Assert.That(pcmA, Is.EqualTo(pcmB));
  }

  // ── truncation tolerance ──────────────────────────────────────────────────

  [Test]
  public void Decode_TruncatedFrame_DoesNotThrow_AndPadsToFrameLength() {
    var codec = new CookCodec(MonoInfo(1024, 20, 96));
    short[] pcm = null!;
    Assert.That(() => pcm = codec.Decode(new byte[10]), Throws.Nothing);
    Assert.That(pcm.Length, Is.EqualTo(1024));
  }

  [Test]
  public void DecodeStream_RaggedTail_IsPaddedAndDecoded() {
    var codec = new CookCodec(MonoInfo(1024, 20, 96));
    // 96 + 40 bytes = one full frame + a 40-byte tail that is padded to a frame.
    var pcm = codec.DecodeStream(new byte[96 + 40]);
    Assert.That(pcm.Length, Is.EqualTo(2 * 1024));
  }

  // ── deinterleaver ─────────────────────────────────────────────────────────

  [Test]
  public void Deinterleave_Int0_ConcatenatesPacketsUnchanged() {
    var packets = new List<byte[]> { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
    var result = CookDeinterleaver.Reorder(packets, CookDeinterleaver.Int0,
      subPacketH: 1, audioFrameSize: 3, subPacketSize: 3, codedFrameSize: 3);
    Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
  }

  [Test]
  public void Deinterleave_Int4_ReordersByCodedFrame() {
    // h=2, cfs=2, w=2  (cfs*h == 2*w). Two packets of w=2 bytes each.
    // y=0: x in {0}: dst[0*2*2 + 0*2] = dst[0] <- src0[0..1]
    // y=1: x in {0}: dst[0*2*2 + 1*2] = dst[2] <- src1[0..1]
    var packets = new List<byte[]> { new byte[] { 0xA0, 0xA1 }, new byte[] { 0xB0, 0xB1 } };
    var result = CookDeinterleaver.Reorder(packets, CookDeinterleaver.Int4,
      subPacketH: 2, audioFrameSize: 2, subPacketSize: 2, codedFrameSize: 2);
    Assert.That(result, Is.EqualTo(new byte[] { 0xA0, 0xA1, 0xB0, 0xB1 }));
  }

  [Test]
  public void Deinterleave_Genr_ScattersSubPackets() {
    // h=2, w=4, sps=2. w/sps = 2 sub-packets per container packet.
    // y=0 (even): dst index = sps*(h*x + 0 + 0) = 2*(2*x). x0->0, x1->4
    // y=1 (odd):  dst index = sps*(h*x + ((h+1)/2)*1 + 0) = 2*(2*x + 1). x0->2, x1->6
    var packets = new List<byte[]> {
      new byte[] { 0x10, 0x11, 0x12, 0x13 }, new byte[] { 0x20, 0x21, 0x22, 0x23 },
    };
    var result = CookDeinterleaver.Reorder(packets, CookDeinterleaver.Genr,
      subPacketH: 2, audioFrameSize: 4, subPacketSize: 2, codedFrameSize: 0);
    // buffer[0,1]=10,11 ; buffer[2,3]=20,21 ; buffer[4,5]=12,13 ; buffer[6,7]=22,23
    Assert.That(result, Is.EqualTo(new byte[] { 0x10, 0x11, 0x20, 0x21, 0x12, 0x13, 0x22, 0x23 }));
  }

  [Test]
  public void Deinterleave_InconsistentInt4Framing_ReturnsEmpty() {
    var packets = new List<byte[]> { new byte[] { 1, 2 }, new byte[] { 3, 4 } };
    // cfs*h (3*2=6) != 2*w (2*2=4) → rejected.
    var result = CookDeinterleaver.Reorder(packets, CookDeinterleaver.Int4,
      subPacketH: 2, audioFrameSize: 2, subPacketSize: 2, codedFrameSize: 3);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Deinterleave_PartialTrailingGroup_IsDropped() {
    // h=2 but only 3 packets → one full group (2) processed, the 3rd dropped.
    var packets = new List<byte[]> {
      new byte[] { 0xA0, 0xA1 }, new byte[] { 0xB0, 0xB1 }, new byte[] { 0xC0, 0xC1 },
    };
    var result = CookDeinterleaver.Reorder(packets, CookDeinterleaver.Int4,
      subPacketH: 2, audioFrameSize: 2, subPacketSize: 2, codedFrameSize: 2);
    Assert.That(result.Length, Is.EqualTo(2 * 2)); // one group of w*h = 4 bytes
  }

  // ── synthetic builders ────────────────────────────────────────────────────

  private static CookCodec.StreamInfo MonoInfo(int samplesPerFrame, int subbands, int blockAlign) {
    var ed = new byte[16];
    BinaryPrimitives.WriteUInt32BigEndian(ed.AsSpan(0), Mono);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(4), (ushort)samplesPerFrame);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(6), (ushort)subbands);
    // ed[8..11] unused, ed[12..13] js_subband_start = 0, ed[14..15] js_vlc_bits = 0
    return new CookCodec.StreamInfo { Channels = 1, SampleRate = 44100, BlockAlign = blockAlign, Extradata = ed };
  }

  private static CookCodec.StreamInfo JointStereoInfo(int samplesPerFrame, int subbands,
      int jsSubbandStart, int jsVlcBits, int blockAlign) {
    var ed = new byte[16];
    BinaryPrimitives.WriteUInt32BigEndian(ed.AsSpan(0), JointStereo);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(4), (ushort)samplesPerFrame);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(6), (ushort)subbands);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(12), (ushort)jsSubbandStart);
    BinaryPrimitives.WriteUInt16BigEndian(ed.AsSpan(14), (ushort)jsVlcBits);
    return new CookCodec.StreamInfo { Channels = 2, SampleRate = 44100, BlockAlign = blockAlign, Extradata = ed };
  }

  private static byte[] Pattern(int n) {
    var b = new byte[n];
    for (var i = 0; i < n; ++i) b[i] = (byte)(i * 37 + 11);
    return b;
  }
}
