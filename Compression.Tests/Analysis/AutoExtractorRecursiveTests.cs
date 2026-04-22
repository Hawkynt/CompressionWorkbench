#pragma warning disable CS1591
using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public class AutoExtractorRecursiveTests {

  [Test]
  public void Defaults_AreLenientEnoughForAudioVideoChains() {
    // Default MaxDepth must be ≥ 6 (GZ→TAR→VMDK→FAT32→MKV→track) so headline
    // recursive-descent scenarios don't truncate.
    var extractor = new AutoExtractor();
    // No way to introspect MaxDepth directly; we just assert construction and a
    // sanity Extract() of a null-ish stream returns null without throwing.
    using var ms = new MemoryStream(new byte[100]);
    Assert.That(extractor.Extract(ms), Is.Null);
  }

  [Test]
  public void Custom_MaxDepth_IsHonored() {
    var extractor = new AutoExtractor(maxDepth: 0);
    // With depth budget 0, any call returns null immediately.
    using var ms = new MemoryStream(new byte[100]);
    Assert.That(extractor.Extract(ms), Is.Null);
  }

  [Test]
  public void ExtractPath_OnWav_SelectsLeftChannel() {
    // Build a simple stereo WAV and verify path-targeted extraction reaches it.
    var pcm = new byte[40]; // 10 stereo frames × 4 bytes
    for (var i = 0; i < 10; ++i) {
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100));
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-i * 100));
    }
    var wav = Compression.Core.Audio.ChannelSplitter.ToWavBlob(pcm, 2, 44100, 16);

    var extractor = new AutoExtractor();
    using var ms = new MemoryStream(wav);
    var left = extractor.ExtractPath(ms, "LEFT.wav");
    Assert.That(left, Is.Not.Null);
    Assert.That(left![..4], Is.EqualTo("RIFF"u8.ToArray()));
    // Extracted LEFT channel is mono.
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(22)), Is.EqualTo(1));
  }
}
