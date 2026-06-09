#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.AmrNb;
using Codec.AmrWb;
using Compression.Registry;
using FileFormat.Amr;

namespace Compression.Tests.Amr;

/// <summary>
/// Pins the 3GPP AMR container descriptor: magic detection for NB/WB/multi-channel, frame-walk
/// counts, valid RIFF MONO/per-channel WAVs at the right rate, metadata, graceful handling of
/// malformed input and the read-only contract.
/// </summary>
[TestFixture]
public class AmrFormatTests {

  private static readonly byte[] MagicNb = "#!AMR\n"u8.ToArray();
  private static readonly byte[] MagicWb = "#!AMR-WB\n"u8.ToArray();
  private static readonly byte[] MagicNbMc = "#!AMR_MC1.0\n"u8.ToArray();

  private static byte[] NbFrame(int frameType) {
    var f = new byte[1 + AmrNbCodec.PayloadBytes(frameType)];
    f[0] = (byte)((frameType << 3) | 0x04);
    return f;
  }

  private static byte[] WbFrame(int frameType) {
    var f = new byte[AmrWbCodec.FrameBytes(frameType)];
    f[0] = (byte)((frameType << 3) | 0x04);
    return f;
  }

  private static byte[] NbFile(int frames) {
    using var ms = new MemoryStream();
    ms.Write(MagicNb);
    for (var i = 0; i < frames; i++)
      ms.Write(NbFrame(7));
    return ms.ToArray();
  }

  private static byte[] WbFile(int frames) {
    using var ms = new MemoryStream();
    ms.Write(MagicWb);
    for (var i = 0; i < frames; i++)
      ms.Write(WbFrame(2));
    return ms.ToArray();
  }

  [Test]
  public void Nb_List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(NbFile(3));
    var entries = new AmrFormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.amr");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Wb_List_UsesAwbFullNameAndMonoWav() {
    using var ms = new MemoryStream(WbFile(2));
    var entries = new AmrFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.awb" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
  }

  [Test]
  public void Nb_MonoWav_IsValidRiffAt8kHz() {
    AssertWav(NbFile(2), "MONO.wav", 8000);
  }

  [Test]
  public void Wb_MonoWav_IsValidRiffAt16kHz() {
    AssertWav(WbFile(2), "MONO.wav", 16000);
  }

  [Test]
  public void MultiChannel_Nb_SplitsIntoPerChannelWavs() {
    // #!AMR_MC1.0\n + channel count (2), then interleaved NB frames (4 → 2 per channel)
    using var ms = new MemoryStream();
    ms.Write(MagicNbMc);
    var ch = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(ch, 2);
    ms.Write(ch);
    for (var i = 0; i < 4; i++)
      ms.Write(NbFrame(7));
    var blob = ms.ToArray();

    var entries = new AmrFormatDescriptor().List(new MemoryStream(blob), null);
    var wavs = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(wavs.Count, Is.EqualTo(2), "two channels → two WAVs");
  }

  [Test]
  public void Metadata_ReportsCodecAndFrameCount() {
    using var ms = new MemoryStream(NbFile(5));
    var output = new MemoryStream();
    new AmrFormatDescriptor().ExtractEntry(new MemoryStream(NbFile(5)), "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("codec=AMR-NB"));
    Assert.That(text, Does.Contain("frames_total=5"));
    Assert.That(text, Does.Contain("sample_rate=8000"));
    Assert.That(text, Does.Contain("note=decode-only"));
  }

  [Test]
  public void Malformed_UnknownHeader_SurfacesFullAndNote() {
    var blob = "#!AMR??garbage"u8.ToArray();
    var entries = new AmrFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name.StartsWith("FULL.")), Is.True);
    var output = new MemoryStream();
    new AmrFormatDescriptor().ExtractEntry(new MemoryStream(blob), "metadata.ini", output, null);
    Assert.That(Encoding.UTF8.GetString(output.ToArray()), Does.Contain("unrecognized header"));
  }

  [Test]
  public void IsReadOnly_HasNoCreateCapability() {
    var desc = new AmrFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveCreatable>(), "AMR has no encoder → read-only");
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(desc.MagicSignatures.Count, Is.GreaterThan(0));
  }

  [Test]
  public void Extensions_IncludeAmrAndAwb() {
    var desc = new AmrFormatDescriptor();
    Assert.That(desc.Extensions, Does.Contain(".amr"));
    Assert.That(desc.Extensions, Does.Contain(".awb"));
  }

  private static void AssertWav(byte[] amrFile, string entryName, int expectedRate) {
    var output = new MemoryStream();
    new AmrFormatDescriptor().ExtractEntry(new MemoryStream(amrFile), entryName, output, null);
    var wav = output.ToArray();
    Assert.That(wav.Length, Is.GreaterThan(44), "WAV must have a header + data");
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
    var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4));
    Assert.That(sampleRate, Is.EqualTo(expectedRate));
  }
}
