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

  private static byte[] SampleAdx(int frames = 4, int channels = 1, int sampleRate = 22050) {
    var count = AdxCodec.SamplesPerFrame * frames * channels;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 80) * 8000);
    return AdxCodec.Encode(pcm, channels, sampleRate);
  }

  private static byte[] MonoWav(int samples, int sampleRate) {
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 8.0) * 6000));
    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(SampleAdx());
    var entries = new AdxFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.adx"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.adx").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesPerChannelWavs() {
    using var ms = new MemoryStream(SampleAdx(frames: 3, channels: 2, sampleRate: 44100));
    var entries = new AdxFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.Count(e => e.Kind == "Channel"), Is.EqualTo(2));
  }

  [Test]
  public void Descriptor_MonoWav_HasHeaderSampleRateAndDecodedLength() {
    const int frames = 4;
    const int rate = 22050;
    using var ms = new MemoryStream(SampleAdx(frames, channels: 1, sampleRate: rate));
    using var output = new MemoryStream();
    new AdxFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(frames * AdxCodec.SamplesPerFrame * 2)));
  }

  [Test]
  public void Descriptor_EncryptedOrUnknown_FallsBackToFullOnly() {
    var adx = SampleAdx();
    adx[19] |= 0x08; // encrypted flag → FULL-only fallback
    using var ms = new MemoryStream(adx);
    var entries = new AdxFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.adx"));
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_ProducesValidAdx() {
    const int samples = AdxCodec.SamplesPerFrame * 3;
    var wav = MonoWav(samples, 16000);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("voice.wav", wav) };
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var adx = output.ToArray();

    var info = AdxCodec.ReadInfo(adx);
    Assert.That(info.SampleRate, Is.EqualTo(16000));
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.TotalSamples, Is.EqualTo(samples));
    Assert.That(info.IsStandard, Is.True);
  }

  [Test]
  public void Descriptor_Create_RoundTrips_ChannelPresentAndSampleCountMatches() {
    const int samples = AdxCodec.SamplesPerFrame * 5;
    var wav = MonoWav(samples, 32000);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new AdxFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var reopen = new MemoryStream(created.ToArray());
    var entries = new AdxFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var monoOut = new MemoryStream();
    using var reopen2 = new MemoryStream(created.ToArray());
    new AdxFormatDescriptor().ExtractEntry(reopen2, "MONO.wav", monoOut, null);
    var mono = monoOut.ToArray();
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(samples * 2)));
  }

  [Test]
  public void Descriptor_Create_FromStereoChannelWavs_ProducesStereoAdx() {
    const int samples = AdxCodec.SamplesPerFrame * 4;
    var left = MonoWav(samples, 48000);
    var right = MonoWav(samples, 48000);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
      ArchiveInputInfo.InMemory("LEFT.wav", left),
    };
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var info = AdxCodec.ReadInfo(output.ToArray());
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.TotalSamples, Is.EqualTo(samples));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullAdx() {
    var original = SampleAdx(2, channels: 1, sampleRate: 8000);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.adx", original) };
    using var output = new MemoryStream();
    new AdxFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Metadata_CarriesSampleRateAndChannels() {
    using var ms = new MemoryStream(SampleAdx(frames: 2, channels: 1, sampleRate: 44100));
    using var output = new MemoryStream();
    new AdxFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(text, Does.Contain("sample_rate=44100"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("highpass_frequency=500"));
  }
}
