#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using FileFormat.Ast;

namespace Compression.Tests.Ast;

[TestFixture]
public class AstTests {

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
  public void Writer_Reader_RoundTripsExactly_Pcm16() {
    var left = MakeTone(20000, 50.0, 11000);
    var right = MakeTone(20000, 80.0, 9000, Math.PI / 3);

    var blob = new AstWriter().Write([left, right], 32000);

    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("STRM"u8.ToArray()));

    var parsed = new AstReader().Read(blob);
    Assert.That(parsed.Info.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Info.SampleRate, Is.EqualTo(32000));
    Assert.That(parsed.Info.Codec, Is.EqualTo(1));
    Assert.That(parsed.Info.SampleCount, Is.EqualTo(20000));

    // PCM16 is lossless: exact round-trip.
    Assert.That(parsed.Pcm[0], Is.EqualTo(left));
    Assert.That(parsed.Pcm[1], Is.EqualTo(right));
  }

  [Test]
  public void Writer_MultiBlock_RoundTripsExactly() {
    // 0x4EC0 bytes/channel = 10080 samples/block; force multiple blocks.
    var mono = MakeTone(30000, 55, 11000);
    var blob = new AstWriter().Write([mono], 32000);
    var parsed = new AstReader().Read(blob);
    Assert.That(parsed.Pcm[0], Is.EqualTo(mono));
  }

  [Test]
  public void Descriptor_ListsFullAndPerChannelAndMetadata() {
    var blob = new AstWriter().Write([MakeTone(5000, 40, 10000), MakeTone(5000, 60, 8000)], 22050);
    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ast" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_Metadata_DescribesStream() {
    var blob = new AstWriter().Write([MakeTone(1000, 25, 8000)], 48000);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(ini, Does.Contain("sampleRate=48000"));
    Assert.That(ini, Does.Contain("codec=PCM16BE"));
  }

  [Test]
  public void Descriptor_AfcCodec_FallsBackToFullOnly_WithNote() {
    // Hand-craft an AST header with codec 0 (AFC) and no usable blocks.
    var blob = new byte[0x40];
    "STRM"u8.CopyTo(blob);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(8), 0);    // codec = AFC
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(10), 16);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(12), 2);   // channels
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(16), 32000);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(20), 1000);

    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ast"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False); // AFC not decoded

    using var ms2 = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms2, "metadata.ini", meta, null);
    Assert.That(Encoding.UTF8.GetString(meta.ToArray()), Does.Contain("AFC"));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "STRM"u8.ToArray().Concat(new byte[4]).ToArray(); // too short for header
    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ast"));
  }

  [Test]
  public void Create_FromPerChannelWavs_RoundTripsExactly() {
    var left = MakeTone(9000, 45, 10000);
    var right = MakeTone(9000, 70, 9000, 1.1);

    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("LEFT.wav", MonoWav(left, 24000)),
      Compression.Registry.ArchiveInputInfo.InMemory("RIGHT.wav", MonoWav(right, 24000)),
    };

    using var output = new MemoryStream();
    new AstFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    var parsed = new AstReader().Read(output.ToArray());
    Assert.That(parsed.Pcm[0], Is.EqualTo(left));
    Assert.That(parsed.Pcm[1], Is.EqualTo(right));
  }

  [Test]
  public void Create_FullPassthrough_ReturnsVerbatim() {
    var original = new AstWriter().Write([MakeTone(2000, 30, 8000)], 16000);
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("FULL.ast", original),
    };
    using var output = new MemoryStream();
    new AstFormatDescriptor().Create(output, inputs, new Compression.Registry.FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
