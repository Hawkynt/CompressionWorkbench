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

  // Builds an AFC-coded AST: codec 0, one BLCK with each channel's AFC frames laid out
  // back-to-back. The frames use the index-0 / exponent-0 path (header 0x00, bare sign-extended
  // nibbles) so the decoded samples are predictable by hand — the same coef-0 trick the
  // DspAdpcm/THP tests rely on.
  private static byte[] BuildAfcAst(byte[] chFrames0, byte[] chFrames1, int sampleCount, int sampleRate) {
    if (chFrames0.Length != chFrames1.Length)
      throw new ArgumentException("equal per-channel block sizes");
    var blockSize = chFrames0.Length;

    var header = new byte[0x40];
    "STRM"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 0);    // codec = AFC
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 16);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 2);   // channels
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)sampleCount);

    var block = new byte[32];
    "BLCK"u8.CopyTo(block);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)blockSize);

    return [.. header, .. block, .. chFrames0, .. chFrames1];
  }

  [Test]
  public void Descriptor_AfcCodec_DecodesPerChannel() {
    // One AFC frame (9 bytes) per channel; header 0x00 + 8 data bytes = 16 bare-nibble samples.
    var left = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x70, 0x00, 0x00, 0x00, 0x00 };
    var right = new byte[] { 0x00, 0x21, 0x43, 0x65, 0x07, 0x00, 0x00, 0x00, 0x00 };
    var blob = BuildAfcAst(left, right, sampleCount: 16, sampleRate: 32000);

    using var ms = new MemoryStream(blob);
    var entries = new AstFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ast"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);

    // Verify the decoded LEFT channel matches the hand-walked AFC nibbles (HIGH first):
    // 1,2,3,4,5,6,7,0,0,0,...
    using var ms2 = new MemoryStream(blob);
    using var wav = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms2, "LEFT.wav", wav, null);
    var parsed = new FileFormat.Wav.WavReader().Read(wav.ToArray());
    var samples = new short[parsed.InterleavedPcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(parsed.InterleavedPcm.AsSpan(i * 2));
    Assert.That(samples[..8], Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6, 7, 0 }));

    using var ms3 = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new AstFormatDescriptor().ExtractEntry(ms3, "metadata.ini", meta, null);
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
