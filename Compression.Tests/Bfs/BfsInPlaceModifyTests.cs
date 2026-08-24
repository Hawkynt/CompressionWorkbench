using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Bfs;

/// <summary>
/// Locks the true-in-place R/W contract of <see cref="FileSystem.Bfs.BfsFormatDescriptor"/>:
/// Add/Remove/Replace operations on a single-AG, single-leaf-root BFS image
/// touch only the inode, the root B+ tree leaf at block 12, the AG bitmap at
/// block 10, the superblock's used_blocks counter, and the affected data
/// blocks — every other block in the image stays byte-identical at its
/// original offset.
/// </summary>
[TestFixture]
public class BfsInPlaceModifyTests {

  private const int BlockSize = 1024;
  private const int AgBitmapBlock = 10;
  /// <summary>The block the root directory's B+ tree stream starts at.</summary>
  private const int RootDirBtreeStreamBlock = 12;

  /// <summary>
  /// The block its root node sits on — the one entries are written into.
  /// </summary>
  /// <remarks>
  /// A stream opens with the tree's own header and the nodes follow, so the node
  /// is the block after the stream's first. This was the stream's first block,
  /// from when there was no header and the two were the same thing.
  /// </remarks>
  private const int RootDirBtreeBlock = RootDirBtreeStreamBlock + 1;

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Bfs.BfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  /// <summary>Lists block numbers whose bytes differ between two equal-sized images.</summary>
  private static HashSet<int> ChangedBlocks(byte[] before, byte[] after) {
    Assert.That(after.Length, Is.EqualTo(before.Length),
      "in-place modifier must keep image size byte-for-byte stable");
    var changed = new HashSet<int>();
    var blocks = before.Length / BlockSize;
    for (var b = 0; b < blocks; b++) {
      var off = b * BlockSize;
      if (!before.AsSpan(off, BlockSize).SequenceEqual(after.AsSpan(off, BlockSize)))
        changed.Add(b);
    }
    return changed;
  }

  private static bool ReadBitmapBit(byte[] image, int blockNum) {
    var off = AgBitmapBlock * BlockSize + blockNum / 8;
    return (image[off] & (1 << (7 - (blockNum % 8)))) != 0;
  }

