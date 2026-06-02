#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// Tests the real Acorn MFS-1 catalog walker. Builds a synthetic DFS-shaped
/// image (256-byte sectors, two-sector catalog, one user file in sector 2)
/// and verifies the reader surfaces the catalog entry with the right name,
/// size, and extracted bytes.
/// </summary>
[TestFixture]
public class Mfs1ReaderTests {

  private const int SectorSize = 256;

  /// <summary>
  /// Build a minimal MFS-1 image with one file "$.HELLO" of 12 bytes starting
  /// at sector 2.
  /// </summary>
  /// <param name="filePayload">Returns the bytes written into sector 2.</param>
  private static byte[] BuildMinimal(out byte[] filePayload) {
    // 4 sectors: catalog (sectors 0,1) + file data (sector 2) + slack (sector 3).
    var image = new byte[4 * SectorSize];

    // Title "MFS1DISK" + "PART"  (8 chars in s0, 4 in s1)
    Encoding.ASCII.GetBytes("MFS1DISK").CopyTo(image.AsSpan(0));
    Encoding.ASCII.GetBytes("PART").CopyTo(image.AsSpan(SectorSize));

    // entry-count * 8 at s1+5
    image[SectorSize + 5] = 1 * 8;

    // Entry 0 name (7 chars) "HELLO  " + dir '$' at sector0 + 8.
    Encoding.ASCII.GetBytes("HELLO  ").CopyTo(image.AsSpan(8));
    image[8 + 7] = (byte)'$';

    // Entry 0 metadata at sector1 + 8: load=0, exec=0, length=12, packed=0, startSector=2
    var metaOff = SectorSize + 8;
    image[metaOff + 4] = 12;           // length lo = 12
    image[metaOff + 5] = 0;            // length mid
    image[metaOff + 6] = 0;            // packed high bits = 0
    image[metaOff + 7] = 2;            // start sector lo = 2

    // File data at sector 2.
    filePayload = "Hello MFS-1!"u8.ToArray();
    filePayload.CopyTo(image.AsSpan(2 * SectorSize));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesCatalog_AndExtractsFile() {
    var img = BuildMinimal(out var content);
    using var ms = new MemoryStream(img);
    var r = new Mfs1Reader(ms);
    Assert.That(r.CatalogParsed, Is.True);
    Assert.That(r.DiskTitle, Does.Contain("MFS1DISK"));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Directory, Is.EqualTo('$'));
    Assert.That(r.Entries[0].FullName, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(12u));

    var bytes = r.Extract(r.Entries[0]);
    Assert.That(bytes, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_IncludesCatalogEntry() {
    var img = BuildMinimal(out _);
    using var ms = new MemoryStream(img);
    var d = new Mfs1FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.mfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("HELLO"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesFileFromCatalog() {
    var img = BuildMinimal(out var content);
    using var ms = new MemoryStream(img);
    var d = new Mfs1FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "mfs1_ex_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var helloPath = Path.Combine(outDir, "HELLO");
      Assert.That(File.Exists(helloPath), Is.True);
      Assert.That(File.ReadAllBytes(helloPath), Is.EqualTo(content));

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("catalog_entries=1"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream_ReadPastSizeReturnsZero() {
    var img = BuildMinimal(out var content);
    using var ms = new MemoryStream(img);
    var d = new Mfs1FormatDescriptor();

    using var s = d.OpenEntry(ms, "HELLO", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>(), "OpenEntry must return BoundedEntryStream");
    Assert.That(s.Length, Is.EqualTo(content.Length));

    var buf = new byte[64];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(content.Length));
    Assert.That(buf.AsSpan(0, n).ToArray(), Is.EqualTo(content));

    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("Sad")]
  public void OpenEntry_UnknownName_ReturnsEmptyBoundedStream() {
    var img = BuildMinimal(out _);
    using var ms = new MemoryStream(img);
    var d = new Mfs1FormatDescriptor();
    using var s = d.OpenEntry(ms, "no-such-file", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(0));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadCountByte_NoEntries_NoThrow() {
    var img = new byte[4 * SectorSize];
    img[SectorSize + 5] = 7; // count*8 = 7 is not a multiple of 8 → invalid
    using var ms = new MemoryStream(img);
    var r = new Mfs1Reader(ms);
    Assert.That(r.CatalogParsed, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_EntryRunsOffImage_Skipped() {
    var img = new byte[4 * SectorSize];
    Encoding.ASCII.GetBytes("DISKTITL").CopyTo(img.AsSpan(0));
    img[SectorSize + 5] = 1 * 8; // 1 entry
    Encoding.ASCII.GetBytes("BIG    ").CopyTo(img.AsSpan(8));
    img[15] = (byte)'$';
    // Bogus length 1MB starting at sector 2 → off-image.
    var metaOff = SectorSize + 8;
    img[metaOff + 4] = 0x00;
    img[metaOff + 5] = 0x00;
    img[metaOff + 6] = 0x10; // length high bits → length = 0x100000
    img[metaOff + 7] = 2;

    using var ms = new MemoryStream(img);
    var r = new Mfs1Reader(ms);
    Assert.That(r.Entries, Is.Empty, "Entry whose extent overruns the image must be skipped.");
  }
}
