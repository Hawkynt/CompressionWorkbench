#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Rf64;

namespace Compression.Tests.Rf64;

[TestFixture]
public class Rf64Tests {

  // 44.1 kHz stereo 16-bit PCM, 10 samples, wrapped as a valid RF64 (with ds64).
  private static byte[] MakeStereoRf64() {
    var pcm = new byte[10 * 2 * 2];
    for (var i = 0; i < 10; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100));      // left
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(i * -100)); // right
    }
    return Rf64Writer.Build(pcm, channels: 2, sampleRate: 44100, bitsPerSample: 16, formatCode: 1, bext: null);
  }

  [Test]
  public void Rf64Reader_ParsesHeader() {
    var blob = MakeStereoRf64();
    var parsed = new Rf64Reader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(40));
  }

  [Test]
  public void Rf64Descriptor_ListsFullAndChannels() {
    var blob = MakeStereoRf64();
    using var ms = new MemoryStream(blob);
    var entries = new Rf64FormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.rf64"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Rf64Descriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoRf64();
    var tmp = Path.Combine(Path.GetTempPath(), "rf64_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new Rf64FormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
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
  public void Rf64Descriptor_ExtractEntry_StreamsSingleChannel() {
    var blob = MakeStereoRf64();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new Rf64FormatDescriptor().ExtractEntry(ms, "RIGHT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
  }

  [Test]
  public void Rf64Descriptor_CreateFromChannels_RoundTrips() {
    // Two mono channel WAVs at 48 kHz 16-bit, 8 frames each.
    var left = new byte[8 * 2];
    var right = new byte[8 * 2];
    for (var i = 0; i < 8; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 11));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(-i * 13));
    }
    var leftWav = PcmCodec.ToWavBlob(left, channels: 1, sampleRate: 48000, bitsPerSample: 16);
    var rightWav = PcmCodec.ToWavBlob(right, channels: 1, sampleRate: 48000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };

    using var output = new MemoryStream();
    new Rf64FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var rf64 = output.ToArray();

    // Header starts with "RF64".
    Assert.That(rf64.AsSpan(0, 4).ToArray(), Is.EqualTo("RF64"u8.ToArray()));
    // Contains a "ds64" chunk (first chunk after WAVE at offset 12).
    Assert.That(rf64.AsSpan(12, 4).ToArray(), Is.EqualTo("ds64"u8.ToArray()));

    var parsed = new Rf64Reader().Read(rf64);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(48000));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(8 * 2 * 2));

    // First interleaved frame should be left[0], right[0].
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(0)), Is.EqualTo((short)0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(2)), Is.EqualTo((short)0));
    // Second frame: left[1]=11, right[1]=-13.
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(4)), Is.EqualTo((short)11));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(6)), Is.EqualTo((short)-13));
  }

  [Test]
  public void Rf64Descriptor_CreatedHeaderHasRf64MagicAndDs64() {
    var blob = MakeStereoRf64();
    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("RF64"u8.ToArray()));
    // riffSize sentinel.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4)), Is.EqualTo(0xFFFFFFFFu));
    Assert.That(blob.AsSpan(8, 4).ToArray(), Is.EqualTo("WAVE"u8.ToArray()));
    Assert.That(blob.AsSpan(12, 4).ToArray(), Is.EqualTo("ds64"u8.ToArray()));
  }

  [Test]
  public void Rf64Descriptor_PassthroughFull() {
    var blob = MakeStereoRf64();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("FULL.rf64", blob),
    };
    using var output = new MemoryStream();
    new Rf64FormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }
}
