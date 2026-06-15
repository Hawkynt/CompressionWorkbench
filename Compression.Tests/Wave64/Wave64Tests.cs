#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wave64;

namespace Compression.Tests.Wave64;

[TestFixture]
public class Wave64Tests {

  // 44.1 kHz stereo 16-bit PCM, 10 samples, assembled into a Wave64 file.
  private static byte[] MakeStereoW64() {
    var pcm = new byte[10 * 2 * 2];
    for (var i = 0; i < 10; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100));      // left
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(i * -100)); // right
    }

    var left = new byte[10 * 2];
    var right = new byte[10 * 2];
    for (var i = 0; i < 10; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 100));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(i * -100));
    }

    var leftWav = PcmCodec.ToWavBlob(left, channels: 1, sampleRate: 44100, bitsPerSample: 16);
    var rightWav = PcmCodec.ToWavBlob(right, channels: 1, sampleRate: 44100, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };
    using var output = new MemoryStream();
    new Wave64FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  [Test]
  public void Wave64Reader_ParsesHeader() {
    var blob = MakeStereoW64();
    var parsed = new Wave64Reader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(40));
  }

  [Test]
  public void Wave64Descriptor_SplitsChannels() {
    var blob = MakeStereoW64();
    using var ms = new MemoryStream(blob);
    var entries = new Wave64FormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.w64"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "FULL.w64").Kind, Is.EqualTo("Container"));
  }

  [Test]
  public void Wave64Descriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoW64();
    var tmp = Path.Combine(Path.GetTempPath(), "w64_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new Wave64FormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
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
  public void Descriptor_ExtractEntry_WritesSingleChannel() {
    var blob = MakeStereoW64();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new Wave64FormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
  }

  [Test]
  public void Create_FromChannelWavs_RoundTrips() {
    var blob = MakeStereoW64();
    var parsed = new Wave64Reader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));

    // Re-derive the interleaved PCM we expect (left = i*100, right = i*-100).
    for (var i = 0; i < 10; ++i) {
      var l = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4));
      var r = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 4 + 2));
      Assert.That(l, Is.EqualTo((short)(i * 100)));
      Assert.That(r, Is.EqualTo((short)(i * -100)));
    }
  }

  [Test]
  public void Create_PassesThroughFullW64() {
    var blob = MakeStereoW64();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("FULL.w64", blob),
    };
    using var output = new MemoryStream();
    new Wave64FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }
}
