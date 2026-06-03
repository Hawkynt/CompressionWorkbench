#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using FileFormat.Brstm;

namespace Compression.Tests.Brstm;

[TestFixture]
public class BrstmTests {

  private static short[] MakeTone(int n, double period, double amp, double phase = 0) {
    var s = new short[n];
    for (var i = 0; i < n; ++i)
      s[i] = (short)(Math.Sin(i * 2.0 * Math.PI / period + phase) * amp);
    return s;
  }

  private static byte[] MonoWav(short[] samples, int sampleRate) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  [Test]
  public void Writer_Reader_RoundTripsStructureAndCloseness() {
    var left = MakeTone(20000, 50.0, 11000);
    var right = MakeTone(20000, 80.0, 9000, Math.PI / 3);

    var blob = new BrstmWriter().Write([left, right], 32000);

    // Magic + BOM.
    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("RSTM"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(4)), Is.EqualTo(0xFEFF));

    var parsed = new BrstmReader().Read(blob);
    Assert.That(parsed.Info.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Info.SampleRate, Is.EqualTo(32000));
    Assert.That(parsed.Info.Codec, Is.EqualTo(2)); // DSP-ADPCM
    Assert.That(parsed.Info.TotalSamples, Is.EqualTo(20000));
    Assert.That(parsed.Pcm[0].Length, Is.EqualTo(20000));

    AssertClose(left, parsed.Pcm[0], 900);
    AssertClose(right, parsed.Pcm[1], 900);
  }

  private static void AssertClose(short[] expected, short[] actual, double maxRms) {
    Assert.That(actual.Length, Is.EqualTo(expected.Length));
    double sumSq = 0;
    for (var i = 0; i < expected.Length; ++i) {
      double d = actual[i] - expected[i];
      sumSq += d * d;
    }
    var rms = Math.Sqrt(sumSq / expected.Length);
    Assert.That(rms, Is.LessThan(maxRms), $"RMS {rms} exceeds {maxRms}");
  }

  [Test]
  public void Descriptor_ListsFullAndPerChannelAndMetadata() {
    var blob = new BrstmWriter().Write([MakeTone(5000, 40, 10000), MakeTone(5000, 60, 8000)], 22050);
    using var ms = new MemoryStream(blob);
    var entries = new BrstmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.brstm" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_ExtractedChannelIsMonoWav() {
    var blob = new BrstmWriter().Write([MakeTone(3000, 30, 9000)], 16000);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new BrstmFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));     // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000u)); // rate
  }

  [Test]
  public void Descriptor_Metadata_DescribesStream() {
    var blob = new BrstmWriter().Write([MakeTone(1000, 25, 8000), MakeTone(1000, 25, 8000)], 48000);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new BrstmFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(ini, Does.Contain("sampleRate=48000"));
    Assert.That(ini, Does.Contain("channels=2"));
    Assert.That(ini, Does.Contain("codec=DSP-ADPCM"));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "RSTM"u8.ToArray().Concat(new byte[0x40]).ToArray();
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(4), 0xFEFF);
    using var ms = new MemoryStream(blob);
    var entries = new BrstmFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.brstm"));
  }

  [Test]
  public void Create_FromPerChannelWavs_ProducesReadableBrstm() {
    var left = MakeTone(9000, 45, 10000);
    var right = MakeTone(9000, 70, 9000, 1.1);

    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("LEFT.wav", MonoWav(left, 24000)),
      Compression.Registry.ArchiveInputInfo.InMemory("RIGHT.wav", MonoWav(right, 24000)),
    };

    using var output = new MemoryStream();
    new BrstmFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    var blob = output.ToArray();

    var parsed = new BrstmReader().Read(blob);
    Assert.That(parsed.Info.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Info.SampleRate, Is.EqualTo(24000));
    AssertClose(left, parsed.Pcm[0], 900);
    AssertClose(right, parsed.Pcm[1], 900);
  }

  [Test]
  public void Create_FullPassthrough_ReturnsVerbatim() {
    var original = new BrstmWriter().Write([MakeTone(2000, 30, 8000)], 16000);
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("FULL.brstm", original),
    };
    using var output = new MemoryStream();
    new BrstmFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Writer_MultiBlock_RoundTrips() {
    // > one 0x2000-byte block (14336 samples/block) forces block splitting.
    var mono = MakeTone(40000, 55, 11000);
    var blob = new BrstmWriter().Write([mono], 32000);
    var parsed = new BrstmReader().Read(blob);
    Assert.That(parsed.Info.NumBlocks, Is.GreaterThan(1));
    Assert.That(parsed.Pcm[0].Length, Is.EqualTo(40000));
    AssertClose(mono, parsed.Pcm[0], 900);
  }
}
