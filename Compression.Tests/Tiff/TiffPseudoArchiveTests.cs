#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Tiff;

/// <summary>
/// Behaviour of <see cref="TiffFormatDescriptor"/> as a page-structured
/// pseudo-archive: FULL.tif + metadata.ini + one self-contained single-page TIFF
/// per IFD, with strip data re-based into each emitted page. Uses a synthetic
/// minimal 2-page little-endian TIFF.
/// </summary>
[TestFixture]
public class TiffPseudoArchiveTests {

  // ── Synthetic sample: 2 IFDs, each with one strip of distinct data ──────────

  private static byte[] BuildTwoPageTiff() {
    // Layout:
    //  [0..8)   header
    //  [8..]    IFD0 (4 entries) -> next IFD1
    //  ...      IFD1 (4 entries) -> 0
    //  strip0 / strip1 data appended at end
    var strip0 = Encoding.ASCII.GetBytes("PAGE-ZERO-STRIP-DATA");
    var strip1 = Encoding.ASCII.GetBytes("PAGE-ONE-STRIP");

    const int entriesPerIfd = 4;
    var ifdSize = 2 + entriesPerIfd * 12 + 4;
    var ifd0Off = 8;
    var ifd1Off = ifd0Off + ifdSize;
    var strip0Off = ifd1Off + ifdSize;
    var strip1Off = strip0Off + strip0.Length;
    var total = strip1Off + strip1.Length;

    var buf = new byte[total];
    buf[0] = (byte)'I'; buf[1] = (byte)'I';
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 0x002A);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)ifd0Off);

    WriteIfd(buf, ifd0Off, width: 4, height: 1, stripOff: (uint)strip0Off, stripLen: (uint)strip0.Length, nextIfd: (uint)ifd1Off);
    WriteIfd(buf, ifd1Off, width: 2, height: 1, stripOff: (uint)strip1Off, stripLen: (uint)strip1.Length, nextIfd: 0);

    strip0.CopyTo(buf.AsSpan(strip0Off));
    strip1.CopyTo(buf.AsSpan(strip1Off));
    return buf;
  }

  private static void WriteIfd(byte[] buf, int off, ushort width, ushort height,
                               uint stripOff, uint stripLen, uint nextIfd) {
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), 4); // entry count
    var e = off + 2;
    WriteEntry(buf, e + 0 * 12, 0x0100, 3, 1, width);          // ImageWidth (SHORT)
    WriteEntry(buf, e + 1 * 12, 0x0101, 3, 1, height);         // ImageLength (SHORT)
    WriteEntry(buf, e + 2 * 12, 0x0111, 4, 1, stripOff);       // StripOffsets (LONG)
    WriteEntry(buf, e + 3 * 12, 0x0117, 4, 1, stripLen);       // StripByteCounts (LONG)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 2 + 4 * 12), nextIfd);
  }

  private static void WriteEntry(byte[] buf, int off, ushort tag, ushort type, uint count, uint value) {
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), tag);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), type);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4), count);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 8), value); // inline value (works for SHORT/LONG, count 1)
  }

  // ── List ────────────────────────────────────────────────────────────────────

  [Test]
  public void List_Exposes_Full_Metadata_And_Pages() {
    var desc = new TiffFormatDescriptor();
    using var s = new MemoryStream(BuildTwoPageTiff());
    var entries = desc.List(s, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("FULL.tif"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("pages/page_000.tif"));
      Assert.That(names, Does.Contain("pages/page_001.tif"));
    });
    Assert.That(entries.First(e => e.Name == "pages/page_000.tif").Kind, Is.EqualTo("Frame"));
  }

  // ── Extract ──────────────────────────────────────────────────────────────────

  [Test]
  public void Extract_Full_ByteIdentical_And_Pages_Are_Valid_SinglePage_Tiffs() {
    var original = BuildTwoPageTiff();
    var desc = new TiffFormatDescriptor();
    using var s = new MemoryStream(original);
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_tiff_{Guid.NewGuid():N}");
    try {
      desc.Extract(s, outDir, null, null);
      var full = File.ReadAllBytes(Path.Combine(outDir, "FULL.tif"));
      Assert.That(full, Is.EqualTo(original), "FULL.tif must be byte-identical.");

      var page0 = File.ReadAllBytes(Path.Combine(outDir, "pages", "page_000.tif"));
      // Valid TIFF header + the original strip payload must be carried inside.
      Assert.That(page0[0], Is.EqualTo((byte)'I'));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(page0.AsSpan(2)), Is.EqualTo(0x002A));
      Assert.That(ContainsAscii(page0, "PAGE-ZERO-STRIP-DATA"), Is.True, "Page 0 must carry its strip data.");

      var page1 = File.ReadAllBytes(Path.Combine(outDir, "pages", "page_001.tif"));
      Assert.That(ContainsAscii(page1, "PAGE-ONE-STRIP"), Is.True, "Page 1 must carry its strip data.");
      Assert.That(ContainsAscii(page1, "PAGE-ZERO-STRIP-DATA"), Is.False, "Page 1 must NOT carry page 0's data.");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void Page_Tiff_Reports_Single_Page_When_ReListed() {
    var desc = new TiffFormatDescriptor();
    using var s = new MemoryStream(BuildTwoPageTiff());
    using var ms = new MemoryStream();
    ((IArchiveInMemoryExtract)desc).ExtractEntry(s, "pages/page_000.tif", ms, null);
    var page = ms.ToArray();

    // Re-feeding a single emitted page through the descriptor must yield exactly one page.
    using var s2 = new MemoryStream(page);
    var entries = desc.List(s2, null);
    var pages = entries.Count(e => e.Name.StartsWith("pages/"));
    Assert.That(pages, Is.EqualTo(1));
  }

  private static bool ContainsAscii(byte[] haystack, string needle) {
    var n = Encoding.ASCII.GetBytes(needle);
    for (var i = 0; i + n.Length <= haystack.Length; i++) {
      var ok = true;
      for (var j = 0; j < n.Length; j++)
        if (haystack[i + j] != n[j]) { ok = false; break; }
      if (ok) return true;
    }
    return false;
  }

  // ── Malformed ────────────────────────────────────────────────────────────────

  [Test]
  public void List_DoesNotThrow_On_Malformed() {
    var desc = new TiffFormatDescriptor();
    using var s = new MemoryStream([(byte)'I', (byte)'I', 0x2A, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = desc.List(s, null));
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.tif"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
