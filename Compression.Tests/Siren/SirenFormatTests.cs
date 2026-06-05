#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Siren;

namespace Compression.Tests.Siren;

/// <summary>
/// Pins the raw Siren7 / G.722.1 container descriptor: it surfaces the byte-exact stream, the decoded
/// mono WAV and a 16 kHz/mono metadata block, dispatches extension-only (no magic), and is read-only
/// (G.722.1 has no encoder).
/// </summary>
[TestFixture]
public class SirenFormatTests {

  private static byte[] Stream(int frames) => new byte[frames * SirenFormatDescriptor.DefaultFrameBytes];

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(Stream(3));
    var entries = new SirenFormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.g7221");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("siren"));
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Extensions_AreSirAndG7221_NoMagic() {
    var desc = new SirenFormatDescriptor();
    Assert.That(desc.Extensions, Does.Contain(".sir"));
    Assert.That(desc.Extensions, Does.Contain(".g7221"));
    Assert.That(desc.MagicSignatures, Is.Empty, "headerless: extension-only dispatch");
  }

  [Test]
  public void IsReadOnly_HasNoCreateCapability() {
    Assert.That(new SirenFormatDescriptor(), Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test]
  public void ExtractEntry_Full_RoundTripsVerbatim() {
    var blob = Stream(2);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SirenFormatDescriptor().ExtractEntry(input, "FULL.g7221", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Mono_IsValidMonoRiffWavAtSixteenKilohertz() {
    using var input = new MemoryStream(Stream(2));
    using var output = new MemoryStream();
    new SirenFormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000), "16 kHz");
  }

  [Test]
  public void Mono_DecodesThreeTwentySamplesPerFrame() {
    using var input = new MemoryStream(Stream(4));
    using var output = new MemoryStream();
    new SirenFormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var dataBytes = output.ToArray().Length - 44;
    Assert.That(dataBytes, Is.EqualTo(4 * 320 * 2));
  }

  [Test]
  public void Metadata_DocumentsSirenSevenScopeAndCounts() {
    using var input = new MemoryStream(Stream(3));
    using var meta = new MemoryStream();
    new SirenFormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("sample_rate=16000"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("regions=14"));
    Assert.That(text, Does.Contain("frames=3"));
    Assert.That(text, Does.Contain("Siren14"), "documents the unsupported 32 kHz Annex C scope");
  }
}
