#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.InterplayAcm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Acm;

namespace Compression.Tests.Acm;

[TestFixture]
public class AcmTests {

  private static byte[] BuildAcm(uint totalSamples, int channels, int sampleRate, int level, int rows, byte[] bitstream) {
    var header = new byte[14];
    BinaryPrimitives.WriteUInt32LittleEndian(header, InterplayAcmCodec.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), totalSamples);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)channels);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)sampleRate);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)((level & 0xF) | (rows << 4)));
    var blob = new byte[header.Length + bitstream.Length];
    header.CopyTo(blob.AsSpan());
    bitstream.CopyTo(blob.AsSpan(header.Length));
    return blob;
  }

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: 22050, level: 2, rows: 8, bitstream: new byte[64]);
    using var ms = new MemoryStream(blob);
    var entries = new AcmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.acm" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Extract_FullAcm_RoundTripsBytes() {
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: 22050, level: 2, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "FULL.acm", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Extract_MonoWav_IsValidRiffAtHeaderSampleRate() {
    const int rate = 22050;
    var blob = BuildAcm(totalSamples: 32, channels: 1, sampleRate: rate, level: 2, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));
  }

  [Test]
  public void Metadata_RecordsRawChannelCount() {
    var blob = BuildAcm(totalSamples: 16, channels: 2, sampleRate: 22050, level: 1, rows: 8, bitstream: new byte[64]);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AcmFormatDescriptor().ExtractEntry(input, "metadata.ini", output, null);
    var meta = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(meta, Does.Contain("channels=2"));
    Assert.That(meta, Does.Contain("sample_rate=22050"));
    Assert.That(meta, Does.Contain("level=1"));
  }

  [Test]
  public void List_UndecodableInput_FallsBackToFullPlusMetadata() {
    var blob = new byte[10];
    BinaryPrimitives.WriteUInt32LittleEndian(blob, InterplayAcmCodec.Magic);
    using var ms = new MemoryStream(blob);
    var entries = new AcmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.acm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Encoder_Level0_RoundTripHasBoundedQuantizationError() {
    var samples = Enumerable.Range(0, 256)
      .Select(static i => (short)(Math.Sin(i * 0.12) * 5000))
      .ToArray();
    var encoded = InterplayAcmEncoder.Encode(samples, channels: 1, sampleRate: 22050, level: 0, rows: 16);
    var decoded = InterplayAcmCodec.Decode(encoded).Samples;

    Assert.That(decoded.Length, Is.EqualTo(samples.Length));
    var maxError = decoded.Zip(samples, static (actual, expected) => Math.Abs(actual - expected)).Max();
    Assert.That(maxError, Is.LessThanOrEqualTo(256));
  }

  [Test]
  public void Encoder_Level7_FullBlockRoundTripHasBoundedError() {
    const int level = 7, rows = 16;
    var samples = Enumerable.Range(0, rows << level)
      .Select(static i => (short)(Math.Sin(i * 0.012) * 5000))
      .ToArray();
    var encoded = InterplayAcmEncoder.Encode(samples, channels: 1, sampleRate: 22050, level, rows);
    var decoded = InterplayAcmCodec.Decode(encoded).Samples;

    Assert.That(decoded.Length, Is.EqualTo(samples.Length));
    var meanError = decoded.Zip(samples, static (actual, expected) => Math.Abs(actual - expected)).Average();
    Assert.That(meanError, Is.LessThan(300));
  }

  [Test]
  public void Encoder_EveryHeaderLevelHasEncodableGeometry() {
    for (var level = 0; level <= 15; ++level)
      Assert.That(Enumerable.Range(1, 4095).Any(rows => InterplayAcmEncoder.IsGeometryEncodable(level, rows)),
        Is.True, $"level {level}");
  }

  [Test]
  public void Create_PassesThroughFullAcmByteExactly() {
    var original = InterplayAcmEncoder.Encode([0, 100, -100, 200, -200, 300, -300, 0], 1, 22050, level: 0, rows: 8);
    var inputs = new[] { ArchiveInputInfo.InMemory("FULL.acm", original) };
    using var output = new MemoryStream();
    new AcmFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Create_FromStereoPcmWav_EncodesHeaderAndSamples() {
    const int frames = 64;
    var pcm = new byte[frames * 4];
    for (var frame = 0; frame < frames; ++frame) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4), (short)(frame * 20));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4 + 2), (short)(-frame * 15));
    }
    var wav = PcmCodec.ToWavBlob(pcm, channels: 2, sampleRate: 44100, bitsPerSample: 16);
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Level"] = "0", ["Rows"] = "16" },
    };
    using var output = new MemoryStream();
    new AcmFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("stereo.wav", wav)], options);

    var header = InterplayAcmCodec.ParseHeader(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(header.Channels, Is.EqualTo(2));
      Assert.That(header.SampleRate, Is.EqualTo(44100));
      Assert.That(header.Level, Is.EqualTo(0));
      Assert.That(header.Rows, Is.EqualTo(16));
      Assert.That(header.TotalSamples, Is.EqualTo((uint)(frames * 2)));
    });
  }

  [Test]
  public void FullRemux_MetadataCanPatchTimingButNotGeometry() {
    var original = InterplayAcmEncoder.Encode(new short[32], 1, 22050, level: 2, rows: 8);
    var descriptor = new AcmFormatDescriptor();

    using var patched = new MemoryStream();
    descriptor.Create(patched, [
      ArchiveInputInfo.InMemory("FULL.acm", original),
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes("sample_rate=44100\nchannels=2\nlevel=2\nrows=8\n")),
    ], new FormatCreateOptions());
    var header = InterplayAcmCodec.ParseHeader(patched.ToArray());
    Assert.Multiple(() => {
      Assert.That(header.SampleRate, Is.EqualTo(44100));
      Assert.That(header.Channels, Is.EqualTo(2));
    });

    Assert.Throws<InvalidOperationException>(() => descriptor.Create(new MemoryStream(), [
      ArchiveInputInfo.InMemory("FULL.acm", original),
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes("level=3\nrows=8\n")),
    ], new FormatCreateOptions()));
  }

  [Test]
  public void DemuxMux_RoundTripsEncodedPayloadByteExactly() {
    var original = InterplayAcmEncoder.Encode(
      Enumerable.Range(0, 128).Select(static i => (short)(Math.Sin(i * 0.1) * 3000)).ToArray(),
      1, 22050, level: 0, rows: 16);
    var descriptor = new AcmFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(original), out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);

    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, stream!, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Descriptor_ExposesAllAudioConversionSurfaces() {
    var descriptor = new AcmFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioPcmTarget>());
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    });
  }
}
