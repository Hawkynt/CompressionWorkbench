#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Dcx;

/// <summary>
/// Behaviour of <see cref="DcxFormatDescriptor"/> as a page-structured
/// pseudo-archive: FULL.dcx + metadata.ini + one PCX per page. Uses a synthetic
/// DCX (0x3ADE68B1 magic + page-offset table) with two stub PCX payloads.
/// </summary>
[TestFixture]
public class DcxPseudoArchiveTests {

  private static byte[] BuildDcx() {
    var page0 = Encoding.ASCII.GetBytes("PCX-PAGE-ZERO-PAYLOAD");
    var page1 = Encoding.ASCII.GetBytes("PCX-PAGE-ONE-PAYLOAD");

    // Header: magic + 2 offsets + terminating 0. Table region = 4 + 3*4 = 16 bytes.
    var headerLen = 4 + 3 * 4;
    var page0Off = headerLen;
    var page1Off = page0Off + page0.Length;
    var total = page1Off + page1.Length;

    var buf = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x3ADE68B1);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)page0Off);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)page1Off);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), 0); // terminator
    page0.CopyTo(buf.AsSpan(page0Off));
    page1.CopyTo(buf.AsSpan(page1Off));
    return buf;
  }

  [Test]
  public void List_Exposes_Full_Metadata_And_Pages() {
    var desc = new DcxFormatDescriptor();
    using var s = new MemoryStream(BuildDcx());
    var entries = desc.List(s, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("FULL.dcx"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("pages/page_000.pcx"));
      Assert.That(names, Does.Contain("pages/page_001.pcx"));
    });
    Assert.That(entries.First(e => e.Name == "pages/page_000.pcx").Kind, Is.EqualTo("Frame"));
  }

  [Test]
  public void Extract_Full_ByteIdentical_And_Pages_Carry_Payloads() {
    var original = BuildDcx();
    var desc = new DcxFormatDescriptor();
    using var s = new MemoryStream(original);
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_dcx_{Guid.NewGuid():N}");
    try {
      desc.Extract(s, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "FULL.dcx")), Is.EqualTo(original));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pages", "page_000.pcx")),
        Is.EqualTo("PCX-PAGE-ZERO-PAYLOAD"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pages", "page_001.pcx")),
        Is.EqualTo("PCX-PAGE-ONE-PAYLOAD"));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void List_DoesNotThrow_On_Malformed() {
    var desc = new DcxFormatDescriptor();
    using var s = new MemoryStream([0xB1, 0x68, 0xDE, 0x3A, 0xFF]); // magic only, truncated table
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = desc.List(s, null));
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.dcx"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
