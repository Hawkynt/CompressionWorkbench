#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using FileFormat.Swav;

namespace Compression.Tests.Swav;

[TestFixture]
public class SwavTests {

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
  public void Writer_Reader_Pcm16_RoundTripsExactly() {
    var pcm = MakeTone(2000, 40, 11000);
    var blob = new SwavWriter().Write(pcm, 22050);

    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("SWAV"u8.ToArray()));

    var parsed = new SwavReader().Read(blob);
    Assert.That(parsed.WaveType, Is.EqualTo(1));
    Assert.That(parsed.SampleRate, Is.EqualTo(22050));
    Assert.That(parsed.Pcm, Is.EqualTo(pcm));
  }

  [Test]
  public void Reader_Pcm8_DecodesSigned() {
    // Craft a minimal PCM8 SWAV by hand.
    sbyte[] samples = [0, 64, -64, 127, -128];
    var blob = BuildSwav(waveType: 0, samples.Select(s => (byte)s).ToArray(), 16000);

    var parsed = new SwavReader().Read(blob);
    Assert.That(parsed.WaveType, Is.EqualTo(0));
    Assert.That(parsed.Pcm.Length, Is.EqualTo(samples.Length));
    for (var i = 0; i < samples.Length; ++i)
      Assert.That(parsed.Pcm[i], Is.EqualTo((short)(samples[i] << 8)));
  }

  [Test]
  public void Reader_ImaAdpcm_DecodesWithInitialState() {
    // 4-byte state header (predictor=0, index=0) + a handful of nibble bytes.
    byte[] sampleData = [0x00, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x77, 0x99, 0xAA];
    var blob = BuildSwav(waveType: 2, sampleData, 16000);

    var parsed = new SwavReader().Read(blob);
    Assert.That(parsed.WaveType, Is.EqualTo(2));
    // first sample is the initial predictor (0), then 2 per nibble byte.
    Assert.That(parsed.Pcm.Length, Is.EqualTo(1 + 6 * 2));
    Assert.That(parsed.Pcm[0], Is.EqualTo(0));
    // monotonic-ish positive growth for the first low/high nibbles (1,1) verifies LOW-first order.
    Assert.That(parsed.Pcm[1], Is.GreaterThan((short)0));
  }

  [Test]
  public void Descriptor_ListsFullMonoAndMetadata() {
    var blob = new SwavWriter().Write(MakeTone(1000, 30, 9000), 32000);
    using var ms = new MemoryStream(blob);
    var entries = new SwavFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.swav" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_Metadata_DescribesSample() {
    var blob = new SwavWriter().Write(MakeTone(500, 25, 8000), 44100);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SwavFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(ini, Does.Contain("waveType=PCM16"));
    Assert.That(ini, Does.Contain("sampleRate=44100"));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "SWAV"u8.ToArray().Concat(new byte[0x20]).ToArray();
    using var ms = new MemoryStream(blob);
    var entries = new SwavFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.swav"));
  }

  [Test]
  public void Create_FromMonoWav_ProducesReadableSwav() {
    var pcm = MakeTone(1500, 35, 10000);
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("MONO.wav", MonoWav(pcm, 24000)),
    };
    using var output = new MemoryStream();
    new SwavFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());

    var parsed = new SwavReader().Read(output.ToArray());
    Assert.That(parsed.SampleRate, Is.EqualTo(24000));
    Assert.That(parsed.Pcm, Is.EqualTo(pcm));
  }

  [Test]
  public void Create_FullPassthrough_ReturnsVerbatim() {
    var original = new SwavWriter().Write(MakeTone(800, 20, 7000), 16000);
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("FULL.swav", original),
    };
    using var output = new MemoryStream();
    new SwavFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  // ── helper: minimal NDS SWAV builder for non-PCM16 wave types ──
  internal static byte[] BuildSwav(byte waveType, byte[] sampleData, int sampleRate) {
    var dataPayload = 12 + sampleData.Length;
    var dataBlockSize = 8 + dataPayload;
    var fileSize = 0x10 + dataBlockSize;
    var buf = new byte[fileSize];
    var s = buf.AsSpan();
    "SWAV"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0xFEFF);
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0x0100);
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)fileSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 0x10);
    BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 1);
    "DATA"u8.CopyTo(s[0x10..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)dataBlockSize);
    s[0x18] = waveType;
    s[0x19] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], (ushort)sampleRate);
    sampleData.CopyTo(s[0x24..]);
    return buf;
  }
}
