#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Iti;
using FileFormat.Its;

namespace Compression.Tests.Iti;

[TestFixture]
public class ItiTests {

  private static byte[] ImpsHeader(string name, int lengthSamples, int c5speed,
      int samplePointer, bool is16, bool compressed, bool signed) {
    var h = new byte[ItsSampleDecoder.HeaderSize];
    "IMPS"u8.ToArray().CopyTo(h, 0);
    byte flags = 0x01;
    if (is16) flags |= 0x02;
    if (compressed) flags |= 0x08;
    h[18] = flags;
    h[19] = 64;
    Encoding.ASCII.GetBytes(name).CopyTo(h, 20);
    h[46] = (byte)(signed ? 0x01 : 0x00);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(48), (uint)lengthSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(60), (uint)c5speed);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(72), (uint)samplePointer);
    return h;
  }

  // IMPI header (554 bytes, instrument name at offset 32) then one embedded IMPS + its data.
  private static byte[] MakeIti(out byte[] expectedPcm) {
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expectedPcm = new byte[] { 128, 138, 118, 255 };

    const int impiSize = 554;
    var sampleHdrOff = impiSize;
    var dataOff = sampleHdrOff + ItsSampleDecoder.HeaderSize;

    var buf = new byte[dataOff + signed.Length];
    "IMPI"u8.ToArray().CopyTo(buf, 0);
    Encoding.ASCII.GetBytes("PianoInstr").CopyTo(buf, 32);

    ImpsHeader("Note", signed.Length, c5speed: 16000, samplePointer: dataOff,
      is16: false, compressed: false, signed: true).CopyTo(buf, sampleHdrOff);
    signed.CopyTo(buf, dataOff);
    return buf;
  }

  [Test]
  public void List_SurfacesFullMetadataAndEmbeddedSample() {
    var blob = MakeIti(out _);
    using var ms = new MemoryStream(blob);
    var entries = new ItiFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.iti").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/01_Note.wav" && e.Kind == "Sample"), Is.True);

    using var meta = new MemoryStream();
    new ItiFormatDescriptor().ExtractEntry(new MemoryStream(blob), "metadata.ini", meta, null);
    Assert.That(Encoding.UTF8.GetString(meta.ToArray()), Does.Contain("instrument_name=PianoInstr"));
  }

  [Test]
  public void EmbeddedSample_DecodesAtC5Speed() {
    var blob = MakeIti(out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new ItiFormatDescriptor().ExtractEntry(ms, "samples/01_Note.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000u));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    using var ms = new MemoryStream("IMPI"u8.ToArray());
    var entries = new ItiFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.iti"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
