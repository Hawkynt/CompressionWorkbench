#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Smoke tests for <see cref="IFilesystemExtentMap"/> implementations on
/// FAT, ext, and D64. We don't pin exact byte offsets — those are
/// FS-version-specific — but we do assert: (1) the descriptor exposes the
/// interface, (2) <see cref="IFilesystemExtentMap.EnumerateExtents"/> yields
/// at least one metadata-reserved region and one used-by-file extent, and
/// (3) every used extent's <c>FileName</c> matches a file the FS lists.
/// </summary>
[TestFixture]
public class FilesystemExtentMapTests {

  [Test]
  public void Fat_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Fat.FatFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Fat_EnumerateExtents_ReportsUsedAndReserved() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "world"u8.ToArray());
    w.AddFile("BIG.BIN", new byte[8192]);
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new FileSystem.Fat.FatFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved), Is.True,
      "Expected at least one MetadataReserved extent (boot/FAT/root region).");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used), Is.True,
      "Expected at least one Used extent (a file's cluster chain).");
    var usedNames = extents.Where(e => e.Kind == DefragBlockKind.Used)
                           .Select(e => e.FileName).ToHashSet();
    Assert.That(usedNames, Does.Contain("HELLO.TXT").Or.Contain("HELLO.TXT".ToUpperInvariant()));
  }

  [Test]
  public void Ext_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Ext.ExtFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Ext_EnumerateExtents_ReportsSuperblockAndFileExtents() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new FileSystem.Ext.ExtFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.Offset == 1024), Is.True,
      "Expected superblock region at offset 1024.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used
                                  && e.FileName != null && e.FileName.EndsWith("hello.txt")),
      Is.True, "Expected at least one file extent named 'hello.txt'.");
  }

  [Test]
  public void D64_DescriptorImplementsExtentMap() {
    var d = new FileSystem.D64.D64FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void D64_EnumerateExtents_ReportsTrack18AsMetadata() {
    var w = new FileSystem.D64.D64Writer();
    w.AddFile("HELLO", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new FileSystem.D64.D64FormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    // Track 18 starts at sector index = 21*17 = 357, byte offset = 357*256 = 91392.
    var meta = extents.FirstOrDefault(e => e.Kind == DefragBlockKind.MetadataReserved);
    Assert.That(meta, Is.Not.Null, "Expected a MetadataReserved extent for track 18.");
    Assert.That(meta!.Offset, Is.EqualTo(91392),
      "Track 18 starts at byte offset 21*17*256 = 91392.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used
                                  && e.FileName != null && e.FileName.Contains("HELLO")),
      Is.True, "Expected at least one Used extent named HELLO.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // Modern big filesystems
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void ExFat_DescriptorImplementsExtentMap() {
    var d = new FileSystem.ExFat.ExFatFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void ExFat_EnumerateExtents_ReportsVbrAndFileExtents() {
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new FileSystem.ExFat.ExFatFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName != null && e.FileName.Contains("VBR")),
      Is.True, "Expected MetadataReserved extent for VBR.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used
                                  && e.FileName != null && e.FileName.EndsWith("hello.txt")),
      Is.True, "Expected at least one Used extent for hello.txt.");
  }

  [Test]
  public void Ntfs_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Ntfs.NtfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Ntfs_EnumerateExtents_ReportsBootAndMft() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build(8 * 1024 * 1024));

    var d = new FileSystem.Ntfs.NtfsFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName == "NTFS boot sector"),
      Is.True, "Expected MetadataReserved extent for boot sector.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName == "$MFT"),
      Is.True, "Expected MetadataReserved extent for $MFT.");
  }

  [Test]
  public void Iso_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Iso.IsoFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Iso_EnumerateExtents_ReportsSystemAreaAndFileExtents() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("HELLO.TXT", "world"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new FileSystem.Iso.IsoFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    var sysArea = extents.FirstOrDefault(e => e.Offset == 0
                                              && e.Kind == DefragBlockKind.MetadataReserved);
    Assert.That(sysArea, Is.Not.Null, "Expected MetadataReserved extent at offset 0 (system area).");
    Assert.That(sysArea!.Length, Is.EqualTo(16L * 2048),
      "System area is 16 * 2048 = 32 KiB.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used
                                  && e.FileName != null && e.FileName.Contains("HELLO")),
      Is.True, "Expected at least one Used extent for HELLO.TXT.");
  }

  [Test]
  public void Udf_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Udf.UdfFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Udf_EnumerateExtents_ReportsAvdpAndFsd() {
    var w = new FileSystem.Udf.UdfWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var d = new FileSystem.Udf.UdfFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName == "UDF AVDP"),
      Is.True, "Expected MetadataReserved extent for AVDP.");
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName == "UDF FSD"),
      Is.True, "Expected MetadataReserved extent for FSD.");
  }

  [Test]
  public void Btrfs_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Btrfs_EnumerateExtents_ReportsSuperblockAndFsTree() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var d = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName == "Btrfs superblock"),
      Is.True, "Expected MetadataReserved extent for superblock.");
  }

  [Test]
  public void Xfs_DescriptorImplementsExtentMap() {
    var d = new FileSystem.Xfs.XfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test]
  public void Xfs_EnumerateExtents_ReportsAgMetadata() {
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("hello.txt", "world"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var d = new FileSystem.Xfs.XfsFormatDescriptor();
    var extents = ((IFilesystemExtentMap)d).EnumerateExtents(ms).ToList();

    Assert.That(extents, Is.Not.Empty);
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                  && e.FileName != null && e.FileName.Contains("AG0")),
      Is.True, "Expected MetadataReserved extent for AG0 metadata.");
  }
}
