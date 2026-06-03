#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Sphere;

namespace Compression.Tests.Sphere;

[TestFixture]
public class SphereTests {

  private const int HeaderSize = 1024;

  // Builds a SPHERE file with the given header fields and raw sample bytes.
  private static byte[] MakeSphere(IReadOnlyList<(string Name, string Type, string Value)> fields, byte[] samples) {
    var sb = new StringBuilder();
    sb.Append("NIST_1A\n");
    sb.Append("   1024\n");
    foreach (var (name, type, value) in fields)
      sb.Append($"{name} -{type} {value}\n");
    sb.Append("end_head\n");
    var headerText = sb.ToString();
    if (headerText.Length > HeaderSize)
      throw new InvalidOperationException("Test header overflows 1024 bytes.");
    var blob = new byte[HeaderSize + samples.Length];
    Encoding.ASCII.GetBytes(headerText).CopyTo(blob, 0);
    samples.CopyTo(blob, HeaderSize);
    return blob;
  }

  // 16-bit stereo little-endian PCM, 8 frames.
  private static byte[] MakeStereoPcm() {
    var samples = new byte[8 * 2 * 2];
    for (var i = 0; i < 8; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 4), (short)(i * 200));
      BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 4 + 2), (short)(i * -200));
    }
    return MakeSphere([
      ("channel_count", "i", "2"),
      ("sample_rate", "i", "16000"),
      ("sample_n_bytes", "i", "2"),
      ("sample_byte_format", "s2", "01"),
      ("sample_coding", "s3", "pcm"),
    ], samples);
  }

  [Test]
  public void List_StereoPcm_SurfacesFullAndChannels() {
    using var ms = new MemoryStream(MakeStereoPcm());
    var entries = new SphereFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sph" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void ExtractEntry_Channel_IsValidMonoRiff() {
    using var ms = new MemoryStream(MakeStereoPcm());
    using var output = new MemoryStream();
    new SphereFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var bytes = output.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)), Is.EqualTo(16000u));
  }

  [Test]
  public void Metadata_CarriesAllHeaderFields() {
    using var ms = new MemoryStream(MakeStereoPcm());
    using var output = new MemoryStream();
    new SphereFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("channel_count=2"));
    Assert.That(text, Does.Contain("sample_rate=16000"));
    Assert.That(text, Does.Contain("sample_coding=pcm"));
  }

  [Test]
  public void Ulaw_IsDecodedViaMuLaw() {
    // Mono μ-law: one byte per sample; assert decoded WAV holds the MuLaw 16-bit values.
    var muSamples = new byte[] { 0xFF, 0x00, 0x80, 0x7F };
    var blob = MakeSphere([
      ("channel_count", "i", "1"),
      ("sample_rate", "i", "8000"),
      ("sample_n_bytes", "i", "1"),
      ("sample_coding", "s4", "ulaw"),
    ], muSamples);

    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SphereFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16)); // decoded to 16-bit
    var pcm = wav.AsSpan(44);
    for (var i = 0; i < muSamples.Length; ++i) {
      var expected = Codec.MuLaw.MuLawCodec.DecodeSample(muSamples[i]);
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]), Is.EqualTo(expected));
    }
  }

  [Test]
  public void BigEndianPcm_IsByteSwappedToLittleEndian() {
    // Mono 16-bit big-endian PCM; the extracted WAV must hold little-endian samples.
    var values = new short[] { 0x0102, 0x0A0B, -100, 12345 };
    var be = new byte[values.Length * 2];
    for (var i = 0; i < values.Length; ++i)
      BinaryPrimitives.WriteInt16BigEndian(be.AsSpan(i * 2), values[i]);
    var blob = MakeSphere([
      ("channel_count", "i", "1"),
      ("sample_rate", "i", "16000"),
      ("sample_n_bytes", "i", "2"),
      ("sample_byte_format", "s2", "10"),
      ("sample_coding", "s3", "pcm"),
    ], be);

    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SphereFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var pcm = output.ToArray().AsSpan(44);
    for (var i = 0; i < values.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]), Is.EqualTo(values[i]));
  }

  [Test]
  public void EmbeddedShorten_IsFullOnly() {
    var blob = MakeSphere([
      ("channel_count", "i", "1"),
      ("sample_rate", "i", "16000"),
      ("sample_n_bytes", "i", "2"),
      ("sample_byte_format", "s2", "01"),
      ("sample_coding", "s21", "pcm,embedded-shorten-v2.00"),
    ], new byte[16]);

    using var ms = new MemoryStream(blob);
    var entries = new SphereFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sph"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Create_RoundTripsThroughReader() {
    // Two mono 16-bit WAVs → SPHERE → read back, channels recovered.
    var left = new byte[6 * 2];
    var right = new byte[6 * 2];
    for (var i = 0; i < 6; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i * 111));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(i * -222));
    }
    var leftWav = PcmCodec.ToWavBlob(left, 1, 22050, 16);
    var rightWav = PcmCodec.ToWavBlob(right, 1, 22050, 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav),
    };

    using var created = new MemoryStream();
    new SphereFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    var blob = created.ToArray();
    using var read = new MemoryStream(blob);
    var entries = new SphereFormatDescriptor().List(read, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    using var read2 = new MemoryStream(blob);
    using var outLeft = new MemoryStream();
    new SphereFormatDescriptor().ExtractEntry(read2, "LEFT.wav", outLeft, null);
    Assert.That(outLeft.ToArray().AsSpan(44).ToArray(), Is.EqualTo(left));
  }
}
