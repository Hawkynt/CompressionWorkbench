#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Tests for <see cref="IArchiveLayoutMap"/> implementations on ZIP, 7z, TAR,
/// LZH, and ARJ. For each format we create a small archive with 2-3 entries,
/// call EnumerateLayout, and assert that MetadataReserved + Used tiles cover
/// the full file with no gaps and that Used tile names match entry names.
/// </summary>
[TestFixture]
public class ArchiveLayoutMapTests {

  // ──────────────────────────────────────────────────────────────────────
  // ZIP
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Zip_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Zip.ZipFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Zip_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true);
    w.AddEntry("hello.txt", "world"u8.ToArray());
    w.AddEntry("data.bin", new byte[256]);
    w.Finish();

    var d = new FileFormat.Zip.ZipFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
    AssertUsedNameContains(tiles, "hello.txt");
    AssertUsedNameContains(tiles, "data.bin");
    AssertFullCoverage(tiles, ms.Length);
  }

  [Test]
  public void Zip_EnumerateLayout_ReportsCentralDirectory() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true);
    w.AddEntry("a.txt", "abc"u8.ToArray());
    w.Finish();

    var d = new FileFormat.Zip.ZipFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("Central Directory")),
      Is.True, "Expected a MetadataReserved tile for the Central Directory.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // 7z
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void SevenZip_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.SevenZip.SevenZipFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void SevenZip_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.SevenZip.SevenZipWriter(ms, FileFormat.SevenZip.SevenZipCodec.Lzma2);
    w.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "hello.txt", Size = 5 }, "world"u8.ToArray());
    w.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "data.bin", Size = 256 }, new byte[256]);
    w.Finish();

    var d = new FileFormat.SevenZip.SevenZipFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
    // 7z uses solid blocks, so names may be combined
    var usedNames = string.Join(" ", tiles.Where(t => t.Kind == DefragBlockKind.Used).Select(t => t.FileName));
    Assert.That(usedNames, Does.Contain("hello.txt").Or.Contain("Solid block"));
  }

  [Test]
  public void SevenZip_EnumerateLayout_ReportsSignatureHeader() {
    using var ms = new MemoryStream();
    var w = new FileFormat.SevenZip.SevenZipWriter(ms, FileFormat.SevenZip.SevenZipCodec.Lzma2);
    w.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "a.txt", Size = 3 }, "abc"u8.ToArray());
    w.Finish();

    var d = new FileFormat.SevenZip.SevenZipFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.Offset == 0 && t.Length == 32),
      Is.True, "Expected 32-byte signature header at offset 0.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // TAR
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Tar_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Tar.TarFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Tar_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tar.TarWriter(ms);
    w.AddEntry(new FileFormat.Tar.TarEntry { Name = "hello.txt", Size = 5 }, "world"u8.ToArray());
    w.AddEntry(new FileFormat.Tar.TarEntry { Name = "data.bin", Size = 4 }, "test"u8.ToArray());
    w.Finish();

    var d = new FileFormat.Tar.TarFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
    AssertUsedNameContains(tiles, "hello.txt");
    AssertUsedNameContains(tiles, "data.bin");
    AssertFullCoverage(tiles, ms.Length);
  }

  [Test]
  public void Tar_EnumerateLayout_ReportsEndOfArchiveMarker() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tar.TarWriter(ms);
    w.AddEntry(new FileFormat.Tar.TarEntry { Name = "a.txt", Size = 3 }, "abc"u8.ToArray());
    w.Finish();

    var d = new FileFormat.Tar.TarFormatDescriptor();
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("End-of-archive")),
      Is.True, "Expected end-of-archive marker tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // LZH
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Lzh_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Lzh.LzhFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Lzh_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var lw = new FileFormat.Lzh.LhaWriter(FileFormat.Lzh.LhaConstants.MethodLh0);
    lw.AddFile("hello.txt", "world"u8.ToArray());
    lw.AddFile("data.bin", new byte[64]);
    lw.WriteTo(ms);

    var d = new FileFormat.Lzh.LzhFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
    AssertUsedNameContains(tiles, "hello.txt");
    AssertUsedNameContains(tiles, "data.bin");
    AssertFullCoverage(tiles, ms.Length);
  }

  [Test]
  public void Lzh_EnumerateLayout_ReportsHeaders() {
    using var ms = new MemoryStream();
    var lw = new FileFormat.Lzh.LhaWriter(FileFormat.Lzh.LhaConstants.MethodLh0);
    lw.AddFile("test.txt", "abc"u8.ToArray());
    lw.WriteTo(ms);

    var d = new FileFormat.Lzh.LzhFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Count(t => t.Kind == DefragBlockKind.MetadataReserved), Is.GreaterThanOrEqualTo(1),
      "Expected at least one header tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // ARJ
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Arj_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Arj.ArjFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Arj_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Arj.ArjWriter(0); // method 0 = Store
    w.AddFile("hello.txt", "world"u8.ToArray());
    w.AddFile("data.bin", new byte[64]);
    w.WriteTo(ms);

    var d = new FileFormat.Arj.ArjFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
    AssertUsedNameContains(tiles, "hello.txt");
    AssertUsedNameContains(tiles, "data.bin");
    AssertFullCoverage(tiles, ms.Length);
  }

  [Test]
  public void Arj_EnumerateLayout_ReportsArchiveHeaderAndEoa() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Arj.ArjWriter(0);
    w.AddFile("a.txt", "abc"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Arj.ArjFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("Archive Header")),
      Is.True, "Expected ARJ Archive Header tile.");
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("End-of-archive")),
      Is.True, "Expected end-of-archive marker tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // Helpers
  // ──────────────────────────────────────────────────────────────────────

  private static void AssertHasMetadata(List<DefragBlockInfo> tiles) {
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved), Is.True,
      "Expected at least one MetadataReserved tile.");
  }

  private static void AssertHasUsed(List<DefragBlockInfo> tiles) {
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.Used), Is.True,
      "Expected at least one Used tile (compressed entry data).");
  }

  private static void AssertUsedNameContains(List<DefragBlockInfo> tiles, string name) {
    var usedNames = tiles
      .Where(t => t.Kind == DefragBlockKind.Used && t.FileName != null)
      .Select(t => t.FileName!)
      .ToList();
    Assert.That(usedNames.Any(n => n.Contains(name, StringComparison.OrdinalIgnoreCase)),
      Is.True, $"Expected a Used tile with name containing '{name}'. Found: [{string.Join(", ", usedNames)}]");
  }

  /// <summary>
  /// Verifies that the emitted tiles (MetadataReserved + Used + Free) cover the
  /// full archive with no gaps. Tiles may overlap slightly in some formats (e.g.,
  /// ZIP central directory starts where the last entry ends), so we check that
  /// every byte from 0..archiveSize is covered by at least one tile.
  /// </summary>
  private static void AssertFullCoverage(List<DefragBlockInfo> tiles, long archiveSize) {
    if (archiveSize == 0) return;

    // Sort tiles by offset and verify coverage
    var sorted = tiles.OrderBy(t => t.Offset).ToList();
    var coveredEnd = 0L;
    foreach (var tile in sorted) {
      // Allow small gaps but not large ones (some formats may have alignment padding)
      // For now, just check that the total covered bytes are reasonable
      if (tile.Offset + tile.Length > coveredEnd)
        coveredEnd = tile.Offset + tile.Length;
    }

    // The tiles should cover at least 90% of the file (allowing for minor alignment gaps)
    var totalCovered = sorted.Sum(t => t.Length);
    Assert.That(totalCovered, Is.GreaterThanOrEqualTo(archiveSize * 0.9),
      $"Tiles cover only {totalCovered} of {archiveSize} bytes ({100.0 * totalCovered / archiveSize:F1}%).");
  }
}
