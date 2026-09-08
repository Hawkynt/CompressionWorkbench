#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Apc;

namespace Compression.Tests.Apc;

[TestFixture]
public class ApcTests {

  private static byte[] BuildApc(
    byte[] data,
    int rate = 22050,
    int leftInit = 0,
    int rightInit = 0,
    bool stereo = false,
    uint? sampleCount = null,
    string version = "1.20",
    uint? stereoFlag = null
  ) {
    if (version.Length != 4)
      throw new ArgumentException("Test APC version must have four characters.", nameof(version));

    var header = new byte[32];
    "CRYO_APC"u8.CopyTo(header);
    Encoding.Latin1.GetBytes(version).CopyTo(header.AsSpan(8));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), sampleCount ?? (uint)(stereo ? data.Length : data.Length * 2));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)rate);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), leftInit);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), rightInit);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), stereoFlag ?? (stereo ? 1u : 0u));
    return [.. header, .. data];
  }

  private static AudioPcmBuffer Pcm16(int sampleRate, int channels, params short[] samples) {
    var data = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
    return new AudioPcmBuffer(new AudioPcmFormat(sampleRate, channels, 16), data);
  }

  private static short[] ReadPcm16(AudioPcmBuffer pcm) {
    var samples = new short[pcm.InterleavedData.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2));
    return samples;
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    using var input = new MemoryStream(BuildApc([0x40, 0x10]));
    var entries = new ApcFormatDescriptor().List(input, null);

    Assert.Multiple(() => {
      Assert.That(entries.First(e => e.Name == "FULL.apc").Kind, Is.EqualTo("Container"));
      Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
      Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
    });
  }

  [Test]
  public void Descriptor_Mono_DecodesHighNibbleFirst() {
    using var input = new MemoryStream(BuildApc([0x40]));
    var pcm = new ApcFormatDescriptor().DecodePcm(input);

    Assert.That(ReadPcm16(pcm), Is.EqualTo(new short[] { 7, 8 }));
  }

  [Test]
  public void Descriptor_Stereo_DecodesHighNibbleAsLeftAndLowAsRight() {
    using var input = new MemoryStream(BuildApc([0x40], leftInit: 0, rightInit: 1000, stereo: true));
    var pcm = new ApcFormatDescriptor().DecodePcm(input);

    Assert.Multiple(() => {
      Assert.That(pcm.Format.Channels, Is.EqualTo(2));
      Assert.That(ReadPcm16(pcm), Is.EqualTo(new short[] { 7, 1000 }));
    });
  }

  [Test]
  public void Descriptor_InitialPredictorsAreSignedLongsAndCannotOverflowImaState() {
    using var input = new MemoryStream(BuildApc(
      [0x00],
      leftInit: int.MaxValue,
      rightInit: int.MinValue,
      stereo: true));

    var samples = ReadPcm16(new ApcFormatDescriptor().DecodePcm(input));
    Assert.That(samples, Is.EqualTo(new short[] { short.MaxValue, short.MinValue }));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesLeftRight() {
    using var input = new MemoryStream(BuildApc(new byte[16], stereo: true));
    var entries = new ApcFormatDescriptor().List(input, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
      Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    });
  }

  [Test]
  public void Descriptor_RejectsHeaderPayloadLengthMismatchAndOddMonoCount() {
    var descriptor = new ApcFormatDescriptor();
    using var tooShort = new MemoryStream(BuildApc([0x40], sampleCount: 4));
    using var oddMono = new MemoryStream(BuildApc([0x40], sampleCount: 1));

    Assert.Multiple(() => {
      Assert.That(() => descriptor.DecodePcm(tooShort), Throws.TypeOf<InvalidDataException>());
      Assert.That(() => descriptor.DecodePcm(oddMono), Throws.TypeOf<InvalidDataException>());
    });
  }

  [Test]
  public void Descriptor_BadMagicAndZeroRateAreRejected() {
    var badMagic = BuildApc([0x40]);
    badMagic[0] = (byte)'X';
    var zeroRate = BuildApc([0x40]);
    BinaryPrimitives.WriteUInt32LittleEndian(zeroRate.AsSpan(16), 0);
    var descriptor = new ApcFormatDescriptor();

    using var magicInput = new MemoryStream(badMagic);
    using var rateInput = new MemoryStream(zeroRate);
    Assert.Multiple(() => {
      Assert.That(() => descriptor.List(magicInput, null), Throws.TypeOf<InvalidDataException>());
      Assert.That(() => descriptor.DecodePcm(rateInput), Throws.TypeOf<InvalidDataException>());
    });
  }

  [TestCase(8000, 1)]
  [TestCase(11025, 1)]
  [TestCase(22050, 1)]
  [TestCase(44100, 2)]
  [TestCase(48000, 2)]
  [TestCase(192000, 2)]
  public void Descriptor_EncodePcm_SupportsEveryStructuralChannelModeAndArbitraryPositiveRates(int rate, int channels) {
    var descriptor = new ApcFormatDescriptor();
    var pcm = channels == 1
      ? Pcm16(rate, 1, 1000, 1500, -1000, -500)
      : Pcm16(rate, 2, 1000, -1000, 1500, -1500, -1000, 1000);

    Assert.That(descriptor.CanEncode(pcm.Format, "ima-adpcm-apc", new FormatCreateOptions(), out var reason), Is.True, reason);
    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "ima-adpcm-apc", new FormatCreateOptions());
    var blob = encoded.ToArray();

    Assert.Multiple(() => {
      Assert.That(blob.AsSpan(0, 8).ToArray(), Is.EqualTo("CRYO_APC"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12)), Is.EqualTo((uint)(pcm.InterleavedData.Length / (channels * 2))));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16)), Is.EqualTo((uint)rate));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(28)) == 0, Is.EqualTo(channels == 1));
    });

    using var decodeInput = new MemoryStream(blob);
    var decoded = descriptor.DecodePcm(decodeInput);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(rate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.FrameCount, Is.EqualTo(pcm.FrameCount));
    });
  }

  [Test]
  public void Descriptor_EncodePcm_SeedsPredictorsFromFirstFrameByDefault() {
    var descriptor = new ApcFormatDescriptor();
    var pcm = Pcm16(12345, 2, 1234, -2345, 1500, -2000);
    using var output = new MemoryStream();

    descriptor.EncodePcm(output, pcm, "ima-adpcm", new FormatCreateOptions());
    var blob = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(Encoding.Latin1.GetString(blob, 8, 4), Is.EqualTo("1.20"));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(20)), Is.EqualTo(1234));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(24)), Is.EqualTo(-2345));
      Assert.That(blob.Length, Is.EqualTo(32 + 2));
    });
  }

  [Test]
  public void Descriptor_EncodePcm_PreservesAllConfigurableHeaderFields() {
    var options = new FormatCreateOptions();
    options.FormatSpecific["version"] = "9.99";
    options.FormatSpecific["left-initial-sample"] = int.MinValue.ToString();
    options.FormatSpecific["right-initial-sample"] = int.MaxValue.ToString();
    options.FormatSpecific["stereo-flag"] = uint.MaxValue.ToString();
    var descriptor = new ApcFormatDescriptor();
    using var output = new MemoryStream();

    descriptor.EncodePcm(output, Pcm16(32000, 2, 0, 0, 1, -1), "adpcm_ima_apc", options);
    var blob = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(Encoding.Latin1.GetString(blob, 8, 4), Is.EqualTo("9.99"));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(20)), Is.EqualTo(int.MinValue));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(24)), Is.EqualTo(int.MaxValue));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(28)), Is.EqualTo(uint.MaxValue));
    });
  }

  [Test]
  public void Descriptor_EncodePcm_RejectsOddMonoFramesAndImpossibleFormats() {
    var descriptor = new ApcFormatDescriptor();
    var options = new FormatCreateOptions();
    var oddMono = Pcm16(22050, 1, 1, 2, 3);

    Assert.Multiple(() => {
      Assert.That(() => descriptor.EncodePcm(new MemoryStream(), oddMono, "ima-adpcm-apc", options), Throws.TypeOf<NotSupportedException>());
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(0, 1, 16), "ima-adpcm-apc", options, out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(22050, 3, 16), "ima-adpcm-apc", options, out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(22050, 1, 24), "ima-adpcm-apc", options, out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(22050, 1, 16, AudioPcmEncoding.IeeeFloat), "ima-adpcm-apc", options, out _), Is.False);
    });
  }

  [TestCase(false, 0u)]
  [TestCase(true, 0x80000000u)]
  public void Descriptor_DemuxMux_IsByteExactIncludingNonDefaultHeaderMetadata(bool stereo, uint stereoFlag) {
    var original = BuildApc(
      [0x40, 0x12],
      rate: 12345,
      leftInit: -123456789,
      rightInit: 987654321,
      stereo: stereo,
      version: "2.34",
      stereoFlag: stereoFlag);
    var descriptor = new ApcFormatDescriptor();

    using var input = new MemoryStream(original);
    Assert.That(descriptor.TryDemux(input, out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    using var output = new MemoryStream();
    descriptor.Mux(output, demuxed!, new FormatCreateOptions());

    Assert.Multiple(() => {
      Assert.That(output.ToArray(), Is.EqualTo(original));
      Assert.That(demuxed!.Format.CodecId, Is.EqualTo("ima-adpcm-apc"));
      Assert.That(demuxed.Format.BitsPerSample, Is.EqualTo(4));
      Assert.That(demuxed.Format.Properties!["version"], Is.EqualTo("2.34"));
      Assert.That(demuxed.Format.Properties["stereo-flag"], Is.EqualTo(stereoFlag.ToString()));
    });
  }

  [TestCase(1, 4)]
  [TestCase(2, 2)]
  public void Descriptor_Mux_ConcatenatesPacketsAndDerivesSampleCountFromDurations(int channels, int expectedCount) {
    var packets = channels == 1
      ? new[] { new AudioPacket([0x40], 2), new AudioPacket([0x12], 2) }
      : new[] { new AudioPacket([0x40], 1), new AudioPacket([0x12], 1) };
    var stream = new AudioEncodedStream(
      new AudioStreamFormat("ima-adpcm-apc", 16000, channels, 4),
      packets);
    var descriptor = new ApcFormatDescriptor();
    using var output = new MemoryStream();

    descriptor.Mux(output, stream, new FormatCreateOptions());
    var blob = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12)), Is.EqualTo((uint)expectedCount));
      Assert.That(blob.AsSpan(32).ToArray(), Is.EqualTo(new byte[] { 0x40, 0x12 }));
    });
  }

  [Test]
  public void Descriptor_Mux_RequiresApcSpecificCodecAndValidPacketShape() {
    var descriptor = new ApcFormatDescriptor();
    var options = new FormatCreateOptions();

    Assert.Multiple(() => {
      Assert.That(descriptor.CanMux(new AudioStreamFormat("ima-adpcm", 22050, 1, 4), options, out _), Is.False,
        "WAVE/other IMA packet layouts must not be packet-remuxed as APC.");
      Assert.That(descriptor.CanMux(new AudioStreamFormat("ima-adpcm-apc", 22050, 3, 4), options, out _), Is.False);
      Assert.That(descriptor.CanMux(new AudioStreamFormat("ima-adpcm-apc", 22050, 1, 16), options, out _), Is.False);
    });

    var withHeaderPacket = new AudioEncodedStream(
      new AudioStreamFormat("ima-adpcm-apc", 22050, 1, 4),
      [new AudioPacket([0x40], 2, IsHeader: true)]);
    Assert.That(() => descriptor.Mux(new MemoryStream(), withHeaderPacket, options), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Descriptor_TryDemux_RejectsMalformedContainerWithoutThrowing() {
    var malformed = BuildApc([0x40], sampleCount: 4);
    using var input = new MemoryStream(malformed);

    Assert.That(new ApcFormatDescriptor().TryDemux(input, out var stream), Is.False);
    Assert.That(stream, Is.Null);
  }

  [Test]
  public void Descriptor_Create_FromMonoAndStereoChannelWavs() {
    var descriptor = new ApcFormatDescriptor();
    var left = PcmCodec.ToWavBlob(Pcm16(22050, 1, 1000, 1100, 1200, 1300).InterleavedData, 1, 22050, 16);
    var right = PcmCodec.ToWavBlob(Pcm16(22050, 1, -1000, -1100, -1200, -1300).InterleavedData, 1, 22050, 16);

    using var monoOutput = new MemoryStream();
    descriptor.Create(monoOutput, [ArchiveInputInfo.InMemory("MONO.wav", left)], new FormatCreateOptions());
    using var monoInput = new MemoryStream(monoOutput.ToArray());
    var mono = descriptor.DecodePcm(monoInput);

    using var stereoOutput = new MemoryStream();
    descriptor.Create(stereoOutput,
      [ArchiveInputInfo.InMemory("RIGHT.wav", right), ArchiveInputInfo.InMemory("LEFT.wav", left)],
      new FormatCreateOptions());
    using var stereoInput = new MemoryStream(stereoOutput.ToArray());
    var stereo = descriptor.DecodePcm(stereoInput);

    Assert.Multiple(() => {
      Assert.That(mono.Format.Channels, Is.EqualTo(1));
      Assert.That(mono.FrameCount, Is.EqualTo(4));
      Assert.That(stereo.Format.Channels, Is.EqualTo(2));
      Assert.That(stereo.FrameCount, Is.EqualTo(4));
    });
  }

  [Test]
  public void Descriptor_Create_FullApcIsValidatedAndPassedThroughByteExact() {
    var original = BuildApc([0x40, 0x12], version: "3.21");
    using var output = new MemoryStream();

    new ApcFormatDescriptor().Create(
      output,
      [ArchiveInputInfo.InMemory("FULL.apc", original)],
      new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Descriptor_AdvertisesCreateAndAllAudioConversionSurfaces() {
    var descriptor = new ApcFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmTarget>());
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
    });
  }
}
