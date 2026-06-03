#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Voc;

namespace Compression.Tests.Voc;

[TestFixture]
public class VocTests {

  // Builds two mono 16-bit WAV inputs (LEFT/RIGHT) and assembles them into a VOC via Create.
  private static byte[] MakeStereoVoc() {
    var left = new byte[10 * 2];
    var right = new byte[10 * 2];
    for (var i = 0; i < 10; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 100));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(i * -100));
    }
    var leftWav = PcmCodec.ToWavBlob(left, channels: 1, sampleRate: 44100, bitsPerSample: 16);
    var rightWav = PcmCodec.ToWavBlob(right, channels: 1, sampleRate: 44100, bitsPerSample: 16);

    var inputs = new[] {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };
    using var ms = new MemoryStream();
    new VocFormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  // Hand-crafts a legacy mono 8-bit (block type 1) VOC.
  private static byte[] MakeMono8Voc(out byte[] samples) {
    samples = new byte[8];
    for (var i = 0; i < samples.Length; ++i) samples[i] = (byte)(128 + i * 10);

    using var ms = new MemoryStream();
    // Header
    ms.Write("Creative Voice File"u8);
    ms.WriteByte(0x1A);
    var hdr = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x001A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(2), 0x010A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(4),
      (ushort)((0x1234 + (~0x010A & 0xFFFF) + 1) & 0xFFFF));
    ms.Write(hdr);
    // Block type 1: divisor + codec + samples.  divisor for 11025 Hz ≈ 256 - 1000000/11025 = 165.
    var divisor = (byte)(256 - 1000000 / 11025);
    var bodyLen = 2 + samples.Length;
    ms.WriteByte(1);
    ms.WriteByte((byte)(bodyLen & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 8) & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 16) & 0xFF));
    ms.WriteByte(divisor);
    ms.WriteByte(0); // codec 0 = 8-bit unsigned PCM
    ms.Write(samples);
    ms.WriteByte(0); // terminator
    return ms.ToArray();
  }

  [Test]
  public void VocReader_ParsesBlock9Stereo() {
    var blob = MakeStereoVoc();
    var parsed = new VocReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm, Is.Not.Null);
    Assert.That(parsed.InterleavedPcm!.Length, Is.EqualTo(10 * 2 * 2));
  }

  [Test]
  public void VocDescriptor_ListsFullAndChannels() {
    var blob = MakeStereoVoc();
    using var ms = new MemoryStream(blob);
    var entries = new VocFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.voc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "FULL.voc").Kind, Is.EqualTo("Container"));
  }

  [Test]
  public void VocDescriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoVoc();
    var tmp = Path.Combine(Path.GetTempPath(), "voc_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new VocFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
      var mono = File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(mono.AsSpan(22)), Is.EqualTo(1));      // NumChannels
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(24)), Is.EqualTo(44100u)); // sample rate
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void VocDescriptor_ExtractEntry_StreamsSingleChannel() {
    var blob = MakeStereoVoc();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new VocFormatDescriptor().ExtractEntry(ms, "RIGHT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
  }

  [Test]
  public void VocDescriptor_Create_RoundTripsChannelsAndRate() {
    var blob = MakeStereoVoc();
    var parsed = new VocReader().Read(blob);

    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));

    // Verify interleaved samples survive the round-trip.
    var pcm = parsed.InterleavedPcm!;
    for (var i = 0; i < 10; ++i) {
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4)), Is.EqualTo((short)(i * 100)));
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4 + 2)), Is.EqualTo((short)(i * -100)));
    }
  }

  [Test]
  public void VocReader_ParsesLegacyMono8Bit() {
    var blob = MakeMono8Voc(out var samples);
    var parsed = new VocReader().Read(blob);

    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(8));
    Assert.That(parsed.Codec, Is.EqualTo(0));
    Assert.That(parsed.InterleavedPcm, Is.Not.Null);
    Assert.That(parsed.InterleavedPcm, Is.EqualTo(samples));
  }

  [Test]
  public void VocDescriptor_Mono8Bit_SurfacesMonoWav() {
    var blob = MakeMono8Voc(out _);
    using var ms = new MemoryStream(blob);
    var entries = new VocFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.voc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
  }
}
