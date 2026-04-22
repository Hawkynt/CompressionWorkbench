#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Webp;

namespace Compression.Tests.Webp;

[TestFixture]
public class WebpTests {

  private static byte[] MakeMinimalWebp() {
    // RIFF [total] WEBP VP8L [9] <9 bytes of body>
    using var ms = new MemoryStream();
    ms.Write("RIFF"u8);
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, 4 + 8 + 10);  // WEBP (4) + chunk header (8) + body (10)
    ms.Write(sz);
    ms.Write("WEBP"u8);
    ms.Write("VP8L"u8);
    BinaryPrimitives.WriteUInt32LittleEndian(sz, 10);
    ms.Write(sz);
    ms.Write(new byte[10]);
    return ms.ToArray();
  }

  [Test]
  public void ReaderFindsVp8lChunk() {
    var data = MakeMinimalWebp();
    var reader = new WebpReader(data);
    Assert.That(reader.Chunks.Any(c => c.FourCc == "VP8L"), Is.True);
  }

  [Test]
  public void DescriptorAlwaysHasFullEntry() {
    var data = MakeMinimalWebp();
    using var ms = new MemoryStream(data);
    var entries = new WebpFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.webp"), Is.True);
  }

  [Test]
  public void BadHeader_Throws() {
    // Need ≥12 bytes to reach the RIFF+WEBP magic check — anything shorter returns an empty chunk list.
    Assert.Throws<InvalidDataException>(() => _ = new WebpReader(new byte[16]));
  }
}
