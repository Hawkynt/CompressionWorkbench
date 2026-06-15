#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Bonk;
using Compression.Registry;
using FileFormat.Bonk;

namespace Compression.Tests.Bonk;

[TestFixture]
public class BonkTests {

  private static byte[] MakeMonoBonk(short[] samples, int nTaps = 4, int spp = 32) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return BonkCodec.Compress(pcm, channels: 1, sampleRate: 44100, nTaps: nTaps, samplesPerPacket: spp);
  }

  private static byte[] MakeStereoBonk(short[] left, short[] right, int spp = 32) {
    var pcm = new byte[left.Length * 2 * 2];
    for (var i = 0; i < left.Length; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), left[i]);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), right[i]);
    }
    return BonkCodec.Compress(pcm, channels: 2, sampleRate: 48000, nTaps: 4, samplesPerPacket: spp);
  }

  // ── Minimal lossless packet round-trips byte-exact ──────────────────────────

  [Test]
  public void Lossless_MinimalPacket_DecodesByteExact() {
    short[] samples = [0, 1, -1, 2, -2, 7, -8, 100, -100, 0, 0, 5, -5, 1234, -1234, 32, -33];
    var bonk = MakeMonoBonk(samples, nTaps: 4, spp: samples.Length);

    var pcm = BonkCodec.Decompress(bonk);
    Assert.That(pcm.Length, Is.EqualTo(samples.Length * 2));
    for (var i = 0; i < samples.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(samples[i]),
        $"sample {i}");
  }

  [Test]
  public void Lossless_MultiplePackets_DecodesByteExact() {
    var samples = new short[200];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = (short)((i * 53 % 4001) - 2000);

    var bonk = MakeMonoBonk(samples, nTaps: 8, spp: 32);
    var pcm = BonkCodec.Decompress(bonk);

    Assert.That(pcm.Length, Is.EqualTo(samples.Length * 2));
    for (var i = 0; i < samples.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(samples[i]),
        $"sample {i}");
  }

  [Test]
  public void Lossless_Stereo_DecodesByteExact() {
    var left = new short[64];
    var right = new short[64];
    for (var i = 0; i < 64; ++i) {
      left[i] = (short)(i * 100 - 3000);
      right[i] = (short)(2000 - i * 60);
    }
    var bonk = MakeStereoBonk(left, right, spp: 32);
    var pcm = BonkCodec.Decompress(bonk);

    Assert.That(pcm.Length, Is.EqualTo(64 * 2 * 2));
    for (var i = 0; i < 64; ++i) {
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4)), Is.EqualTo(left[i]), $"L {i}");
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4 + 2)), Is.EqualTo(right[i]), $"R {i}");
    }
  }

  [Test]
  public void ReadStreamInfo_ParsesHeader() {
    var bonk = MakeMonoBonk([1, 2, 3, 4], spp: 4);
    var info = BonkCodec.ReadStreamInfo(bonk, out var dataOffset);
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.Lossless, Is.True);
    Assert.That(info.NTaps, Is.EqualTo(4));
    Assert.That(dataOffset, Is.EqualTo(5 + BonkCodec.HeaderBytes));
  }

  // ── Descriptor ──────────────────────────────────────────────────────────────

  [Test]
  public void Descriptor_ListsFullAndChannels() {
    var left = new short[48];
    var right = new short[48];
    for (var i = 0; i < 48; ++i) { left[i] = (short)(i * 50); right[i] = (short)(-i * 40); }
    var bonk = MakeStereoBonk(left, right, spp: 16);

    using var ms = new MemoryStream(bonk);
    var entries = new BonkFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.bonk" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_FullBonkIsByteExact() {
    var bonk = MakeMonoBonk([10, 20, 30, 40], spp: 4);
    using var ms = new MemoryStream(bonk);
    using var output = new MemoryStream();
    new BonkFormatDescriptor().ExtractEntry(ms, "FULL.bonk", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(bonk));
  }

  [Test]
  public void Descriptor_GracefulFallback_OnGarbage() {
    using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    var entries = new BonkFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.bonk"));
  }
}
