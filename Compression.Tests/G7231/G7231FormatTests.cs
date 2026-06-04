#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.G7231;
using Compression.Registry;
using FileFormat.G7231;

namespace Compression.Tests.G7231;

/// <summary>
/// Pins the raw ITU-T G.723.1 container descriptor: it surfaces the byte-exact stream, the decoded
/// mono WAV and an 8 kHz/mono metadata block with per-type frame counts, and is read-only (G.723.1
/// has no encoder).
/// </summary>
[TestFixture]
public class G7231FormatTests {

  /// <summary>A single all-zero 6.3 kbit/s (24-byte) active frame.</summary>
  private static byte[] ZeroActive6300() {
    // info_bits=0 in the low two bits → 24-byte frame; the rest being zero is a valid (silent)
    // active frame for the decoder.
    return new byte[24];
  }

  private static byte[] Sid() {
    var f = new byte[4];
    f[0] = 0b10; // info_bits = 2 → SID
    return f;
  }

  private static byte[] Untransmitted() {
    var f = new byte[1];
    f[0] = 0b11; // info_bits = 3 → untransmitted
    return f;
  }

  private static byte[] Stream(int activeFrames) =>
    Enumerable.Range(0, activeFrames).SelectMany(_ => ZeroActive6300()).ToArray();

  [Test]
  public void List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(Stream(4));
    var entries = new G7231FormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.g723");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("g7231"));
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void IsReadOnly_HasNoCreateCapability() {
    var desc = new G7231FormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveCreatable>(), "G.723.1 has no encoder → read-only");
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
  }

  [Test]
  public void Extensions_AreG723AndG7231_NoMagic() {
    var desc = new G7231FormatDescriptor();
    Assert.That(desc.Extensions, Does.Contain(".g723"));
    Assert.That(desc.Extensions, Does.Contain(".g7231"));
    Assert.That(desc.MagicSignatures, Is.Empty, "headerless: extension-only dispatch");
  }

  [Test]
  public void Metadata_DocumentsMonoEightKilohertzAndFrameCounts() {
    var blob = Stream(3).Concat(Sid()).Concat(Untransmitted()).ToArray();
    using var input = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new G7231FormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());

    Assert.That(text, Does.Contain("sample_rate=8000"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("frames=5"));
    Assert.That(text, Does.Contain("frames_active=3"));
    Assert.That(text, Does.Contain("frames_sid=1"));
    Assert.That(text, Does.Contain("frames_untransmitted=1"));
    Assert.That(text, Does.Contain("frame_samples=240"));
  }

  [Test]
  public void ExtractEntry_Full_RoundTripsVerbatim() {
    var blob = Stream(3);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new G7231FormatDescriptor().ExtractEntry(input, "FULL.g723", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Mono_IsAValidMonoRiffWavAtEightKilohertz() {
    using var input = new MemoryStream(Stream(2));
    using var output = new MemoryStream();
    new G7231FormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000), "8 kHz");
  }

  [Test]
  public void Mono_DecodesExactlyTwoFortySamplesPerFrame() {
    var frames = 4;
    using var input = new MemoryStream(Stream(frames));
    using var output = new MemoryStream();
    new G7231FormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();
    // WAV data chunk = samples × 2 bytes; the 44-byte canonical PCM header precedes it.
    var dataBytes = wav.Length - 44;
    Assert.That(dataBytes, Is.EqualTo(frames * 240 * 2));
  }

  [Test]
  public void List_OnTruncatedTail_DropsPartialFrameGracefully() {
    var blob = Stream(2).Concat(new byte[5]).ToArray(); // 5 dangling bytes < a 24-byte frame
    using var ms = new MemoryStream(blob);
    var entries = new G7231FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.g723"), Is.True);

    using var meta = new MemoryStream();
    using var input = new MemoryStream(blob);
    new G7231FormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    Assert.That(Encoding.UTF8.GetString(meta.ToArray()), Does.Contain("frames=2"));
  }
}
