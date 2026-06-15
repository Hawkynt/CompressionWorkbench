#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Lpc10;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Lpc10;

namespace Compression.Tests.Lpc10;

/// <summary>
/// Pins the raw FS-1015 LPC-10 container descriptor: it surfaces the byte-exact stream, the
/// synthesized mono WAV and an 8 kHz/mono metadata block, and round-trips a mono 16-bit WAV
/// through analysis-encode (and a provided FULL.lpc10 verbatim) on create.
/// </summary>
[TestFixture]
public class Lpc10FormatTests {

  private static byte[] MakeLpc10Stream(int frames) => Lpc10Codec.Encode(new short[frames * 180]);

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(MakeLpc10Stream(4));
    var entries = new Lpc10FormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.lpc10");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("lpc10"));
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Metadata_DocumentsMonoEightKilohertzAndFrameCount() {
    using var ms = new MemoryStream(MakeLpc10Stream(4));
    var entries = new Lpc10FormatDescriptor().List(ms, null);

    using var meta = new MemoryStream();
    using var input = new MemoryStream(MakeLpc10Stream(4));
    new Lpc10FormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());

    Assert.That(text, Does.Contain("sample_rate=8000"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("frames=4"));
    Assert.That(text, Does.Contain("frame_bits=54"));
  }

  [Test]
  public void ExtractEntry_Full_RoundTripsVerbatim() {
    var blob = MakeLpc10Stream(3);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new Lpc10FormatDescriptor().ExtractEntry(input, "FULL.lpc10", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Mono_IsAValidMonoRiffWav() {
    using var input = new MemoryStream(MakeLpc10Stream(2));
    using var output = new MemoryStream();
    new Lpc10FormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000), "8 kHz");
  }

  [Test]
  public void Create_FromMonoWav_ProducesDecodableLpc10() {
    // A mono 16-bit WAV → analysis-encode → the result must list/decode as LPC-10.
    var pcm = new short[5 * 180];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 150 * i / 8000.0));
    var pcmBytes = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(i * 2), pcm[i]);
    var wav = PcmCodec.ToWavBlob(pcmBytes, channels: 1, sampleRate: 8000, bitsPerSample: 16);

    using var output = new MemoryStream();
    new Lpc10FormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("voice.wav", wav)],
      new FormatCreateOptions());

    var coded = output.ToArray();
    Assert.That(coded.Length, Is.EqualTo(5 * 7), "5 frames × 7 bytes");
    Assert.That(Lpc10Codec.Decode(coded).Length, Is.EqualTo(5 * 180));
  }

  [Test]
  public void Create_FromFullLpc10_PassesThroughVerbatim() {
    var blob = MakeLpc10Stream(3);
    using var output = new MemoryStream();
    new Lpc10FormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("FULL.lpc10", blob)],
      new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Create_FromStereoWav_Throws() {
    var pcmBytes = new byte[4 * 180 * 2 * 2]; // stereo
    var wav = PcmCodec.ToWavBlob(pcmBytes, channels: 2, sampleRate: 8000, bitsPerSample: 16);
    using var output = new MemoryStream();
    Assert.That(() => new Lpc10FormatDescriptor().Create(
        output, [ArchiveInputInfo.InMemory("s.wav", wav)], new FormatCreateOptions()),
      Throws.InvalidOperationException);
  }
}
