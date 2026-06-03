#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Its;

namespace Compression.Tests.Its;

[TestFixture]
public class ItsTests {

  // Builds an 80-byte IMPS header. flags bit1 = 16-bit, bit3 = compressed; cvt bit0 = signed.
  internal static byte[] ImpsHeader(string name, int lengthSamples, int c5speed,
      int samplePointer, bool is16, bool compressed, bool signed) {
    var h = new byte[ItsSampleDecoder.HeaderSize];
    "IMPS"u8.ToArray().CopyTo(h, 0);
    Encoding.ASCII.GetBytes("DOS.SMP").CopyTo(h, 4);
    byte flags = 0x01;                  // bit0 = has sample
    if (is16) flags |= 0x02;
    if (compressed) flags |= 0x08;
    h[18] = flags;
    h[19] = 64;                          // default volume
    Encoding.ASCII.GetBytes(name).CopyTo(h, 20);
    h[46] = (byte)(signed ? 0x01 : 0x00); // cvt
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(48), (uint)lengthSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(60), (uint)c5speed);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(72), (uint)samplePointer);
    return h;
  }

  private static byte[] MakeIts(out byte[] expectedPcm) {
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expectedPcm = new byte[] { 128, 138, 118, 255 };

    var ptr = ItsSampleDecoder.HeaderSize;
    var hdr = ImpsHeader("Snare", signed.Length, c5speed: 22050,
      samplePointer: ptr, is16: false, compressed: false, signed: true);

    var buf = new byte[ptr + signed.Length];
    hdr.CopyTo(buf, 0);
    signed.CopyTo(buf, ptr);
    return buf;
  }

  [Test]
  public void List_SurfacesFullMetadataAndOneSample() {
    var blob = MakeIts(out _);
    using var ms = new MemoryStream(blob);
    var entries = new ItsFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.its").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/01_Snare.wav" && e.Kind == "Sample"), Is.True);
  }

  [Test]
  public void Sample_DecodesAtC5SpeedWithRebiasedSamples() {
    var blob = MakeIts(out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new ItsFormatDescriptor().ExtractEntry(ms, "samples/01_Snare.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Compressed_FallsBackToFullOnly() {
    var signed = new byte[] { 1, 2, 3, 4 };
    var ptr = ItsSampleDecoder.HeaderSize;
    var hdr = ImpsHeader("Comp", signed.Length, 8000, ptr, is16: false, compressed: true, signed: true);
    var buf = new byte[ptr + signed.Length];
    hdr.CopyTo(buf, 0);
    signed.CopyTo(buf, ptr);

    using var ms = new MemoryStream(buf);
    var entries = new ItsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.its"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    using var ms = new MemoryStream("IMP"u8.ToArray());
    var entries = new ItsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.its"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
