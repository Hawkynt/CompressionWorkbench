#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Djvu;

namespace Compression.Tests.Djvu;

[TestFixture]
public class DjvuTests {

  /// <summary>
  /// Builds a minimal single-page DJVU file: AT&amp;T+FORM+size+DJVU+INFO(10 bytes)+Sjbz(1 byte).
  /// </summary>
  private static byte[] MakeSinglePage() {
    // INFO chunk body (10 bytes per spec): width(2 BE), height(2 BE), minor, major, dpi(2 LE), gamma, flags
    var info = new byte[] { 0x00, 0x64, 0x00, 0x32, 0x00, 0x18, 0x12, 0x00, 0x2C, 0x00 };
    // Sjbz payload (placeholder bit-coded mask)
    var sjbz = new byte[] { 0xAA };
    // ANTa annotation chunk
    var anta = "(bookmarks ())"u8.ToArray();

    var body = new MemoryStream();
    body.Write("DJVU"u8);
    WriteChunk(body, "INFO", info);
    WriteChunk(body, "Sjbz", sjbz);
    WriteChunk(body, "ANTa", anta);
    var bodyBytes = body.ToArray();

    return WrapTopForm(bodyBytes);
  }

  /// <summary>
  /// Builds a minimal DJVM multi-page bundle with a DIRM stub + two DJVU sub-forms.
  /// </summary>
  private static byte[] MakeMultiPage() {
    // DIRM stub — the reader only uses it for metadata count; we don't parse its body.
    var dirm = new byte[] { 0x01 };

    var info1 = new byte[] { 0x00, 0x40, 0x00, 0x30, 0x00, 0x18, 0x12, 0x00, 0x2C, 0x00 };
    var info2 = new byte[] { 0x00, 0x50, 0x00, 0x40, 0x00, 0x18, 0x12, 0x00, 0x2C, 0x00 };

    var page1 = new MemoryStream();
    page1.Write("DJVU"u8);
    WriteChunk(page1, "INFO", info1);
    WriteChunk(page1, "TXTz", "zz"u8.ToArray());

    var page2 = new MemoryStream();
    page2.Write("DJVU"u8);
    WriteChunk(page2, "INFO", info2);

    var body = new MemoryStream();
    body.Write("DJVM"u8);
    WriteChunk(body, "DIRM", dirm);
    WriteChunk(body, "FORM", page1.ToArray());
    WriteChunk(body, "FORM", page2.ToArray());
    return WrapTopForm(body.ToArray());
  }

  private static void WriteChunk(Stream ms, string id, byte[] body) {
    Span<byte> header = stackalloc byte[8];
    header[0] = (byte)id[0]; header[1] = (byte)id[1]; header[2] = (byte)id[2]; header[3] = (byte)id[3];
    BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), (uint)body.Length);
    ms.Write(header);
    ms.Write(body);
    if ((body.Length & 1) == 1) ms.WriteByte(0);
  }

  private static byte[] WrapTopForm(byte[] body) {
    // Top container: "AT&T" + "FORM" + uint32 BE size + body
    var ms = new MemoryStream();
    ms.Write("AT&T"u8);
    ms.Write("FORM"u8);
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
    ms.Write(len);
    ms.Write(body);
    return ms.ToArray();
  }

  [Test]
  public void SinglePage_ListsFullAndChunks() {
    var data = MakeSinglePage();
    using var ms = new MemoryStream(data);
    var entries = new DjvuFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.djvu"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("chunks/INFO_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("chunks/Sjbz_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("chunks/ANTa_")), Is.True);
    Assert.That(entries.Any(e => e.Name == "annotations/page_00_anno.bin"), Is.True);
  }

  [Test]
  public void SinglePage_ExtractWritesChunks() {
    var data = MakeSinglePage();
    var tmp = Path.Combine(Path.GetTempPath(), "djvu_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new DjvuFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.djvu")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form_type=DJVU"));
      Assert.That(ini, Does.Contain("total_chunks=3"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void MultiPage_ListsPages() {
    var data = MakeMultiPage();
    using var ms = new MemoryStream(data);
    var entries = new DjvuFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "pages/page_00.djvu"), Is.True);
    Assert.That(entries.Any(e => e.Name == "pages/page_01.djvu"), Is.True);
    Assert.That(entries.Any(e => e.Name == "text/page_00_text.bin"), Is.True);
  }

  [Test]
  public void MultiPage_MetadataIniHasPageCount() {
    var data = MakeMultiPage();
    var tmp = Path.Combine(Path.GetTempPath(), "djvm_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new DjvuFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form_type=DJVM"));
      Assert.That(ini, Does.Contain("page_count=2"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
