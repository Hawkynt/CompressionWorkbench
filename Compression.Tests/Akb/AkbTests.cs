using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Akb;

namespace Compression.Tests.Akb;

[TestFixture]
public sealed class AkbTests {

  [Test]
  public void Reader_ParsesRealClassic204ByteLayout() {
    var payload = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 3, 4 };
    var file = new byte[0xCC + payload.Length];
    "AKB "u8.CopyTo(file);
    file[0x04] = 2;
    file[0x05] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x06, 2), 0xCC);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08, 4), (uint)file.Length);
    file[0x0C] = 0x05;
    file[0x0D] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0E, 2), 44_100);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x10, 4), 1_000);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x14, 4), 100);
    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x18, 4), 900);
    payload.CopyTo(file, 0xCC);

    using var reader = new AkbReader(new MemoryStream(file, writable: false));
    var entry = reader.Entries.Single();

    Assert.Multiple(() => {
      Assert.That(reader.ContainerKind, Is.EqualTo(AkbContainerKind.Classic));
      Assert.That(reader.VersionByte, Is.EqualTo(2));
      Assert.That(entry.Codec, Is.EqualTo(AkbCodec.OggVorbis));
      Assert.That(entry.Offset, Is.EqualTo(0xCC));
      Assert.That(entry.SampleRate, Is.EqualTo(44_100));
      Assert.That(entry.Channels, Is.EqualTo(2));
      Assert.That(entry.SampleCount, Is.EqualTo(1_000));
      Assert.That(entry.LoopStart, Is.EqualTo(100));
      Assert.That(entry.LoopEnd, Is.EqualTo(900));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    });
  }

  [Test]
  public void Akb2Writer_SingleOggUsesMemoria304BytePayloadOffset() {
    var payload = "OggS-memoria-layout"u8.ToArray();
    using var output = new MemoryStream();
    using (var writer = new AkbWriter(output, leaveOpen: true)) {
      writer.ContainerKind = AkbContainerKind.Akb2;
      writer.AddEncodedEntry(new AkbWriteEntry("music.ogg", payload, AkbCodec.OggVorbis, 44_100, 2, 12_345, 100, 10_000));
      writer.Write();
    }
    var file = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(file.AsSpan(0, 4).SequenceEqual("AKB2"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x06, 2)), Is.EqualTo(0x10));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x14, 4)), Is.EqualTo(0x20));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x22, 2)), Is.EqualTo(0x30));
      Assert.That(file[0x2F], Is.EqualTo(1));
      Assert.That(file[0xE1], Is.EqualTo(0x05));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0xE4, 2)), Is.EqualTo(0x40));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0xF8, 4)), Is.EqualTo(0x10));
      Assert.That(file.AsSpan(0x130, payload.Length).ToArray(), Is.EqualTo(payload));
    });

    using var reader = new AkbReader(new MemoryStream(file, writable: false));
    Assert.That(reader.Entries.Single().Offset, Is.EqualTo(0x130));
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(6)]
  public void Akb2Pcm_EncodeDecodeIsBitExact(int channels) {
    const int frames = 64;
    var pcmBytes = MakePcm(frames, channels);
    var descriptor = new AkbFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(32_000, channels, 16), pcmBytes);

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "pcm16le", Options(("Variant", "Akb2")));
    encoded.Position = 0;
    var decoded = descriptor.DecodePcm(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(32_000));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.FrameCount, Is.EqualTo(frames));
      Assert.That(decoded.InterleavedData, Is.EqualTo(pcmBytes));
    });
  }

  [TestCase("Akb2", "Auto", 128)]
  [TestCase("Classic", "2", 256)]
  [TestCase("Classic", "3", 512)]
  public void MsAdpcm_EncodesAndDecodesSupportedProfiles(string variant, string version, int blockAlign) {
    const int frames = 333;
    const int channels = 2;
    var descriptor = new AkbFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(44_100, channels, 16), MakePcm(frames, channels));
    var options = Options(("Variant", variant), ("ClassicVersion", version), ("BlockAlign", blockAlign.ToString()));

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "ms-adpcm", options);
    encoded.Position = 0;
    using (var reader = new AkbReader(encoded, leaveOpen: true)) {
      var entry = reader.Entries.Single();
      Assert.That(entry.BlockAlign, Is.EqualTo(blockAlign));
      Assert.That(entry.Codec, Is.EqualTo(AkbCodec.MsAdpcm));
    }
    encoded.Position = 0;
    var decoded = descriptor.DecodePcm(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.FrameCount, Is.EqualTo(frames));
      Assert.That(decoded.InterleavedData, Is.Not.EqualTo(new byte[decoded.InterleavedData.Length]));
    });
  }

  [TestCase("Akb2")]
  [TestCase("Classic")]
  public void Vorbis_DemuxMuxPreservesCompleteInnerOgg(string variant) {
    var descriptor = new AkbFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(44_100, 2, 16), MakePcm(256, 2));
    var options = Options(("Variant", variant), ("ClassicVersion", "2"));
    using var first = new MemoryStream();
    descriptor.EncodePcm(first, pcm, "vorbis", options);

    first.Position = 0;
    Assert.That(descriptor.TryDemux(first, out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.That(demuxed!.Format.CodecId, Is.EqualTo("ogg-vorbis"));

    first.Position = 0;
    using var firstReader = new AkbReader(first, leaveOpen: true);
    var originalOgg = firstReader.Extract(firstReader.Entries.Single());
    Assert.That(demuxed.Packets.Single().Data, Is.EqualTo(originalOgg));

    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, demuxed, new FormatCreateOptions());
    remuxed.Position = 0;
    using var secondReader = new AkbReader(remuxed, leaveOpen: true);
    Assert.That(secondReader.Extract(secondReader.Entries.Single()), Is.EqualTo(originalOgg));
  }

  [Test]
  public void ClassicV3EncryptedOgg_DecryptsAndRemuxPreservesEncryption() {
    var descriptor = new AkbFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(44_100, 2, 16), MakePcm(256, 2));
    var options = Options(("Variant", "Classic"), ("ClassicVersion", "3"), ("Encrypt", "true"));
    using var first = new MemoryStream();
    descriptor.EncodePcm(first, pcm, "vorbis", options);

    first.Position = 0;
    using (var reader = new AkbReader(first, leaveOpen: true)) {
      var entry = reader.Entries.Single();
      Assert.That(entry.Encrypted, Is.True);
      Assert.That(reader.ExtractRaw(entry).AsSpan(0, 4).SequenceEqual("OggS"u8), Is.False);
      Assert.That(reader.Extract(entry).AsSpan(0, 4).SequenceEqual("OggS"u8), Is.True);
    }

    first.Position = 0;
    Assert.That(descriptor.TryDemux(first, out var demuxed), Is.True);
    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, demuxed!, new FormatCreateOptions());
    remuxed.Position = 0;
    using var remuxedReader = new AkbReader(remuxed, leaveOpen: true);
    var remuxedEntry = remuxedReader.Entries.Single();
    Assert.That(remuxedEntry.Encrypted, Is.True);
    Assert.That(remuxedReader.Extract(remuxedEntry), Is.EqualTo(demuxed!.Packets.Single().Data));
  }

  [Test]
  public void AacEncoding_AutoSelectsClassicV0M4a() {
    var descriptor = new AkbFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(44_100, 2, 16), MakePcm(2_048, 2));
    using var encoded = new MemoryStream();

    descriptor.EncodePcm(encoded, pcm, "aac", new FormatCreateOptions());
    encoded.Position = 0;
    using var reader = new AkbReader(encoded, leaveOpen: true);
    var entry = reader.Entries.Single();
    var m4a = reader.Extract(entry);

    Assert.Multiple(() => {
      Assert.That(reader.ContainerKind, Is.EqualTo(AkbContainerKind.Classic));
      Assert.That(reader.VersionByte, Is.EqualTo(0));
      Assert.That(entry.Codec, Is.EqualTo(AkbCodec.M4aAac));
      Assert.That(entry.Offset, Is.EqualTo(0xCC));
      Assert.That(m4a.AsSpan(4, 4).SequenceEqual("ftyp"u8), Is.True);
    });
  }

  [Test]
  public void Akb2Writer_RoundTripsMultipleMaterials() {
    var left = MakePcm(16, 1);
    var right = MakePcm(24, 2);
    using var output = new MemoryStream();
    using (var writer = new AkbWriter(output, leaveOpen: true)) {
      writer.ContainerKind = AkbContainerKind.Akb2;
      writer.AddEncodedEntry(new AkbWriteEntry("one.pcm", left, AkbCodec.Pcm16Le, 22_050, 1, 16));
      writer.AddEncodedEntry(new AkbWriteEntry("two.pcm", right, AkbCodec.Pcm16Le, 48_000, 2, 24));
      writer.Write();
    }

    output.Position = 0;
    using var reader = new AkbReader(output, leaveOpen: true);
    Assert.Multiple(() => {
      Assert.That(reader.Entries, Has.Count.EqualTo(2));
      Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(left));
      Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(right));
      Assert.That(reader.Entries[0].SampleRate, Is.EqualTo(22_050));
      Assert.That(reader.Entries[1].Channels, Is.EqualTo(2));
    });
  }

  [Test]
  public void Reader_RejectsDeclaredFileSizeMismatch() {
    var file = new byte[0xCC];
    "AKB "u8.CopyTo(file);
    file[0x04] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x06, 2), 0xCC);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08, 4), 0xCD);
    file[0x0C] = 5;
    file[0x0D] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0E, 2), 44_100);

    Assert.Throws<InvalidDataException>(() => _ = new AkbReader(new MemoryStream(file, writable: false)));
  }

  [Test]
  public void Descriptor_AdvertisesRealMagicAndAudioCapabilities() {
    var descriptor = new AkbFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Category, Is.EqualTo(FormatCategory.Audio));
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmTarget>());
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor, Is.InstanceOf<IContainerRemuxable>());
      Assert.That(descriptor.MagicSignatures.Select(static signature => signature.Bytes),
        Does.Contain("AKB "u8.ToArray()).And.Contain("AKB2"u8.ToArray()));
    });
  }

  [Test]
  public void CanEncode_RejectsUnobservedCombinations() {
    var descriptor = new AkbFormatDescriptor();
    var stereo = new AudioPcmFormat(44_100, 2, 16);

    Assert.Multiple(() => {
      Assert.That(descriptor.CanEncode(stereo, "pcm16le", Options(("Variant", "Classic")), out _), Is.False);
      Assert.That(descriptor.CanEncode(stereo, "aac", Options(("Variant", "Akb2")), out _), Is.False);
      Assert.That(descriptor.CanEncode(stereo, "ms-adpcm", Options(("Variant", "Classic"), ("ClassicVersion", "0")), out _), Is.False);
      Assert.That(descriptor.CanEncode(stereo, "vorbis", Options(("Variant", "Akb2"), ("Encrypt", "true")), out _), Is.False);
    });
  }

  private static FormatCreateOptions Options(params (string Key, string Value)[] values) {
    var options = new FormatCreateOptions();
    foreach (var (key, value) in values)
      options.FormatSpecific[key] = value;
    return options;
  }

  private static byte[] MakePcm(int frames, int channels) {
    var result = new byte[checked(frames * channels * 2)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)(((frame * 977 + channel * 8_123) % 50_000) - 25_000);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan((frame * channels + channel) * 2, 2), sample);
      }
    return result;
  }
}
