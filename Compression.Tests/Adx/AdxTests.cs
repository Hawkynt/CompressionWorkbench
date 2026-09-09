#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.CriAdx;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Adx;

namespace Compression.Tests.Adx;

[TestFixture]
public class AdxTests {
  private static byte[] SampleAdx(int frames = 4, int channels = 1, int sampleRate = 22050, AdxEncodeOptions? options = null) {
    var count = AdxCodec.SamplesPerFrame * frames;
    var pcm = new short[count * channels];
    for (var frame = 0; frame < count; ++frame)
      for (var channel = 0; channel < channels; ++channel)
        pcm[frame * channels + channel] = (short)(Math.Sin(frame / (7.0 + channel)) * 8000);
    return AdxCodec.Encode(pcm, channels, sampleRate, options ?? new AdxEncodeOptions());
  }

  private static byte[] MonoWav(int samples, int sampleRate) {
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 8.0) * 6000));
    return PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);
  }

  [Test]
  public void Descriptor_AdvertisesCanonicalAudioConversionAndPacketInterfaces() {
    var descriptor = new AdxFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IAudioContainerFormat>());
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmTarget>());
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    using var input = new MemoryStream(SampleAdx());
    var entries = new AdxFormatDescriptor().List(input, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.adx"), Is.True);
      Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    });
  }

  [Test]
  public void Descriptor_Stereo_SurfacesPerChannelWavs() {
    using var input = new MemoryStream(SampleAdx(3, 2, 44100));
    var entries = new AdxFormatDescriptor().List(input, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.Count(e => e.Kind == "Channel"), Is.EqualTo(2));
  }

  [Test]
  public void Descriptor_Create_FromStereoChannelWavs_ProducesSelectedVariant() {
    const int samples = AdxCodec.SamplesPerFrame * 4;
    var left = MonoWav(samples, 48000);
    var right = MonoWav(samples, 48000);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
      ArchiveInputInfo.InMemory("LEFT.wav", left),
    };
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
        ["Encoding"] = "Fixed",
        ["Version"] = "4",
        ["HeaderAlignment"] = "256",
        ["LoopStartSample"] = "32",
        ["LoopEndSample"] = "96",
      },
    };
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Create(output, inputs, options);
    var info = AdxCodec.ReadInfo(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(info.EncodingType, Is.EqualTo(AdxCodec.EncodingTypeFixed));
      Assert.That(info.Version, Is.EqualTo(4));
      Assert.That(info.Channels, Is.EqualTo(2));
      Assert.That(info.TotalSamples, Is.EqualTo(samples));
      Assert.That(info.DataOffset % 256, Is.Zero);
      Assert.That(info.LoopStartSample, Is.EqualTo(32));
      Assert.That(info.LoopEndSample, Is.EqualTo(96));
    });
  }

  [Test]
  public void Descriptor_Create_EncryptedType9_ProducesOpaqueButDemuxableAdx() {
    var wav = MonoWav(AdxCodec.SamplesPerFrame * 3, 22050);
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
        ["Version"] = "4",
        ["EncryptionRevision"] = "9",
        ["EncryptionStart"] = "4660",
        ["EncryptionMultiplier"] = "7997",
        ["EncryptionAddend"] = "1111",
      },
    };
    using var output = new MemoryStream();
    var descriptor = new AdxFormatDescriptor();
    descriptor.Create(output, [ArchiveInputInfo.InMemory("MONO.wav", wav)], options);
    var bytes = output.ToArray();
    var info = AdxCodec.ReadInfo(bytes);
    Assert.That(info.IsEncrypted, Is.True);

    using var listInput = new MemoryStream(bytes);
    var entries = descriptor.List(listInput, null);
    Assert.That(entries.Select(static e => e.Name), Is.EquivalentTo(["FULL.adx"]));

    Assert.That(descriptor.TryDemux(new MemoryStream(bytes), out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.That(encoded!.Format.CodecId, Is.EqualTo("adx"));
  }

  [Test]
  public void PcmInterfaces_EncodeThenDecode_RoundTripGeometry() {
    const int frames = 101;
    var pcmBytes = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(i * 2), (short)(Math.Sin(i / 8.0) * 6000));
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(32000, 1, 16), pcmBytes);
    var descriptor = new AdxFormatDescriptor();
    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "adx", new FormatCreateOptions());
    var decoded = descriptor.DecodePcm(new MemoryStream(encoded.ToArray()));
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(32000));
      Assert.That(decoded.Format.Channels, Is.EqualTo(1));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.FrameCount, Is.EqualTo(frames));
    });
  }

  [Test]
  public void DemuxMux_StandardAdx_IsByteExactIncludingHeaderLoopsAndEndMarker() {
    var original = SampleAdx(5, 2, 44100, new AdxEncodeOptions {
      Version = AdxHeaderVersion.Version4,
      LoopStartSample = 32,
      LoopEndSample = 128,
      HeaderAlignment = 256,
      WriteEndMarker = true,
    });
    var descriptor = new AdxFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(original), out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);

    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, stream!, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void DemuxMux_EncryptedAdx_IsByteExactWithoutDecrypting() {
    var original = SampleAdx(4, 1, 22050, new AdxEncodeOptions {
      Version = AdxHeaderVersion.Version4,
      Encryption = new AdxEncryptionKey(0x1234, 0x1F3D, 0x0457, 8),
    });
    var descriptor = new AdxFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(original), out var stream), Is.True);
    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, stream!, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void DemuxMux_AhxPayload_IsByteExact() {
    var header = AdxCodec.BuildAhxHeader(22050, 2304, AdxCodec.EncodingTypeAhx11);
    var payload = Enumerable.Range(0, 257).Select(static i => (byte)(i * 17)).ToArray();
    var original = header.Concat(payload).ToArray();
    var descriptor = new AdxFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(original), out var stream), Is.True);
    Assert.That(stream!.Format.CodecId, Is.EqualTo("ahx"));
    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, stream, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Mux_AdxWithoutPrivateHeader_RebuildsMinimalHeaderFromProperties() {
    var source = SampleAdx(2, 1, 22050);
    var info = AdxCodec.ReadInfo(source);
    var payload = source.AsSpan(info.DataOffset).ToArray();
    var format = new AudioStreamFormat("adx", 22050, 1, 4,
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["encoding-type"] = "3",
        ["version"] = "3",
        ["revision"] = "0",
        ["highpass-frequency"] = "500",
        ["total-samples"] = (AdxCodec.SamplesPerFrame * 2).ToString(),
      });
    var encoded = new AudioEncodedStream(format, [new AudioPacket(payload, AdxCodec.SamplesPerFrame * 2)]);
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());
    var rebuilt = output.ToArray();
    var rebuiltInfo = AdxCodec.ReadInfo(rebuilt);
    Assert.That(rebuiltInfo.TotalSamples, Is.EqualTo(AdxCodec.SamplesPerFrame * 2));
    Assert.That(rebuilt.AsSpan(rebuiltInfo.DataOffset).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullAdx() {
    var original = SampleAdx(2, 1, 8000);
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("FULL.adx", original)], new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Metadata_CarriesVariantAndLoopFields() {
    var adx = SampleAdx(5, 1, 44100, new AdxEncodeOptions {
      Encoding = AdxEncodingMode.Exponential,
      Version = AdxHeaderVersion.Version4,
      LoopStartSample = 32,
      LoopEndSample = 128,
    });
    using var output = new MemoryStream();
    new AdxFormatDescriptor().ExtractEntry(new MemoryStream(adx), "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("sample_rate=44100"));
      Assert.That(text, Does.Contain("version=4"));
      Assert.That(text, Does.Contain("encoding_type=4"));
      Assert.That(text, Does.Contain("loop_start_sample=32"));
      Assert.That(text, Does.Contain("loop_end_sample=128"));
    });
  }

  [Test]
  public void CanEncode_RejectsNonPcm16AndMoreThanEightChannels() {
    var descriptor = new AdxFormatDescriptor();
    Assert.That(descriptor.CanEncode(new AudioPcmFormat(44100, 2, 24), "adx", new FormatCreateOptions(), out _), Is.False);
    Assert.That(descriptor.CanEncode(new AudioPcmFormat(44100, 9, 16), "adx", new FormatCreateOptions(), out _), Is.False);
  }
}
