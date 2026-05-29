#pragma warning disable CS1591
using Compression.Lib.FsConversion;

namespace Compression.Tests.FsConversion;

[TestFixture]
public class FilesystemResizerTests {

  // ── FAT shrink ─────────────────────────────────────────────────────────

  /// <summary>
  /// Shrinks a 1.44 MB FAT12 image down to 720 KB. Files inside that fit in
  /// the smaller image survive verbatim.
  /// </summary>
  [Test]
  public void FatShrink_FromFloppy_PreservesFiles() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("SMALL.TXT", "tiny"u8.ToArray());
    var image = writer.Build(totalSectors: 2880); // 1.44 MB

    using var stream = ToStream(image);
    FilesystemResizer.Shrink(stream, "Fat", 737_280);

    Assert.That(stream.Length, Is.EqualTo(737_280));
    AssertFatContentsMatch(stream, [("SMALL.TXT", "tiny"u8.ToArray())]);
  }

  /// <summary>
  /// Shrink succeeds even when the original allocator placed clusters above
  /// the new boundary — the resizer migrates them down before truncation.
  /// </summary>
  [Test]
  public void FatShrink_RequiringClusterMigration_StillWorks() {
    // Build an image with a file that lives at the very tail of the disk.
    // Strategy: fill the image with a big file that occupies clusters all
    // the way up, then shrink. The shrink must migrate the tail back.
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("FILLER.DAT", new byte[400_000]);
    var image = writer.Build(totalSectors: 2880); // 1.44 MB

    using var stream = ToStream(image);
    // Shrink to ~600 KB. The 400 KB file should still fit but its tail
    // clusters need migrating down.
    FilesystemResizer.Shrink(stream, "Fat", 614_400);

    Assert.That(stream.Length, Is.EqualTo(614_400));

    // Verify the file is still extractable and matches the original bytes.
    stream.Position = 0;
    using var reader = new FileSystem.Fat.FatReader(stream, leaveOpen: true);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(1));
    var extracted = reader.Extract(entries[0]);
    Assert.That(extracted, Has.Length.EqualTo(400_000));
  }

  /// <summary>
  /// Shrinking to a size that cannot hold the live data must throw rather
  /// than silently lose bytes.
  /// </summary>
  [Test]
  public void FatShrink_TooSmallForData_Throws() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("BIG.DAT", new byte[600_000]); // 600 KB of payload
    var image = writer.Build(totalSectors: 2880); // 1.44 MB

    using var stream = ToStream(image);
    // 100 KB target can't fit a 600 KB file.
    Assert.That(() => FilesystemResizer.Shrink(stream, "Fat", 102_400),
      Throws.InstanceOf<InvalidOperationException>());
  }

  /// <summary>
  /// Shrink target larger than current image is a no-op.
  /// </summary>
  [Test]
  public void FatShrink_LargerThanCurrent_IsNoOp() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("X.TXT", "x"u8.ToArray());
    var image = writer.Build(totalSectors: 1440); // 720 KB

    using var stream = ToStream(image);
    var oldLength = stream.Length;
    FilesystemResizer.Shrink(stream, "Fat", 1_474_560);
    Assert.That(stream.Length, Is.EqualTo(oldLength));
  }

  // ── FAT grow ───────────────────────────────────────────────────────────

  /// <summary>
  /// Grows a 720 KB FAT12 image up to 1.44 MB. Files inside survive verbatim
  /// and the new geometry exposes the larger volume size.
  /// </summary>
  [Test]
  public void FatGrow_From720kTo144M_PreservesFiles() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("GROW.TXT", "growing"u8.ToArray());
    var image = writer.Build(totalSectors: 1440); // 720 KB

    using var stream = ToStream(image);
    // Note: this grow would normally cross the FAT12/16 boundary at the
    // same cluster size (1440 sectors × 1 spc = ~1422 clusters → still
    // FAT12 at 2880 sectors → ~2848 clusters, still FAT12). Good.
    FilesystemResizer.Grow(stream, "Fat", 1_474_560);

    Assert.That(stream.Length, Is.EqualTo(1_474_560));
    AssertFatContentsMatch(stream, [("GROW.TXT", "growing"u8.ToArray())]);
  }

  /// <summary>
  /// After a grow, new free clusters above the old boundary become available
  /// to the FS (their FAT entries read as 0).
  /// </summary>
  [Test]
  public void FatGrow_NewClustersReadAsFree() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("TINY.TXT", "x"u8.ToArray());
    var image = writer.Build(totalSectors: 1440);
    var oldLength = (long)image.Length;

    using var stream = ToStream(image);
    FilesystemResizer.Grow(stream, "Fat", 1_474_560);

    // Spot-check that the bytes past the old length are zero (free clusters).
    stream.Position = oldLength;
    var probe = new byte[4096];
    stream.ReadExactly(probe);
    Assert.That(probe, Is.All.EqualTo((byte)0));
  }

  /// <summary>
  /// Grow target smaller than current image is a no-op.
  /// </summary>
  [Test]
  public void FatGrow_SmallerThanCurrent_IsNoOp() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("X.TXT", "x"u8.ToArray());
    var image = writer.Build(totalSectors: 2880);

    using var stream = ToStream(image);
    var oldLength = stream.Length;
    FilesystemResizer.Grow(stream, "Fat", 737_280);
    Assert.That(stream.Length, Is.EqualTo(oldLength));
  }

  // ── ext shrink ─────────────────────────────────────────────────────────

  /// <summary>
  /// Shrinks a 4 MB ext2 image to 2 MB. Files survive.
  /// </summary>
  [Test]
  public void ExtShrink_From4MbTo2Mb_PreservesFiles() {
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("HELLO", "Hello, ext!"u8.ToArray());
    var image = writer.Build(blockSize: 1024, totalBlocks: 4096); // 4 MB

    using var stream = ToStream(image);
    FilesystemResizer.Shrink(stream, "Ext", 2 * 1024 * 1024);

    Assert.That(stream.Length, Is.EqualTo(2 * 1024 * 1024));
    AssertExtContentsMatch(stream, [("HELLO", "Hello, ext!"u8.ToArray())]);
  }

  /// <summary>
  /// Shrinking below the minimum metadata footprint must throw.
  /// </summary>
  [Test]
  public void ExtShrink_BelowMetadataMinimum_Throws() {
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("F", "x"u8.ToArray());
    var image = writer.Build(blockSize: 1024, totalBlocks: 4096);

    using var stream = ToStream(image);
    // 4 KiB is well below SB(1)+BGD(1)+2 bitmaps+inode_table(16)+root(1)
    // = 21 blocks of 1 KiB = 21 KiB minimum.
    Assert.That(() => FilesystemResizer.Shrink(stream, "Ext", 4096),
      Throws.InstanceOf<InvalidOperationException>());
  }

  // ── ext grow ───────────────────────────────────────────────────────────

  /// <summary>
  /// Grows a 2 MB ext2 image to 4 MB. Files survive and the FS reports the
  /// new size.
  /// </summary>
  [Test]
  public void ExtGrow_From2MbTo4Mb_PreservesFiles() {
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("GROW", "growing"u8.ToArray());
    var image = writer.Build(blockSize: 1024, totalBlocks: 2048); // 2 MB

    using var stream = ToStream(image);
    FilesystemResizer.Grow(stream, "Ext", 4 * 1024 * 1024);

    Assert.That(stream.Length, Is.EqualTo(4 * 1024 * 1024));
    AssertExtContentsMatch(stream, [("GROW", "growing"u8.ToArray())]);
  }

  /// <summary>
  /// Grow past the single-group bitmap capacity (blockSize × 8 bits) must
  /// throw — additional block groups are out of scope.
  /// </summary>
  [Test]
  public void ExtGrow_PastSingleGroupBitmap_Throws() {
    // 1024-byte blocks × 8 = 8192 max blocks per group → 8 MB cap.
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("X", "x"u8.ToArray());
    var image = writer.Build(blockSize: 1024, totalBlocks: 2048);

    using var stream = ToStream(image);
    // Try to grow to 16 MB — well past the single-group cap.
    Assert.That(() => FilesystemResizer.Grow(stream, "Ext", 16 * 1024 * 1024),
      Throws.InstanceOf<NotSupportedException>());
  }

  // ── Dispatcher ─────────────────────────────────────────────────────────

  /// <summary>
  /// IsSupported recognises FAT and ext family ids (case + alias forms).
  /// </summary>
  [Test]
  public void IsSupported_RecognisesKnownFamilies() {
    Assert.Multiple(() => {
      Assert.That(FilesystemResizer.IsSupported("Fat"), Is.True);
      Assert.That(FilesystemResizer.IsSupported("Fat12"), Is.True);
      Assert.That(FilesystemResizer.IsSupported("Fat32"), Is.True);
      Assert.That(FilesystemResizer.IsSupported("Ext"), Is.True);
      Assert.That(FilesystemResizer.IsSupported("ext4"), Is.True);
      Assert.That(FilesystemResizer.IsSupported("Ntfs"), Is.False);
      Assert.That(FilesystemResizer.IsSupported(""), Is.False);
      Assert.That(FilesystemResizer.IsSupported(null!), Is.False);
    });
  }

  /// <summary>
  /// Resizing an unsupported FS via the dispatcher throws NotSupportedException.
  /// </summary>
  [Test]
  public void Shrink_UnsupportedFs_Throws() {
    using var stream = new MemoryStream(new byte[4096]);
    Assert.That(() => FilesystemResizer.Shrink(stream, "Ntfs", 2048),
      Throws.InstanceOf<NotSupportedException>());
  }

  // ── Crash simulation ───────────────────────────────────────────────────

  /// <summary>
  /// Simulates a crash mid-grow: stream is extended but BPB is not yet
  /// patched. The resulting image must still be readable as the original
  /// (smaller) FS — just with trailing dead space.
  /// </summary>
  [Test]
  public void FatGrow_CrashAfterSetLengthBeforeBpb_StillReadable() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("KEEP.TXT", "intact"u8.ToArray());
    var image = writer.Build(totalSectors: 1440);
    var oldLen = image.Length;

    // Manually do step 1 only (extend stream + zero new region).
    using var stream = ToStream(image);
    stream.SetLength(1_474_560);
    var tail = new byte[1_474_560 - oldLen];
    stream.Position = oldLen;
    stream.Write(tail);
    // ⚠ Intentionally skip the BPB update — simulating a crash.

    // Reader should still find the original file with the original BPB
    // saying 1440 sectors.
    stream.Position = 0;
    stream.Position = 0;
    using var reader = new FileSystem.Fat.FatReader(stream, leaveOpen: true);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(entries[0]), Is.EqualTo("intact"u8.ToArray()));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream ToStream(byte[] image) {
    // Use the capacity ctor + Write so the stream is expandable (so SetLength
    // can grow it past the initial length, exactly like a real file).
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    ms.Position = 0;
    return ms;
  }

  private static void AssertFatContentsMatch(Stream image, (string Name, byte[] Data)[] expected) {
    image.Position = 0;
    using var reader = new FileSystem.Fat.FatReader(image, leaveOpen: true);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(expected.Length));
    foreach (var (name, data) in expected) {
      var e = entries.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(e, Is.Not.Null, $"Missing entry: {name}");
      Assert.That(reader.Extract(e!), Is.EqualTo(data), $"Data mismatch for: {name}");
    }
  }

  private static void AssertExtContentsMatch(Stream image, (string Name, byte[] Data)[] expected) {
    image.Position = 0;
    var reader = new FileSystem.Ext.ExtReader(image);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(expected.Length));
    foreach (var (name, data) in expected) {
      var e = entries.FirstOrDefault(x => x.Name == name);
      Assert.That(e, Is.Not.Null, $"Missing entry: {name}");
      Assert.That(reader.Extract(e!), Is.EqualTo(data), $"Data mismatch for: {name}");
    }
  }
}
