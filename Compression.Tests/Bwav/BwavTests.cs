#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using FileFormat.Bwav;

namespace Compression.Tests.Bwav;

[TestFixture]
public class BwavTests {

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

  private static void AssertClose(short[] expected, short[] actual, double maxRms) {
    Assert.That(actual.Length, Is.EqualTo(expected.Length));
    double sumSq = 0;
    for (var i = 0; i < expected.Length; ++i) {
      double d = actual[i] - expected[i];
      sumSq += d * d;
    }
    var rms = Math.Sqrt(sumSq / Math.Max(1, expected.Length));
    Assert.That(rms, Is.LessThan(maxRms), $"RMS {rms} exceeds {maxRms}");
  }

  [Test]
  public void Writer_Reader_RoundTripsStructureAndCloseness() {
    var left = MakeTone(8000, 50, 11000);
    var right = MakeTone(8000, 80, 9000, Math.PI / 3);

    var blob = new BwavWriter().Write([left, right], 32000);

    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("BWAV"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4)), Is.EqualTo(0xFEFF));

    var parsed = new BwavReader().Read(blob);
    Assert.That(parsed.ChannelCount, Is.EqualTo(2));
    Assert.That(parsed.Crc, Is.EqualTo(0u));
    Assert.That(parsed.Channels[0].Codec, Is.EqualTo(1)); // DSP-ADPCM
    Assert.That(parsed.Channels[0].SampleRate, Is.EqualTo(32000));
    Assert.That(parsed.Pcm[0].Length, Is.EqualTo(8000));

    AssertClose(left, parsed.Pcm[0], 900);
    AssertClose(right, parsed.Pcm[1], 900);
  }

  [Test]
  public void Reader_Pcm16Codec_DecodesExactly() {
    // Hand-build a single-channel PCM16 BWAV (codec 0).
    short[] samples = MakeTone(64, 16, 12000);
    const int infoSize = 0x4C;
    var dataStart = 0x10 + infoSize;
    var buf = new byte[dataStart + samples.Length * 2];
    var s = buf.AsSpan();
    "BWAV"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0xFEFF);
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 2);
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], 1);
    var o = 0x10;
    BinaryPrimitives.WriteUInt16LittleEndian(s[o..], 0);            // codec PCM16
    BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], 16000);  // rate
    BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x0C)..], (uint)samples.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 0x34)..], (uint)dataStart);
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(s[(dataStart + i * 2)..], samples[i]);

    var parsed = new BwavReader().Read(buf);
    Assert.That(parsed.Channels[0].Codec, Is.EqualTo(0));
    Assert.That(parsed.Pcm[0], Is.EqualTo(samples));
  }

  [Test]
  public void Descriptor_ListsFullPerChannelAndMetadata() {
    var blob = new BwavWriter().Write([MakeTone(3000, 40, 10000), MakeTone(3000, 60, 8000)], 22050);
    using var ms = new MemoryStream(blob);
    var entries = new BwavFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.bwav" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_Metadata_NotesZeroCrc() {
    var blob = new BwavWriter().Write([MakeTone(1000, 25, 8000)], 48000);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new BwavFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(ini, Does.Contain("codec=DSP-ADPCM"));
    Assert.That(ini, Does.Contain("sampleRate=48000"));
    Assert.That(ini, Does.Contain("crc=0"));
    Assert.That(ini, Does.Contain("note="));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "BWAV"u8.ToArray().Concat(new byte[0x10]).ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), 0xFEFF);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0E), 1); // claims 1 channel, no info block
    using var ms = new MemoryStream(blob);
    var entries = new BwavFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.bwav"));
  }

  [Test]
  public void Create_FromPerChannelWavs_RoundTripsThroughReader() {
    var left = MakeTone(6000, 45, 10000);
    var right = MakeTone(6000, 70, 9000, 1.1);

    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("LEFT.wav", MonoWav(left, 24000)),
      Compression.Registry.ArchiveInputInfo.InMemory("RIGHT.wav", MonoWav(right, 24000)),
    };

    using var output = new MemoryStream();
    new BwavFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());

    var parsed = new BwavReader().Read(output.ToArray());
    Assert.That(parsed.ChannelCount, Is.EqualTo(2));
    Assert.That(parsed.Channels[0].SampleRate, Is.EqualTo(24000));
    AssertClose(left, parsed.Pcm[0], 900);
    AssertClose(right, parsed.Pcm[1], 900);
  }

  [Test]
  public void Create_FullPassthrough_ReturnsVerbatim() {
    var original = new BwavWriter().Write([MakeTone(1000, 30, 8000)], 16000);
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("FULL.bwav", original),
    };
    using var output = new MemoryStream();
    new BwavFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
