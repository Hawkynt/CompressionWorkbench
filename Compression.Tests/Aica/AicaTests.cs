#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AicaAdpcm;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aica;

namespace Compression.Tests.Aica;

[TestFixture]
public class AicaTests {

  private static byte[] MakeAica(int byteCount) {
    var data = new byte[byteCount];
    for (var i = 0; i < byteCount; ++i)
      data[i] = (byte)(((i * 3) & 0x07) << 4 | ((i * 5) & 0x07));
    return data;
  }

  [Test]
  public void Descriptor_ListsFullMonoAndMetadata() {
    var blob = MakeAica(64);
    using var ms = new MemoryStream(blob);
    var entries = new AicaFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.aica" && e.Kind == "Container"), Is.True);
      Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    });
  }

  [Test]
  public void Descriptor_ExtractedChannel_IsValidMonoRiffAt22050Hz() {
    var blob = MakeAica(64);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AicaFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), Is.True);
      Assert.That(wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)AicaFormatDescriptor.AssumedSampleRate));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16), "16-bit decoded");
    });

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(blob.Length * 2 * 2)));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsWithinTolerance() {
    const int n = 2000;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = (short)(8000.0 * i / n * Math.Sin(2 * Math.PI * i / 48.0));
    var le = ShortsToLe(pcm);
    var wavBlob = PcmCodec.ToWavBlob(le, channels: 1, AicaFormatDescriptor.AssumedSampleRate, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wavBlob) };
    using var aicaOut = new MemoryStream();
    new AicaFormatDescriptor().Create(aicaOut, inputs, new FormatCreateOptions());
    var aica = aicaOut.ToArray();

    Assert.That(aica.Length, Is.EqualTo((n + 1) / 2), "two samples per AICA byte");

    var decoded = AicaAdpcmCodec.Decode(aica);
    var maxErr = 0;
    for (var i = 0; i < n; ++i) maxErr = Math.Max(maxErr, Math.Abs(pcm[i] - decoded[i]));
    Assert.That(maxErr, Is.LessThan(4000), $"AICA round-trip max error {maxErr}");
  }

  [Test]
  public void Create_From24BitMonoWav_RequantizesBeforeEncoding() {
    short[] samples = [-30000, -12345, -1, 0, 1, 12345, 30000, 16000];
    var pcm16 = ShortsToLe(samples);
    var pcm24 = PcmCodec.Requantize(pcm16, 16, 24);
    var wav = PcmCodec.ToWavBlob(pcm24, channels: 1, sampleRate: 48_000, bitsPerSample: 24);

    using var output = new MemoryStream();
    new AicaFormatDescriptor().Create(
      output,
      [ArchiveInputInfo.InMemory("source.wav", wav)],
      new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(AicaAdpcmCodec.Encode(samples)));
  }

  [Test]
  public void Create_RejectsMultipleWavsInsteadOfDroppingChannels() {
    var wav = PcmCodec.ToWavBlob(new byte[16], channels: 1, sampleRate: 22_050, bitsPerSample: 16);
    var inputs = new[] {
      ArchiveInputInfo.InMemory("FL.wav", wav),
      ArchiveInputInfo.InMemory("FR.wav", wav),
    };

    using var output = new MemoryStream();
    var error = Assert.Throws<InvalidOperationException>(() =>
      new AicaFormatDescriptor().Create(output, inputs, new FormatCreateOptions()));

    Assert.That(error!.Message, Does.Contain("exactly one mono WAV"));
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void Create_PassesThroughFullAica() {
    var blob = MakeAica(40);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.aica", blob) };
    using var aicaOut = new MemoryStream();
    new AicaFormatDescriptor().Create(aicaOut, inputs, new FormatCreateOptions());
    Assert.That(aicaOut.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void NativeAudioInventory_AdvertisesEncodeDecodeMuxAndDemux() {
    var capability = AudioConversionInventory.Describe(new AicaFormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(capability.CanDecodePcm, Is.True);
      Assert.That(capability.CanEncodePcm, Is.True);
      Assert.That(capability.CanDemuxEncoded, Is.True);
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.EncodeCodecs, Is.EqualTo(new[] { AicaFormatDescriptor.CodecId }));
      Assert.That(capability.MuxCodecs, Is.EqualTo(new[] { AicaFormatDescriptor.CodecId }));
    });
  }

  [TestCase(8_000)]
  [TestCase(11_025)]
  [TestCase(22_050)]
  [TestCase(32_000)]
  [TestCase(44_100)]
  [TestCase(48_000)]
  [TestCase(96_000)]
  [TestCase(192_000)]
  public void PcmTarget_EncodesAtAnyPositiveOutOfBandSampleRate(int sampleRate) {
    short[] samples = [0, 100, 500, 1200, 2500, 5000, -2000, -8000, 10000, -12000];
    var pcm = new AudioPcmBuffer(
      new AudioPcmFormat(sampleRate, 1, 16, AudioPcmEncoding.SignedInteger),
      ShortsToLe(samples));
    var target = (IAudioPcmTarget)new AicaFormatDescriptor();

    Assert.That(target.CanEncode(pcm.Format, AicaFormatDescriptor.CodecId, new FormatCreateOptions(), out var reason),
      Is.True, reason);

    using var output = new MemoryStream();
    target.EncodePcm(output, pcm, AicaFormatDescriptor.CodecId, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(AicaAdpcmCodec.Encode(samples)));
  }

  [Test]
  public void PcmTarget_RejectsParametersRawAicaCannotRepresent() {
    var target = (IAudioPcmTarget)new AicaFormatDescriptor();
    var options = new FormatCreateOptions();

    Assert.Multiple(() => {
      Assert.That(target.CanEncode(
        new AudioPcmFormat(22_050, 2, 16, AudioPcmEncoding.SignedInteger),
        AicaFormatDescriptor.CodecId, options, out var stereoReason), Is.False);
      Assert.That(stereoReason, Does.Contain("multichannel"));

      Assert.That(target.CanEncode(
        new AudioPcmFormat(22_050, 1, 8, AudioPcmEncoding.UnsignedInteger),
        AicaFormatDescriptor.CodecId, options, out var widthReason), Is.False);
      Assert.That(widthReason, Does.Contain("PCM16"));

      Assert.That(target.CanEncode(
        new AudioPcmFormat(0, 1, 16, AudioPcmEncoding.SignedInteger),
        AicaFormatDescriptor.CodecId, options, out var rateReason), Is.False);
      Assert.That(rateReason, Does.Contain("positive sample rate"));
    });
  }

  [Test]
  public void Demux_ExposesOneExactPacketAndHeaderlessDefaults() {
    var blob = MakeAica(37);
    var source = (IAudioDemuxSource)new AicaFormatDescriptor();
    using var input = new MemoryStream(blob);

    Assert.That(source.TryDemux(input, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo(AicaFormatDescriptor.CodecId));
      Assert.That(encoded.Format.SampleRate, Is.EqualTo(AicaFormatDescriptor.AssumedSampleRate));
      Assert.That(encoded.Format.Channels, Is.EqualTo(1));
      Assert.That(encoded.Format.BitsPerSample, Is.EqualTo(4));
      Assert.That(encoded.CodecPrivateData, Is.Null);
      Assert.That(encoded.Packets, Has.Count.EqualTo(1));
      Assert.That(encoded.Packets[0].Data, Is.EqualTo(blob));
      Assert.That(encoded.Packets[0].DurationSamples, Is.EqualTo(blob.Length * 2));
      Assert.That(encoded.Packets[0].IsHeader, Is.False);
    });
  }

  [Test]
  public void Mux_ConcatenatesCompatiblePacketsWithoutReencoding() {
    var mux = (IAudioMuxTarget)new AicaFormatDescriptor();
    var stream = new AudioEncodedStream(
      new AudioStreamFormat(AicaFormatDescriptor.CodecId, 48_000, 1, 4),
      [
        new AudioPacket([0x10, 0x32], DurationSamples: 4),
        new AudioPacket([0x54, 0x76, 0x18], DurationSamples: 6),
      ]);

    using var output = new MemoryStream();
    mux.Mux(output, stream, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0x10, 0x32, 0x54, 0x76, 0x18 }));
  }

  [Test]
  public void Mux_RejectsMetadataRawAicaCannotPreserve() {
    var mux = (IAudioMuxTarget)new AicaFormatDescriptor();
    var options = new FormatCreateOptions();

    Assert.That(mux.CanMux(
      new AudioStreamFormat(AicaFormatDescriptor.CodecId, 22_050, 2, 4),
      options, out var reason), Is.False);
    Assert.That(reason, Does.Contain("multichannel"));

    var trimmed = new AudioEncodedStream(
      new AudioStreamFormat(AicaFormatDescriptor.CodecId, 22_050, 1, 4),
      [new AudioPacket([0x12, 0x34], DurationSamples: 3)]);
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => mux.Mux(output, trimmed, options));
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void ExplicitSameFormatConversion_RemuxesBitExactly() {
    var blob = MakeAica(129);
    var descriptor = new AicaFormatDescriptor();
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();

    AudioConversionOperation.Convert(
      input,
      descriptor,
      output,
      descriptor,
      new FormatCreateOptions(AicaFormatDescriptor.CodecId));

    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  private static byte[] ShortsToLe(ReadOnlySpan<short> samples) {
    var bytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
    return bytes;
  }
}
