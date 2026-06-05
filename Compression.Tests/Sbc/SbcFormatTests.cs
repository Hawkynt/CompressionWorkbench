#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Sbc;

namespace Compression.Tests.Sbc;

/// <summary>
/// Pins the raw SBC/mSBC container descriptor: it surfaces the byte-exact stream, decoded per-channel
/// WAVs and a metadata block read from the first frame, declares the low-confidence <c>0x9C</c>
/// magic plus the <c>.sbc</c>/<c>.msbc</c> extensions, and is read-only (no SBC encoder).
/// </summary>
[TestFixture]
public class SbcFormatTests {

  private static byte[] SilentMsbcFrame() {
    var frame = new byte[57];
    frame[0] = 0xAD; // mSBC syncword
    frame[3] = 197;  // ff_sbc_crc8 over the zero header + scale factors
    return frame;
  }

  private static byte[] Stream(int frames) =>
    Enumerable.Range(0, frames).SelectMany(_ => SilentMsbcFrame()).ToArray();

  [Test]
  public void List_SurfacesFullChannelAndMetadata() {
    using var ms = new MemoryStream(Stream(3));
    var entries = new SbcFormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.sbc");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    Assert.That(full.Method, Is.EqualTo("sbc"));
    Assert.That(entries.Any(e => e.Kind == "Channel" && e.Name.EndsWith(".wav")), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Magic_IsLowConfidenceSyncword() {
    var desc = new SbcFormatDescriptor();
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    var sig = desc.MagicSignatures[0];
    Assert.That(sig.Bytes, Is.EqualTo(new byte[] { 0x9C }));
    Assert.That(sig.Confidence, Is.LessThan(0.5), "single syncword byte is a weak signal");
  }

  [Test]
  public void Extensions_AreSbcAndMsbc() {
    var desc = new SbcFormatDescriptor();
    Assert.That(desc.Extensions, Does.Contain(".sbc"));
    Assert.That(desc.Extensions, Does.Contain(".msbc"));
  }

  [Test]
  public void IsReadOnly_HasNoCreateCapability() {
    var desc = new SbcFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveCreatable>(), "SBC has no encoder → read-only");
  }

  [Test]
  public void ExtractEntry_Full_RoundTripsVerbatim() {
    var blob = Stream(2);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SbcFormatDescriptor().ExtractEntry(input, "FULL.sbc", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Channel_IsValidMonoRiffWavAtSixteenKilohertz() {
    using var ms = new MemoryStream(Stream(2));
    var entries = new SbcFormatDescriptor().List(ms, null);
    var channel = entries.First(e => e.Kind == "Channel");

    using var input = new MemoryStream(Stream(2));
    using var output = new MemoryStream();
    new SbcFormatDescriptor().ExtractEntry(input, channel.Name, output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000), "16 kHz");
  }

  [Test]
  public void Metadata_DocumentsFirstFrameParameters() {
    using var input = new MemoryStream(Stream(5));
    using var meta = new MemoryStream();
    new SbcFormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("variant=mSBC"));
    Assert.That(text, Does.Contain("sample_rate=16000"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("frames=5"));
    Assert.That(text, Does.Contain("decoded=true"));
  }
}
