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

  /// <summary>
  /// The container's chunks are read by the image package now, so what Workbench
  /// still owns is what it says about them.
  /// </summary>
  [Test]
  public void MetadataNamesTheStillCodec() {
    var data = MakeMinimalWebp();
    using var ms = new MemoryStream(data);
    using var meta = new MemoryStream();
    new WebpFormatDescriptor().ExtractEntry(ms, "metadata.ini", meta, null);

    var text = System.Text.Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("parse_status=ok"));
    Assert.That(text, Does.Contain("codec=VP8L (lossless)"));
  }

  [Test]
  public void DescriptorAlwaysHasFullEntry() {
    var data = MakeMinimalWebp();
    using var ms = new MemoryStream(data);
    var entries = new WebpFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.webp"), Is.True);
  }

  /// <summary>
  /// Something that is not a RIFF/WebP is reported rather than thrown at the caller:
  /// the full blob is still offered and the metadata says why it could not be read.
  /// </summary>
  [Test]
  public void SomethingThatIsNotAWebp_IsReportedAsPartial() {
    using var ms = new MemoryStream(new byte[16]);
    var entries = new WebpFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.webp"), Is.True);

    ms.Position = 0;
    using var meta = new MemoryStream();
    new WebpFormatDescriptor().ExtractEntry(ms, "metadata.ini", meta, null);
    Assert.That(System.Text.Encoding.UTF8.GetString(meta.ToArray()),
      Does.Contain("reason=not_a_valid_riff_webp"));
  }
}
