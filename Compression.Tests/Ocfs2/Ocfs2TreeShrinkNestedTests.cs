using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Extent trees, in-place shrink and nested in-place edits, checked without the
/// kernel. Kernel-written trees, and fsck plus kernel mounts of shrunk and edited
/// volumes, are in <see cref="Ocfs2KernelMountTests"/>.
/// </summary>
[TestFixture]
public class Ocfs2TreeShrinkNestedTests {

  private const int BlockSize = 4096;

  // ── extent trees ──────────────────────────────────────────────────────

  /// <summary>
  /// Given a file whose extents hang off a one-level tree — the dinode's list at
  /// depth 1 naming an extent block whose leaf records cover the file — when it
  /// is read, then the bytes are exactly those of the same file held flat.
  /// </summary>
  [Test, Category("HappyPath")]
  public void DepthOneTree_ReadsTheSameBytesAsTheFlatFile() {
    var (image, data, dinode, first) = ThreeClusterFile();
    var leaf = BuildExtentBlock(image, ExtentBlockAt(image, 1), depth: 0,
      [(0u, 1, first, false), (1u, 1, first + 1, false), (2u, 1, first + 2, false)]);
    PointDinodeAt(image, dinode, depth: 1, [(0u, 3, leaf)]);

    Assert.That(Read(image, "big.bin"), Is.EqualTo(data));
    Assert.That(Ocfs2Reader.ReadFilePlacements(image).Single().HasTree, Is.True);
  }

  /// <summary>
  /// Given a two-level tree — dinode at depth 2, an interior extent block at
  /// depth 1, a leaf block at depth 0 — when the file is read, then every level
  /// is followed down to the data.
  /// </summary>
  [Test, Category("HappyPath")]
  public void DepthTwoTree_IsFollowedThroughEveryLevel() {
    var (image, data, dinode, first) = ThreeClusterFile();
    var leaf = BuildExtentBlock(image, ExtentBlockAt(image, 1), depth: 0, [(0u, 3, first, false)]);
    var interior = BuildExtentBlock(image, ExtentBlockAt(image, 2), depth: 1, [(0u, 3, leaf, false)]);
    PointDinodeAt(image, dinode, depth: 2, [(0u, 3, interior)]);

    Assert.That(Read(image, "big.bin"), Is.EqualTo(data));
  }

  /// <summary>
  /// Given a tree whose leaves leave a hole (cluster 1 not mapped) and mark a run
  /// unwritten (cluster 2), when the file is read, then both read as zeros and
  /// the mapped cluster reads its bytes.
  /// </summary>
  [Test, Category("Boundary")]
  public void TreeWithHoleAndUnwrittenRun_ReadsZerosThere() {
    var (image, data, dinode, first) = ThreeClusterFile();
    var leaf = BuildExtentBlock(image, ExtentBlockAt(image, 1), depth: 0,
      [(0u, 1, first, false), (2u, 1, first + 2, true)]);
    PointDinodeAt(image, dinode, depth: 1, [(0u, 3, leaf)]);

    var expected = (byte[])data.Clone();
    Array.Clear(expected, BlockSize, expected.Length - BlockSize);
    Assert.That(Read(image, "big.bin"), Is.EqualTo(expected));
  }

