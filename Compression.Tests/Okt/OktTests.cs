#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Okt;

namespace Compression.Tests.Okt;

[TestFixture]
public class OktTests {

  private static byte[] Chunk(string id, byte[] body) {
    var r = new byte[8 + body.Length];
    Encoding.ASCII.GetBytes(id).CopyTo(r, 0);
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)body.Length);
    body.CopyTo(r, 8);
    return r;
  }

  private static byte[] SampDescriptor(string name, uint length, byte volume) {
    var d = new byte[36];
    Encoding.ASCII.GetBytes(name).CopyTo(d, 0);
    BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), length);
    d[28] = volume;
    return d;
  }

  private static byte[] MakeOkt(out byte[] expectedPcm) {
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expectedPcm = new byte[] { 128, 138, 118, 255 };

    using var ms = new MemoryStream();
    ms.Write("OKTASONG"u8);
    // Two descriptors: one with data (len 4), one empty (len 0).
    var samp = new byte[72];
    SampDescriptor("Lead", 4, 64).CopyTo(samp, 0);
    SampDescriptor("Empty", 0, 0).CopyTo(samp, 36);
    ms.Write(Chunk("SAMP", samp));
    ms.Write(Chunk("PATT", new byte[] { 1 }));
    ms.Write(Chunk("PBOD", new byte[] { 0xAA, 0xBB, 0xCC }));
    ms.Write(Chunk("SBOD", signed)); // belongs to descriptor 0 ("Lead")
    return ms.ToArray();
  }

  [Test]
  public void List_SurfacesFullMetadataPatternAndSample() {
    var blob = MakeOkt(out _);
    using var ms = new MemoryStream(blob);
    var entries = new OktFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.okt").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin" && e.Kind == "Pattern"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/01_Lead.wav" && e.Kind == "Sample"), Is.True);
  }

  [Test]
  public void Sample_DecodesToRebiasedUnsigned8Wav() {
    var blob = MakeOkt(out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new OktFormatDescriptor().ExtractEntry(ms, "samples/01_Lead.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8363u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Pattern_PreservesRawBytes() {
    var blob = MakeOkt(out _);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new OktFormatDescriptor().ExtractEntry(ms, "patterns/pattern_00.bin", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC }));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    using var ms = new MemoryStream("OKTASONG"u8.ToArray());
    var entries = new OktFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.okt"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
