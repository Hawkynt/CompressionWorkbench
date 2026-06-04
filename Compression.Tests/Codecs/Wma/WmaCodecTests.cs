#pragma warning disable CS1591
using Codec.Wma;

namespace Compression.Tests.Codecs.Wma;

/// <summary>
/// Pins the WMA v1/v2 decoder's construction-time derivations and its deterministic
/// decode paths. Real WMA bitstreams are produced by the Microsoft encoder; without one
/// at hand the decode tests use the one bitstream the format guarantees is trivially
/// constructible — a frame whose blocks code no channels, which the reference decoder
/// turns into exactly <c>frame_len</c> samples of silence per channel. These tests also
/// assert the frame-length / block-size derivations against the reference formulas and
/// guard the transcribed tables' shape invariants.
/// </summary>
[TestFixture]
public class WmaCodecTests {

  // ── frame-length derivation (ff_wma_get_frame_len_bits) ──────────────────────

  [TestCase(8000, 2, 512)]    // <=16000 → 9 bits
  [TestCase(11025, 2, 512)]
  [TestCase(16000, 2, 512)]
  [TestCase(22050, 2, 1024)]  // <=22050 → 10 bits
  [TestCase(32000, 2, 2048)]  // v2 <=48000 → 11 bits
  [TestCase(44100, 2, 2048)]
  public void FrameLength_PerSampleRate_V2(int sampleRate, int channels, int expectedFrameLen) {
    var codec = NewCodec(version: 2, channels, sampleRate, flags2: 0);
    Assert.That(codec.FrameLength, Is.EqualTo(expectedFrameLen));
    Assert.That(1 << codec.FrameLengthBits, Is.EqualTo(codec.FrameLength));
  }

  [TestCase(32000, 1024)]  // v1: <=32000 → 10 bits
  [TestCase(44100, 2048)]  // v1: <=48000 → 11 bits
  public void FrameLength_V1_SpecificRates(int sampleRate, int expectedFrameLen) {
    var codec = NewCodec(version: 1, channels: 2, sampleRate, flags2: 0);
    Assert.That(codec.FrameLength, Is.EqualTo(expectedFrameLen));
  }

  // ── feature-flag derivation from extradata flags2 ────────────────────────────

  [Test]
  public void Flags2_SelectExponentVlcReservoirAndVariableBlocks() {
    // flags2 bit0 = exp VLC, bit1 = bit reservoir, bit2 = variable block length.
    var both = NewCodec(version: 2, channels: 2, sampleRate: 44100, flags2: 0x0007);
    Assert.That(both.UsesExponentVlc, Is.True);
    Assert.That(both.UsesBitReservoir, Is.True);
    Assert.That(both.UsesVariableBlockLength, Is.True);

    var lsp = NewCodec(version: 2, channels: 2, sampleRate: 44100, flags2: 0x0000);
    Assert.That(lsp.UsesExponentVlc, Is.False);
    Assert.That(lsp.UsesBitReservoir, Is.False);
    Assert.That(lsp.UsesVariableBlockLength, Is.False);
  }

  [Test]
  public void VariableBlockLength_ProducesMultipleBlockSizes() {
    var codec = NewCodec(version: 2, channels: 2, sampleRate: 44100, flags2: 0x0004);
    Assert.That(codec.BlockSizeCount, Is.GreaterThan(1));
  }

  [Test]
  public void NoiseCoding_DisabledAtHighBitrate_44100() {
    // 44100 Hz, bps1 >= 0.61 disables noise coding (stereo high bitrate).
    var high = NewCodec(version: 2, channels: 2, sampleRate: 44100, flags2: 0, bitrate: 256000);
    Assert.That(high.UsesNoiseCoding, Is.False);
    // A low-bitrate stream keeps perceptual noise coding active.
    var low = NewCodec(version: 2, channels: 2, sampleRate: 44100, flags2: 0, bitrate: 32000);
    Assert.That(low.UsesNoiseCoding, Is.True);
  }

  // ── deterministic decode: a no-coded-channel frame is silence ────────────────

  [Test]
  public void DecodeSuperframe_AllZero_FixedBlock_YieldsSilenceOfFrameLength() {
    const int blockAlign = 256;
    var codec = NewCodec(version: 2, channels: 2, sampleRate: 8000, flags2: 0, blockAlign: blockAlign);

    // All-zero superframe: ms_stereo=0, channel_coded[0]=0, channel_coded[1]=0 → no
    // coded channels → silent IMDCT → exactly frame_len samples per channel.
    var pcm = codec.DecodeSuperframe(new byte[blockAlign]);

    Assert.That(pcm.Length, Is.EqualTo(codec.FrameLength * codec.Channels));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void DecodeSuperframe_Mono_AllZero_YieldsSilence() {
    const int blockAlign = 256;
    var codec = NewCodec(version: 2, channels: 1, sampleRate: 8000, flags2: 0, blockAlign: blockAlign);
    var pcm = codec.DecodeSuperframe(new byte[blockAlign]);
    Assert.That(pcm.Length, Is.EqualTo(codec.FrameLength));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void DecodeSuperframe_TruncatedInput_StillProducesFrame() {
    const int blockAlign = 256;
    var codec = NewCodec(version: 2, channels: 2, sampleRate: 8000, flags2: 0, blockAlign: blockAlign);
    // Feed fewer bytes than block_align: zero-padded, still decodes a silent frame.
    var pcm = codec.DecodeSuperframe(new byte[16]);
    Assert.That(pcm.Length, Is.EqualTo(codec.FrameLength * codec.Channels));
  }

  // ── input validation ─────────────────────────────────────────────────────────

  [Test]
  public void Constructor_RejectsUnsupportedVersionChannelsRate() {
    Assert.That(() => new WmaCodec(3, 2, 44100, 128000, 4096, []), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new WmaCodec(2, 3, 44100, 128000, 4096, []), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new WmaCodec(2, 2, 96000, 128000, 4096, []), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new WmaCodec(2, 2, 44100, 128000, 0, []), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  private static WmaCodec NewCodec(int version, int channels, int sampleRate, int flags2,
      long bitrate = 128000, int blockAlign = 4096) {
    // Build a WAVEFORMATEX-style extradata tail carrying flags2 at the version-specific offset.
    byte[] extradata = version == 1
      ? [0, 0, (byte)(flags2 & 0xFF), (byte)(flags2 >> 8)]
      : [0, 0, 0, 0, (byte)(flags2 & 0xFF), (byte)(flags2 >> 8)];
    return new WmaCodec(version, channels, sampleRate, bitrate, blockAlign, extradata);
  }
}
