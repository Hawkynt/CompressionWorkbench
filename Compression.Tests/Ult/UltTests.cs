#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ult;

namespace Compression.Tests.Ult;

[TestFixture]
public class UltTests {

  private const int SampleHeaderSize = 64;

  private static byte[] SampleHeader(string name, uint sizeStart, uint sizeEnd, bool is16) {
    var h = new byte[SampleHeaderSize];
    Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(52), sizeStart);  // sizeStart
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(56), sizeEnd);    // sizeEnd
    h[60] = 64;                                                          // volume
    h[61] = (byte)(is16 ? 0x04 : 0x00);                                  // flags bit2 = 16-bit
    return h;
  }

  // Sample 1: 8-bit, 4 samples. Sample 2: 16-bit, 2 samples (4 bytes).
  private static byte[] MakeUlt(out byte[] expected8, out byte[] expected16) {
    var s1Signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expected8 = new byte[] { 128, 138, 118, 255 };
    var s2 = new byte[] { 0x01, 0x02, 0x03, 0x04 }; // 16-bit LE: passed through as-is
    expected16 = s2;

    using var ms = new MemoryStream();
    ms.Write(Encoding.ASCII.GetBytes("MAS_UTrack_V004")); // 15 bytes magic+version
    ms.Write(new byte[32]);                                // title
    ms.WriteByte(0);                                       // message lines = 0
    ms.WriteByte(2);                                       // numSamples
    ms.Write(SampleHeader("Bass", 0, 4, is16: false));     // 4 samples → 4 bytes
    ms.Write(SampleHeader("Pad", 0, 2, is16: true));       // 2 samples → 4 bytes
    // Trailing sample data, in descriptor order.
    ms.Write(s1Signed);
    ms.Write(s2);
    return ms.ToArray();
  }

  [Test]
  public void List_SurfacesFullMetadataAndTwoSamples() {
    var blob = MakeUlt(out _, out _);
    using var ms = new MemoryStream(blob);
    var entries = new UltFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.ult").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Count(e => e.Kind == "Sample"), Is.EqualTo(2));
    Assert.That(entries.Any(e => e.Name == "samples/01_Bass.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/02_Pad.wav"), Is.True);
  }

  [Test]
  public void EightBitSample_RebiasedToUnsigned8() {
    var blob = MakeUlt(out var expected, out _);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new UltFormatDescriptor().ExtractEntry(ms, "samples/01_Bass.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void SixteenBitSample_PassedThroughAsSigned16() {
    var blob = MakeUlt(out _, out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new UltFormatDescriptor().ExtractEntry(ms, "samples/02_Pad.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    using var ms = new MemoryStream("MAS_UTrack_V004"u8.ToArray()); // header only, no sample table
    var entries = new UltFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ult"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
