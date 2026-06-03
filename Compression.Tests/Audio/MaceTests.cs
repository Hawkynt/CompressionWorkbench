#pragma warning disable CS1591
using Codec.Mace;

namespace Compression.Tests.Audio;

[TestFixture]
public class MaceTests {

  // ── lengths ────────────────────────────────────────────────────────────────

  [Test]
  public void Mace3_Mono_ExpandsThreeSamplesPerByte() {
    var pcm = MaceCodec.DecodeMace3(new byte[] { 0x00, 0x00 }, 1);
    Assert.That(pcm.Length, Is.EqualTo(6)); // 2 bytes × 3
  }

  [Test]
  public void Mace6_Mono_ExpandsSixSamplesPerByte() {
    var pcm = MaceCodec.DecodeMace6(new byte[] { 0x00 }, 1);
    Assert.That(pcm.Length, Is.EqualTo(6)); // 1 byte × 6
  }

  [Test]
  public void Mace3_Mono_FourBytes_TwelveSamples() {
    var pcm = MaceCodec.DecodeMace3(new byte[] { 1, 2, 3, 4 }, 1);
    Assert.That(pcm.Length, Is.EqualTo(12));
  }

  [Test]
  public void Mace6_Mono_ThreeBytes_EighteenSamples() {
    var pcm = MaceCodec.DecodeMace6(new byte[] { 1, 2, 3 }, 1);
    Assert.That(pcm.Length, Is.EqualTo(18));
  }

  [Test]
  public void Mace3_Stereo_InterleavesBothChannels() {
    // 4 bytes → 2 frames × 2 bytes/ch → 2 ch × 6 samples = 12 interleaved.
    var pcm = MaceCodec.DecodeMace3(new byte[] { 0, 0, 0, 0 }, 2);
    Assert.That(pcm.Length, Is.EqualTo(12));
  }

  [Test]
  public void Mace6_Stereo_InterleavesBothChannels() {
    // 2 bytes → 1 frame × 1 byte/ch → 2 ch × 6 samples = 12 interleaved.
    var pcm = MaceCodec.DecodeMace6(new byte[] { 0, 0 }, 2);
    Assert.That(pcm.Length, Is.EqualTo(12));
  }

  [Test]
  public void Mace_RaggedTail_IsTrimmedToFrameBoundary() {
    // MACE3 mono frame = 2 bytes; one ragged byte yields nothing.
    Assert.That(MaceCodec.DecodeMace3(new byte[] { 0x12 }, 1).Length, Is.EqualTo(0));
  }

  // ── exact hand-walked decode (state starts zeroed) ───────────────────────────

  [Test]
  public void Mace3_ZeroInput_DecodesToSilence() {
    // Every step magnitude for index 0 (37/116/206/330) is < 256, so the high byte
    // is zero and QT_8S_2_16S maps each sample to 0.
    var pcm = MaceCodec.DecodeMace3(new byte[] { 0x00, 0x00 }, 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 0, 0, 0, 0, 0, 0 }));
  }

  [Test]
  public void Mace3_FfThen80_DecodesExactSamples() {
    // 0xFF → val {7,3,7}; 0x80 → val {0,0,4}. Walked through chomp3 with zeroed state.
    var pcm = MaceCodec.DecodeMace3(new byte[] { 0xFF, 0x80 }, 1);
    Assert.That(pcm, Is.EqualTo(new short[] { -1, -1, -1, -1, 0, -258 }));
  }

  [Test]
  public void Mace6_ZeroInput_DecodesToSilence() {
    var pcm = MaceCodec.DecodeMace6(new byte[] { 0x00 }, 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 0, 0, 0, 0, 0, 0 }));
  }

  [Test]
  public void Mace6_AllOnes_DecodesExactSamples() {
    // 0xFF → val {7,3,7}; chomp6 yields two samples per index. Hand-walked, zeroed state.
    var pcm = MaceCodec.DecodeMace6(new byte[] { 0xFF }, 1);
    Assert.That(pcm, Is.EqualTo(new short[] { -1, -1, -1, -1, -1, -1 }));
  }

  [Test]
  public void Mace_RejectsUnsupportedChannelCount() {
    Assert.Throws<ArgumentOutOfRangeException>(() => MaceCodec.DecodeMace3(new byte[] { 0, 0 }, 3));
    Assert.Throws<ArgumentOutOfRangeException>(() => MaceCodec.DecodeMace6(new byte[] { 0 }, 0));
  }
}
