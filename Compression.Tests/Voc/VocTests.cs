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

  // Hand-crafts a legacy mono Creative 4-bit ADPCM (block type 1, codec 1) VOC.
  // ADPCM body = 8-bit reference + nibble bytes.
  private static byte[] MakeMonoAdpcm4Voc(byte reference, byte[] nibbleBytes) {
    using var ms = new MemoryStream();
    ms.Write("Creative Voice File"u8);
    ms.WriteByte(0x1A);
    var hdr = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x001A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(2), 0x010A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(4),
      (ushort)((0x1234 + (~0x010A & 0xFFFF) + 1) & 0xFFFF));
    ms.Write(hdr);

    // Block type 1: divisor + codec(1) + [reference + nibble bytes].
    var divisor = (byte)(256 - 1000000 / 11025);
    var bodyLen = 2 + 1 + nibbleBytes.Length;
    ms.WriteByte(1);
    ms.WriteByte((byte)(bodyLen & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 8) & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 16) & 0xFF));
    ms.WriteByte(divisor);
    ms.WriteByte(1); // codec 1 = Creative 4-bit ADPCM
    ms.WriteByte(reference);
    ms.Write(nibbleBytes);
    ms.WriteByte(0); // terminator
    return ms.ToArray();
  }

  // Hand-crafts a legacy mono Creative ADPCM (block type 1) VOC with an arbitrary codec id.
  // ADPCM body = 8-bit reference + code bytes.
  private static byte[] MakeMonoAdpcmVoc(byte codec, byte reference, byte[] codeBytes) {
    using var ms = new MemoryStream();
    ms.Write("Creative Voice File"u8);
    ms.WriteByte(0x1A);
    var hdr = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, 0x001A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(2), 0x010A);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(4),
      (ushort)((0x1234 + (~0x010A & 0xFFFF) + 1) & 0xFFFF));
    ms.Write(hdr);

    var divisor = (byte)(256 - 1000000 / 11025);
    var bodyLen = 2 + 1 + codeBytes.Length;
    ms.WriteByte(1);
    ms.WriteByte((byte)(bodyLen & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 8) & 0xFF));
    ms.WriteByte((byte)((bodyLen >> 16) & 0xFF));
    ms.WriteByte(divisor);
    ms.WriteByte(codec);
    ms.WriteByte(reference);
    ms.Write(codeBytes);
    ms.WriteByte(0); // terminator
    return ms.ToArray();
  }

  [Test]
  public void VocReader_ParsesCreativeAdpcm2_6Bit_KnownSamples() {
    // Codec 2 (2.6-bit): three codes per byte of widths 3,3,2 (top-first: bits 7-5, 4-2, 1-0).
    // Reference 128 → predictor 0; bytes 0xB5 (101,101,01), 0x27 (001,001,11).
    var blob = MakeMonoAdpcmVoc(codec: 2, reference: 128, [0xB5, 0x27]);
    var parsed = new VocReader().Read(blob);

    Assert.That(parsed.Codec, Is.EqualTo(2));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm, Is.Not.Null);

    // Hand-walked through the width-scaled CT expander (3 codes per data byte + 1 reference).
    short[] expected = [0, 0, -383, 385, 764, 1141, 366];
    var pcm = parsed.InterleavedPcm!;
    Assert.That(pcm.Length, Is.EqualTo(expected.Length * 2));
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)),
        Is.EqualTo(expected[i]), $"sample {i}");
  }

  [Test]
  public void VocReader_ParsesCreativeAdpcm2Bit_KnownSamples() {
    // Codec 3 (2-bit): four 2-bit codes per byte (top-first).
    // Reference 128 → predictor 0; bytes 0x6C (01,10,11,00), 0x39 (00,11,10,01).
    var blob = MakeMonoAdpcmVoc(codec: 3, reference: 128, [0x6C, 0x39]);
    var parsed = new VocReader().Read(blob);

    Assert.That(parsed.Codec, Is.EqualTo(3));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.InterleavedPcm, Is.Not.Null);

    // Hand-walked through the width-scaled CT expander (4 codes per data byte + 1 reference).
    short[] expected = [0, 0, -255, -1020, -758, -498, -1261, -1507, -730];
    var pcm = parsed.InterleavedPcm!;
    Assert.That(pcm.Length, Is.EqualTo(expected.Length * 2));
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)),
        Is.EqualTo(expected[i]), $"sample {i}");
  }

  [Test]
  public void VocDescriptor_CreativeAdpcm2Bit_SurfacesMonoWav() {
    var blob = MakeMonoAdpcmVoc(codec: 3, reference: 128, [0x6C, 0x39]);
    using var ms = new MemoryStream(blob);
    var entries = new VocFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.voc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void VocReader_ParsesCreativeAdpcm4_KnownSamples() {
    // Reference 200 → predictor 18432; data bytes 0x57, 0x31 (high nibble first).
    var blob = MakeMonoAdpcm4Voc(reference: 200, [0x57, 0x31, 0x00]);
    var parsed = new VocReader().Read(blob);

    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.Codec, Is.EqualTo(1));
    Assert.That(parsed.InterleavedPcm, Is.Not.Null);

    var pcm = parsed.InterleavedPcm!;
    short[] expected = [18432, 18288, 19103, 20024, 20279, 20243, 20194];
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)),
        Is.EqualTo(expected[i]), $"sample {i}");
  }

  [Test]
  public void VocDescriptor_CreativeAdpcm4_SurfacesMonoWav() {
    var blob = MakeMonoAdpcm4Voc(reference: 128, [0x12, 0x34, 0x56]);
    using var ms = new MemoryStream(blob);
    var entries = new VocFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.voc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
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