  /// <summary>
  /// Given a tree whose interior record names a block that is not an extent
  /// block, when the volume is listed, then List does not throw, and the reader
  /// raises InvalidDataException instead of handing back zeros.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void CorruptTree_ListDoesNotThrow_ReaderRefusesToInventBytes() {
    var (image, _, dinode, first) = ThreeClusterFile();
    PointDinodeAt(image, dinode, depth: 1, [(0u, 3, first)]); // names a data block
    using var ms = new MemoryStream(image);
    Assert.DoesNotThrow(() => new Ocfs2FormatDescriptor().List(ms, null));
    Assert.Throws<InvalidDataException>(() => Ocfs2Reader.ReadFiles(image));
  }

  // ── shrink ────────────────────────────────────────────────────────────

  /// <summary>
  /// Given a 140 MiB volume (two cluster groups) holding a few small files, when
  /// it is shrunk, then the second group is gone, the volume ends at its last
  /// allocated cluster, every allocator count agrees, the files read back
  /// unchanged, and the label and UUID are those of the original.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Shrink_DropsTrailingGroupsAndKeepsEverything() {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(140L * 1024 * 1024);
    w.SetLabel("KEEPME");
    w.AddFile("a.txt", "alpha"u8.ToArray());
    w.AddFile("docs/b.bin", Ocfs2LayoutTests.Pattern(50_000));
    var before = w.Build();

    var after = Shrink(before);

    Assert.That(after.Length, Is.LessThan(32L * 1024 * 1024), "the 16 MiB journal of a 140 MiB volume stays; the free tail goes");
    Ocfs2LayoutTests.AssertAllocatorsConsistent(after);
    var bitmap = after.AsSpan(11 * BlockSize, BlockSize);
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bitmap[(0xC0 + 6)..]), Is.EqualTo(1), "one cluster group left");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bitmap[0xBC..]), Is.EqualTo((uint)(after.Length / BlockSize)), "i_total is the new size");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(2 * BlockSize + 0x14)), Is.EqualTo((uint)(after.Length / BlockSize)), "superblock i_clusters");
    Assert.That(Ocfs2Reader.ReadFiles(after).ToDictionary(f => f.Name, f => f.Data),
      Is.EquivalentTo(Ocfs2Reader.ReadFiles(before).ToDictionary(f => f.Name, f => f.Data)));
    var sb = 2 * BlockSize + 0xC0;
    Assert.That(after.AsSpan(sb + 0x50, 0x50).ToArray(), Is.EqualTo(before.AsSpan(sb + 0x50, 0x50).ToArray()), "label and UUID");
  }

  /// <summary>
  /// Given a volume that ends at its last allocated cluster, when it is shrunk,
  /// then the output is byte for byte the input — never a rebuild.
  /// </summary>
  [Test, Category("Boundary")]
  public void Shrink_TightVolume_ComesBackUnchanged() {
    var w = new Ocfs2Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    var once = Shrink(w.Build());
    Assert.That(Shrink(once), Is.EqualTo(once));
  }

  /// <summary>
  /// Given a volume whose truncate log still holds clusters to free, when it is
  /// shrunk, then the trim is refused and the volume is copied through unchanged.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void Shrink_PendingTruncateLog_IsRefusedAndCopiedThrough() {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(32L * 1024 * 1024);
    w.AddFile("a.txt", "alpha"u8.ToArray());
    var image = w.Build();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(17 * BlockSize + 0xC0 + 2), 1); // tl_used
    Assert.That(Shrink(image), Is.EqualTo(image));
  }

  // ── nested edits ──────────────────────────────────────────────────────

  /// <summary>
  /// Given a volume with a directory, when files are added into it, into three
  /// new levels of directories, replaced and removed — all in place — then the
  /// tree reads back as edited, every allocator count agrees, and each parent's
  /// link count grew by its new subdirectories.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void NestedEdits_AddReplaceRemoveAndMakeDirectories() {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(16L * 1024 * 1024);
    w.AddFile("docs/old.txt", "old"u8.ToArray());
    w.AddFile("docs/keep.txt", "keep"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    var rootLinks = Links(ms.ToArray(), 5);

    var d = new Ocfs2FormatDescriptor();
    d.Add(ms, [
      ArchiveInputInfo.InMemory("docs/new.txt", "new"u8.ToArray()),
      ArchiveInputInfo.InMemory("a/b/c/deep.bin", Ocfs2LayoutTests.Pattern(20_000)),
      ArchiveInputInfo.InMemory("docs/keep.txt", "kept, replaced"u8.ToArray()),
    ]);
    d.Remove(ms, ["docs/old.txt"]);

    var image = ms.ToArray();
    Ocfs2LayoutTests.AssertAllocatorsConsistent(image);
    var files = Ocfs2Reader.ReadFiles(image).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "docs/new.txt", "docs/keep.txt", "a/b/c/deep.bin" }));
    Assert.That(files["docs/keep.txt"], Is.EqualTo("kept, replaced"u8.ToArray()));
    Assert.That(files["a/b/c/deep.bin"], Is.EqualTo(Ocfs2LayoutTests.Pattern(20_000)));
    Assert.That(Links(image, 5), Is.EqualTo(rootLinks + 1), "root gained the subdirectory 'a'");
  }

  /// <summary>
  /// Given a path that runs through a regular file, or a removal that names a
  /// directory, when the edit is asked for, then it is refused and the image is
  /// byte for byte unchanged; a missing nested file is reported as missing.
  /// </summary>
  [TestCase("add-through-file")]
  [TestCase("remove-directory")]
  [Category("ErrorHandling")]
  public void ImpossibleNestedEdit_IsRefusedAndLeavesTheImageUnchanged(string edit) {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(16L * 1024 * 1024);
    w.AddFile("docs/guide.txt", "guide"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    var before = ms.ToArray();
    var d = new Ocfs2FormatDescriptor();

    Assert.Throws<NotSupportedException>(() => {
      if (edit == "add-through-file") d.Add(ms, [ArchiveInputInfo.InMemory("docs/guide.txt/x", "x"u8.ToArray())]);
      else d.Remove(ms, ["docs"]);
    });
    Assert.That(ms.ToArray(), Is.EqualTo(before));
    Assert.Throws<FileNotFoundException>(() => d.Remove(ms, ["docs/ghost.txt"]));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  // ── helpers ───────────────────────────────────────────────────────────

  private static byte[] Shrink(byte[] image) {
    using var input = new MemoryStream(image);
    using var output = new MemoryStream();
    new Ocfs2FormatDescriptor().Shrink(input, output);
    return output.ToArray();
  }

  private static int Links(byte[] image, long blkno) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)(blkno * BlockSize) + 0x2A));

  private static byte[] Read(byte[] image, string name) => Ocfs2Reader.ReadFiles(image).Single(f => f.Name == name).Data;

  /// <summary>A volume with one three-cluster file held in one flat extent record.</summary>
  private static (byte[] Image, byte[] Data, long Dinode, long FirstData) ThreeClusterFile() {
    var data = Ocfs2LayoutTests.Pattern(3 * BlockSize - 100);
    var w = new Ocfs2Writer();
    w.AddFile("big.bin", data);
    var image = w.Build();
    var p = Ocfs2Reader.ReadFilePlacements(image).Single();
    return (image, data, p.DinodeBlkno, p.DataBlkno);
  }

  /// <summary>One of the free slack blocks at the end of a writer volume.</summary>
  private static long ExtentBlockAt(byte[] image, int fromEnd) => image.Length / BlockSize - fromEnd;

  /// <summary>Writes an EXBLK01 extent block at <paramref name="blkno"/> holding the given records.</summary>
  private static long BuildExtentBlock(byte[] image, long blkno, int depth, (uint Cpos, int Clusters, long Blkno, bool Unwritten)[] records) {
    var b = image.AsSpan((int)(blkno * BlockSize), BlockSize);
    b.Clear();
    "EXBLK01"u8.CopyTo(b);
    BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], (ulong)blkno);
    WriteList(b[0x30..], depth, 252, records);
    return blkno;
  }

  private static void PointDinodeAt(byte[] image, long dinode, int depth, (uint Cpos, int Clusters, long Blkno)[] records) {
    var list = image.AsSpan((int)(dinode * BlockSize) + 0xC0, BlockSize - 0xC0);
    list.Clear();
    WriteList(list, depth, 243, records.Select(r => (r.Cpos, r.Clusters, r.Blkno, false)).ToArray());
  }

  private static void WriteList(Span<byte> list, int depth, int count, (uint Cpos, int Clusters, long Blkno, bool Unwritten)[] records) {
    BinaryPrimitives.WriteUInt16LittleEndian(list, (ushort)depth);
    BinaryPrimitives.WriteUInt16LittleEndian(list[2..], (ushort)count);
    BinaryPrimitives.WriteUInt16LittleEndian(list[4..], (ushort)records.Length);
    for (var i = 0; i < records.Length; ++i) {
      var rec = list.Slice(0x10 + i * 16, 16);
      BinaryPrimitives.WriteUInt32LittleEndian(rec, records[i].Cpos);
      if (depth == 0) {
        BinaryPrimitives.WriteUInt16LittleEndian(rec[4..], (ushort)records[i].Clusters);
        rec[7] = (byte)(records[i].Unwritten ? 1 : 0);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], (uint)records[i].Clusters);
      }
      BinaryPrimitives.WriteUInt64LittleEndian(rec[8..], (ulong)records[i].Blkno);
    }
  }
}
