#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Audio;
using FileFormat.Wav;

namespace Compression.Tests.Audio;

[TestFixture]
public class WavTests {

  // 44.1 kHz stereo 16-bit PCM, 10 samples.
  private static byte[] MakeStereoWav() {
    var pcm = new byte[10 * 2 * 2];
    for (var i = 0; i < 10; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100));      // left
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(i * -100)); // right
    }
    return ChannelSplitter.ToWavBlob(pcm, channels: 2, sampleRate: 44100, bitsPerSample: 16);
  }

  [Test]
  public void WavReader_ParsesHeader() {
    var blob = MakeStereoWav();
    var parsed = new WavReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(40));
  }

  [Test]
  public void WavDescriptor_SplitsChannels() {
    var blob = MakeStereoWav();
    using var ms = new MemoryStream(blob);
    var entries = new WavFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void WavDescriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoWav();
    var tmp = Path.Combine(Path.GetTempPath(), "wav_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new WavFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
      var mono = File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav"));
      // fmt chunk's NumChannels field at offset 22 (uint16 LE)
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(mono.AsSpan(22)), Is.EqualTo(1));
      // Sample rate preserved at offset 24
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(24)), Is.EqualTo(44100u));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void ChannelSplitter_NamesMatchLayout() {
    Assert.That(ChannelSplitter.LayoutNames(1), Is.EqualTo(new[] { "MONO" }));
    Assert.That(ChannelSplitter.LayoutNames(2), Is.EqualTo(new[] { "LEFT", "RIGHT" }));
    Assert.That(ChannelSplitter.LayoutNames(6)[3], Is.EqualTo("LFE"));
  }

  [Test]
  public void Descriptor_ExtractEntry_WritesSingleChannel() {
    var blob = MakeStereoWav();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new WavFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
  }
}