  private static long ReadSuperblockUsedBlocks(byte[] image)
    => BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(56));

  // ── Add ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Add_UntouchedBlocks_AreByteIdentical() {
    var before = BuildImageWith(
      ("alpha.txt", "alpha content"u8.ToArray()),
      ("beta.txt", "beta content"u8.ToArray()));

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fresh payload"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "gamma.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    var after = ms.ToArray();
    var changed = ChangedBlocks(before, after);

    // Required touched blocks: superblock (block 0, used_blocks counter),
    // AG bitmap (10), root B+ tree leaf (12), plus the newly-allocated
    // inode block and the data block for "fresh payload" (one block).
    Assert.That(changed, Does.Contain(0), "superblock must reflect new used_blocks");
    Assert.That(changed, Does.Contain(AgBitmapBlock), "AG bitmap must record new allocations");
    Assert.That(changed, Does.Contain(RootDirBtreeBlock), "root B+ tree leaf must include new entry");

    // The required allowlist: superblock, bitmap, root leaf, plus the newly
    // allocated inode and data blocks (each freshly-allocated free run is
    // contiguous in our bitmap allocator, so this gives a tight upper bound).
    var allowed = new HashSet<int> { 0, AgBitmapBlock, RootDirBtreeStreamBlock, RootDirBtreeBlock };
    // Find which new blocks (>=15) the bitmap flipped from 0→1.
    var blocks = before.Length / BlockSize;
    for (var b = 15; b < blocks; b++)
      if (!ReadBitmapBit(before, b) && ReadBitmapBit(after, b)) allowed.Add(b);

    Assert.That(changed.IsSubsetOf(allowed), Is.True,
      $"Changed blocks {string.Join(",", changed.OrderBy(x => x))} must be subset of allowed {string.Join(",", allowed.OrderBy(x => x))}");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_RoundTrip_ListAndExtract() {
    var before = BuildImageWith(("old.txt", "kept"u8.ToArray()));
    var d = new FileSystem.Bfs.BfsFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added bytes"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "added.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("old.txt"));
    Assert.That(names, Does.Contain("added.txt"));

    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "bfs_addrt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "old.txt")),
        Is.EqualTo("kept"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "added.txt")),
        Is.EqualTo("added bytes"u8.ToArray()));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Add_MultipleFiles_BitmapTracksAllAllocations() {
    var before = BuildImageWith(("seed.txt", "seed"u8.ToArray()));
    var d = new FileSystem.Bfs.BfsFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmpA = Path.GetTempFileName();
    var tmpB = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpA, "alpha payload"u8.ToArray());
      File.WriteAllBytes(tmpB, new byte[2048]); // 2 blocks
      d.Add(ms, [
        new ArchiveInputInfo(tmpA, "a.txt", false),
        new ArchiveInputInfo(tmpB, "b.bin", false),
      ]);
    } finally {
      File.Delete(tmpA);
      File.Delete(tmpB);
    }

    var after = ms.ToArray();

    // Count freshly-allocated blocks via bitmap delta.
    var blocks = before.Length / BlockSize;
    var allocated = 0;
    for (var b = 0; b < blocks; b++)
      if (!ReadBitmapBit(before, b) && ReadBitmapBit(after, b)) allocated++;

    // a.txt: 1 inode + 1 data = 2 blocks; b.bin: 1 inode + 2 data = 3 blocks.
    Assert.That(allocated, Is.EqualTo(5),
      "expected 5 newly allocated blocks (2 inodes + 3 data)");

    Assert.That(ReadSuperblockUsedBlocks(after),
      Is.EqualTo(ReadSuperblockUsedBlocks(before) + 5),
      "superblock used_blocks must mirror bitmap delta");
  }

  // ── Remove ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Remove_UntouchedBlocks_AreByteIdentical() {
    var before = BuildImageWith(
      ("keep.txt", "keep content"u8.ToArray()),
      ("delete.txt", "deleted content"u8.ToArray()));

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    d.Remove(ms, ["delete.txt"]);

    var after = ms.ToArray();
    var changed = ChangedBlocks(before, after);

    // Required touched blocks: superblock (0), bitmap (10), root leaf (12),
    // freed inode block of delete.txt, and the freed data block(s).
    Assert.That(changed, Does.Contain(0));
    Assert.That(changed, Does.Contain(AgBitmapBlock));
    Assert.That(changed, Does.Contain(RootDirBtreeBlock));

    var allowed = new HashSet<int> { 0, AgBitmapBlock, RootDirBtreeStreamBlock, RootDirBtreeBlock };
    var blocks = before.Length / BlockSize;
    for (var b = 15; b < blocks; b++)
      if (ReadBitmapBit(before, b) && !ReadBitmapBit(after, b)) allowed.Add(b);

    Assert.That(changed.IsSubsetOf(allowed), Is.True,
      $"Changed {string.Join(",", changed.OrderBy(x => x))} must subset allowed {string.Join(",", allowed.OrderBy(x => x))}");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remove_RoundTrip_RemovedFileGoneKeptFileIntact() {
    var before = BuildImageWith(
      ("keep.txt", "still here"u8.ToArray()),
      ("delete.txt", "going away"u8.ToArray()));

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    d.Remove(ms, ["delete.txt"]);

    ms.Position = 0;
    var entries = d.List(ms, null).ToList();
    Assert.That(entries.Select(e => e.Name), Does.Contain("keep.txt"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("delete.txt"));

    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "bfs_rmrt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "keep.txt")),
        Is.EqualTo("still here"u8.ToArray()));
      Assert.That(File.Exists(Path.Combine(outDir, "delete.txt")), Is.False);
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Remove_FreesBitmapBitsAndUpdatesUsedBlocks() {
    var before = BuildImageWith(
      ("keep.txt", "keep"u8.ToArray()),
      ("victim.bin", new byte[3000])); // ~3 blocks

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    d.Remove(ms, ["victim.bin"]);
    var after = ms.ToArray();

    var blocks = before.Length / BlockSize;
    var freed = 0;
    for (var b = 0; b < blocks; b++)
      if (ReadBitmapBit(before, b) && !ReadBitmapBit(after, b)) freed++;

    // 1 inode + 3 data blocks = 4 blocks freed.
    Assert.That(freed, Is.EqualTo(4));
    Assert.That(ReadSuperblockUsedBlocks(after),
      Is.EqualTo(ReadSuperblockUsedBlocks(before) - 4));
  }

  // ── Replace (in-place) ─────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Replace_FitsInExistingRun_UntouchedBlocksAreByteIdentical() {
    // Pad the original file so it occupies a whole 1024-byte block; the
    // replacement is smaller so it fits in the same allocation.
    var original = Encoding.UTF8.GetBytes(new string('A', 800));
    var before = BuildImageWith(
      ("anchor.txt", "anchor"u8.ToArray()),
      ("target.txt", original));

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var ok = FileSystem.Bfs.BfsInPlaceModifier.TryReplace(ms, "target.txt",
      Encoding.UTF8.GetBytes("short replacement"));
    Assert.That(ok, Is.True);

    var after = ms.ToArray();
    var changed = ChangedBlocks(before, after);

    // The inode (size field changes) + the single data block of target.txt
    // must change; bitmap stays untouched (run didn't move); root leaf stays
    // untouched (same name, same inode block). Superblock used_blocks
    // unchanged so block 0 should match too.
    Assert.That(changed, Does.Not.Contain(AgBitmapBlock),
      "in-place replace within run must not touch bitmap");
    Assert.That(changed, Does.Not.Contain(RootDirBtreeBlock),
      "in-place replace must not touch root B+ tree leaf");
    Assert.That(changed, Does.Not.Contain(0),
      "in-place replace within run leaves used_blocks unchanged");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Replace_FitsInExistingRun_RoundTripsNewContent() {
    var before = BuildImageWith(
      ("anchor.txt", "anchor"u8.ToArray()),
      ("target.txt", Encoding.UTF8.GetBytes(new string('A', 800))));

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var newBytes = Encoding.UTF8.GetBytes("short replacement");
    Assert.That(FileSystem.Bfs.BfsInPlaceModifier.TryReplace(ms, "target.txt", newBytes), Is.True);

    ms.Position = 0;
    var r = new FileSystem.Bfs.BfsReader(ms);
    var target = r.Entries.Single(e => e.Name == "target.txt");
    Assert.That(target.Size, Is.EqualTo(newBytes.Length));
    Assert.That(r.Extract(target), Is.EqualTo(newBytes));

    var anchor = r.Entries.Single(e => e.Name == "anchor.txt");
    Assert.That(r.Extract(anchor), Is.EqualTo("anchor"u8.ToArray()));
  }

  // ── Bitmap-consistency invariants ──────────────────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Add_AllocatesFromBitmap_NotOverwritingExistingFiles() {
    var before = BuildImageWith(
      ("first.txt", "first"u8.ToArray()),
      ("second.txt", "second"u8.ToArray()));
    var d = new FileSystem.Bfs.BfsFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "newcomer"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "third.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var r = new FileSystem.Bfs.BfsReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["first.txt"]), Is.EqualTo("first"u8.ToArray()));
    Assert.That(r.Extract(byName["second.txt"]), Is.EqualTo("second"u8.ToArray()));
    Assert.That(r.Extract(byName["third.txt"]), Is.EqualTo("newcomer"u8.ToArray()));
  }

  // ── Mutate-then-extract end-to-end ─────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AddThenRemoveThenList_LeavesOriginalSetMinusRemoved() {
    var before = BuildImageWith(
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", "BBB"u8.ToArray()));
    var d = new FileSystem.Bfs.BfsFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "CCC"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "c.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    d.Remove(ms, ["a.txt"]);

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).OrderBy(n => n).ToList();
    Assert.That(names, Is.EqualTo(new[] { "b.txt", "c.txt" }));

    ms.Position = 0;
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "b.txt")), Is.EqualTo("BBB"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "c.txt")), Is.EqualTo("CCC"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Add_DescriptorRoute_KeepsLastModifiedExtractBytesIntact() {
    var before = BuildImageWith(("keep.bin", new byte[1500]));
    var keepCopy = new byte[1500];
    // Fill kept file with a recognisable pattern so we can verify it survived.
    for (var i = 0; i < keepCopy.Length; i++) keepCopy[i] = (byte)(i & 0xFF);

    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("keep.bin", keepCopy);
    var img = w.Build();

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added!"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "newcomer.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "keep.bin")), Is.EqualTo(keepCopy));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "newcomer.txt")), Is.EqualTo("added!"u8.ToArray()));
  }

  // ── Subdirectory falls back to rebuild ─────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Add_WithSubdirectoryPath_FallsBackToRebuild_StillRoundTrips() {
    var before = BuildImageWith(("root.txt", "root"u8.ToArray()));
    var d = new FileSystem.Bfs.BfsFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.SetLength(before.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "in subdir"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "sub/nested.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("root.txt"));
    Assert.That(names, Does.Contain("sub/nested.txt"));
  }
}
