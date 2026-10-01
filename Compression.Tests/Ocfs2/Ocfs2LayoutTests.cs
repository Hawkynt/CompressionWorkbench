using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Layout rules the Linux kernel and <c>fsck.ocfs2</c> enforce, checked without
/// either: journal sizing, cluster groups, extent splitting, allocator counts,
/// and the refusals that keep an edit lossless. The kernel and fsck themselves
/// run in <see cref="Ocfs2KernelMountTests"/>.
/// </summary>
[TestFixture]
public class Ocfs2LayoutTests {

  private const int BlockSize = 4096;
  private const int JournalDinode = 15;
  private const int GlobalBitmapDinode = 11;
  private const int InodeAllocDinode = 14;

  // ── journal sizing ────────────────────────────────────────────────────

  /// <summary>
  /// Given volume sizes on both sides of each mkfs.ocfs2 tier boundary, when the
  /// journal is sized, then it is 4 MiB below 128 MiB, 16 MiB below 1 GiB and
  /// 64 MiB from there — never below the kernel's OCFS2_MIN_JOURNAL_SIZE.
  /// </summary>
  [TestCase(0L, 1024)]
  [TestCase(128L * 1024 * 1024 - 1, 1024)]
  [TestCase(128L * 1024 * 1024, 4096)]
  [TestCase(1024L * 1024 * 1024 - 1, 4096)]
  [TestCase(1024L * 1024 * 1024, 16384)]
  [TestCase(64L * 1024 * 1024 * 1024, 16384)]
  [Category("Boundary")]
  public void JournalClusters_FollowMkfsTiers(long volumeBytes, int expectedClusters) {
    var clusters = Ocfs2Writer.JournalClustersFor(volumeBytes);
    Assert.That(clusters, Is.EqualTo(expectedClusters));
    Assert.That((long)clusters * BlockSize, Is.GreaterThanOrEqualTo(Ocfs2Writer.MinJournalBytes));
  }

  /// <summary>
  /// Given the smallest volume the writer makes (no files at all), when its
  /// journal is read, then journal:0000 is exactly the 4 MiB kernel minimum and
  /// its JBD2 superblock is clean (s_start = 0) with s_maxlen naming every block.
  /// </summary>
  [Test, Category("Boundary")]
  public void EmptyVolume_JournalIsKernelMinimumAndClean() {
    var image = new Ocfs2Writer().Build();

    var journal = Dinode(image, JournalDinode);
    var size = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(0x20, 8));
    Assert.That(size, Is.EqualTo(Ocfs2Writer.MinJournalBytes), "journal:0000 i_size");

    var first = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(0xC0 + 0x10 + 8, 8));
    var jsb = image.AsSpan((int)(first * BlockSize), BlockSize).ToArray();
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(jsb), Is.EqualTo(0xC03B3998u), "JBD2 magic");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(jsb.AsSpan(0x10)), Is.EqualTo((uint)(size / BlockSize)), "s_maxlen");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(jsb.AsSpan(0x1C)), Is.Zero, "s_start: nothing to replay");
    });
    AssertAllocatorsConsistent(image);
  }

  // ── cluster groups and extents ────────────────────────────────────────

  /// <summary>
  /// Given a file larger than one global-bitmap group (32256 clusters), when the
  /// volume is written, then the bitmap has a second group whose descriptor sits
  /// at cluster 32256, the file's data is split into one extent record per group
  /// with none covering that descriptor, and every allocator count agrees.
  /// </summary>
  [Test, Category("Boundary")]
  public void FileLargerThanAClusterGroup_IsSplitAroundTheGroupDescriptor() {
    const long size = (Ocfs2Writer.ClustersPerGroup + 100L) * BlockSize;
    var path = Path.Combine(Path.GetTempPath(), $"cwb_ocfs2_big_{Guid.NewGuid():N}.img");
    try {
      var w = new Ocfs2Writer();
      w.AddStreamingFile("big.bin", size, () => new PatternStream(size));
      using (var fs = File.Create(path)) w.WriteTo(fs);

      using var image = File.OpenRead(path);
      var bitmap = ReadBlock(image, GlobalBitmapDinode);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bitmap.AsSpan(0xC0 + 6, 2)), Is.EqualTo(2), "two cluster groups, one chain each");
      var group1 = ReadBlock(image, Ocfs2Writer.ClustersPerGroup);
      Assert.That(Encoding.ASCII.GetString(group1, 0, 7), Is.EqualTo("GROUP01"), "group 1's descriptor at cluster 32256");
      Assert.That(group1[0x40] & 1, Is.EqualTo(1), "a group's descriptor is its own first allocated bit");

      var dinode = ReadBlock(image, FindRootEntry(image, "big.bin"));
      var records = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(0xC0 + 4, 2));
      Assert.That(records, Is.EqualTo(2));
      long cpos = 0;
      for (var r = 0; r < records; ++r) {
        var rec = dinode.AsSpan(0xC0 + 0x10 + r * 16, 16);
        var start = BinaryPrimitives.ReadInt64LittleEndian(rec[8..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rec), Is.EqualTo((uint)cpos), $"record {r} e_cpos");
        Assert.That(Ocfs2Writer.ClustersPerGroup, Is.Not.InRange(start, start + count - 1), $"record {r} covers the group descriptor");
        cpos += count;
      }
      Assert.That(cpos * BlockSize, Is.GreaterThanOrEqualTo(size));

      image.Position = 0;
      var extracted = new Ocfs2FormatDescriptor().List(image, null).Single(e => e.Name == "big.bin");
      Assert.That(extracted.OriginalSize, Is.EqualTo(size));
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>
  /// Given more files than one 1024-bit inode group holds, when the volume is
  /// written, then the inode allocator gets a second whole group and every file
  /// still reads back.
  /// </summary>
  [Test, Category("Boundary")]
  public void ManyFiles_SpillIntoASecondWholeInodeGroup() {
    var w = new Ocfs2Writer();
    for (var i = 0; i < 1100; ++i) w.AddFile($"f{i:D4}.txt", Encoding.ASCII.GetBytes($"file {i}"));
    var image = w.Build();

    var alloc = Dinode(image, InodeAllocDinode);
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(alloc.AsSpan(0xC0 + 6, 2)), Is.EqualTo(2), "two inode groups");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(alloc.AsSpan(0xBC, 4)), Is.EqualTo(2 * Ocfs2Writer.InodeGroupBits),
      "every inode group is cl_cpg wide — fsck takes a group to own cl_cpg clusters");
    AssertAllocatorsConsistent(image);
    Assert.That(Ocfs2Reader.ReadFiles(image), Has.Count.EqualTo(1100));
  }

  // ── in-place edits keep the allocators exact ──────────────────────────

  /// <summary>
  /// Given a roomy volume, when files are added (inline and extent-backed),
  /// replaced and removed in place, then every group, chain record and used
  /// count still agrees with the bitmaps.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void InPlaceEdits_KeepEveryAllocatorCountExact() {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(16L * 1024 * 1024);
    w.AddFile("keep.bin", Pattern(20000));
    w.AddFile("drop.bin", Pattern(9000));
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var d = new Ocfs2FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("tiny.txt", "tiny"u8.ToArray()), ArchiveInputInfo.InMemory("large.bin", Pattern(50000))]);
    d.Add(ms, [ArchiveInputInfo.InMemory("keep.bin", Pattern(30000, 3))]);
    d.Remove(ms, ["drop.bin"]);

    AssertAllocatorsConsistent(ms.ToArray());
    var files = Ocfs2Reader.ReadFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "keep.bin", "tiny.txt", "large.bin" }));
    Assert.That(files["keep.bin"], Is.EqualTo(Pattern(30000, 3)));
    Assert.That(files["large.bin"], Is.EqualTo(Pattern(50000)));
  }

  /// <summary>
  /// Given a volume with a hole left by a removed file, when it is defragmented,
  /// then the later file moves down into the hole, reads back unchanged, and the
  /// bitmap follows it exactly.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Defragment_MovesIntoTheHoleAndKeepsTheBitmapExact() {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(16L * 1024 * 1024);
    w.AddFile("first.bin", Pattern(40000));
    w.AddFile("second.bin", Pattern(30000, 7));
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    var d = new Ocfs2FormatDescriptor();
    d.Remove(ms, ["first.bin"]);
    var before = Ocfs2Reader.ReadFilePlacements(ms.ToArray()).Single().DataBlkno;

    d.Defragment(ms);

    var after = Ocfs2Reader.ReadFilePlacements(ms.ToArray()).Single();
    Assert.That(after.DataBlkno, Is.LessThan(before), "second.bin moved down into the hole");
    Assert.That(Ocfs2Reader.ReadFiles(ms.ToArray()).Single().Data, Is.EqualTo(Pattern(30000, 7)));
    AssertAllocatorsConsistent(ms.ToArray());
  }

  // ── refusals leave the image untouched ────────────────────────────────

  /// <summary>
  /// Given a volume, when an edit outside the in-place scope is asked for — a
  /// path through a file, removing a directory, an add the volume has no room for — then it is
  /// refused with NotSupportedException and the image is byte for byte unchanged.
  /// </summary>
  [TestCase("add-through-file")]
  [TestCase("remove-directory")]
  [TestCase("add-too-big")]
  [Category("ErrorHandling")]
  public void OutOfScopeEdit_IsRefusedAndLeavesTheImageUnchanged(string edit) {
    var w = new Ocfs2Writer();
    w.AddFile("docs/guide.txt", "guide"u8.ToArray());
    w.AddFile("root.txt", "root"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());
    var before = ms.ToArray();
    var d = new Ocfs2FormatDescriptor();

    Assert.Throws<NotSupportedException>(() => {
      switch (edit) {
        case "add-through-file": d.Add(ms, [ArchiveInputInfo.InMemory("root.txt/new.txt", "x"u8.ToArray())]); break;
        case "remove-directory": d.Remove(ms, ["docs"]); break;
        case "add-too-big": d.Add(ms, [ArchiveInputInfo.InMemory("huge.bin", new byte[64 * 1024 * 1024])]); break;
      }
    });
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  /// <summary>
  /// Given an input tree with nested paths, when it is created through the
  /// descriptor, then the directories are kept rather than flattened to leaf names.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void DescriptorCreate_KeepsNestedPaths() {
    using var ms = new MemoryStream();
    new Ocfs2FormatDescriptor().Create(ms, [
      ArchiveInputInfo.InMemory("a/readme.txt", "one"u8.ToArray()),
      ArchiveInputInfo.InMemory("b/readme.txt", "two"u8.ToArray()),
    ], new FormatCreateOptions());
    var files = Ocfs2Reader.ReadFiles(ms.ToArray()).ToDictionary(f => f.Name, f => Encoding.ASCII.GetString(f.Data));
    Assert.That(files, Is.EquivalentTo(new Dictionary<string, string> { ["a/readme.txt"] = "one", ["b/readme.txt"] = "two" }));
  }

  /// <summary>
  /// Given the ImageSize option, when a volume is created, then it is that large
  /// (free space for later in-place adds), and an "Auto" request fits the files.
  /// </summary>
  [TestCase("32 MB", 32L * 1024 * 1024)]
  [TestCase("Auto (fit to files)", -1L)]
  [Category("HappyPath")]
  public void DescriptorCreate_HonoursImageSize(string option, long expected) {
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["ImageSize"] = option } };
    new Ocfs2FormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", "a"u8.ToArray())], options);
    if (expected > 0) Assert.That(ms.Length, Is.EqualTo(expected));
    else Assert.That(ms.Length, Is.LessThan(16L * 1024 * 1024));
    AssertAllocatorsConsistent(ms.ToArray());
  }

  // ── helpers ───────────────────────────────────────────────────────────

  internal static byte[] Pattern(int length, int seed = 1) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed + (i >> 12));
    return data;
  }

  private static byte[] Dinode(byte[] image, long blkno) => image.AsSpan((int)(blkno * BlockSize), BlockSize).ToArray();

  private static byte[] ReadBlock(Stream image, long blkno) {
    var block = new byte[BlockSize];
    image.Position = blkno * BlockSize;
    image.ReadExactly(block);
    return block;
  }

  private static long FindRootEntry(Stream image, string name) {
    var root = ReadBlock(image, 5);
    var want = Encoding.ASCII.GetBytes(name);
    for (var at = 0xC8; at + 12 <= BlockSize;) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(at + 8, 2));
      if (root[at + 10] == want.Length && root.AsSpan(at + 12, want.Length).SequenceEqual(want))
        return BinaryPrimitives.ReadInt64LittleEndian(root.AsSpan(at, 8));
      if (recLen < 12) break;
      at += recLen;
    }
    throw new AssertionException($"'{name}' is not in the root directory.");
  }

  /// <summary>
  /// The invariant fsck.ocfs2 and the kernel check on every chain allocator: a
  /// group's free count is the zeros in its bitmap, a chain record's free and
  /// total are its groups' sums, and the dinode's used/total are the whole.
  /// </summary>
  internal static void AssertAllocatorsConsistent(byte[] image) {
    foreach (var allocator in new long[] { GlobalBitmapDinode, 8, InodeAllocDinode }) {
      var dinode = Dinode(image, allocator);
      var chains = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(0xC0 + 6, 2));
      long used = 0, total = 0;
      for (var c = 0; c < chains; ++c) {
        var rec = 0xC0 + 0x10 + c * 16;
        long chainFree = 0, chainTotal = 0;
        for (var g = BinaryPrimitives.ReadInt64LittleEndian(dinode.AsSpan(rec + 8, 8)); g != 0;) {
          var group = Dinode(image, g);
          Assert.That(Encoding.ASCII.GetString(group, 0, 7), Is.EqualTo("GROUP01"), $"allocator {allocator} group {g}");
          var bits = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(0x0A, 2));
          var zeros = 0;
          for (var i = 0; i < bits; ++i) if ((group[0x40 + (i >> 3)] & (1 << (i & 7))) == 0) ++zeros;
          Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(0x0C, 2)), Is.EqualTo(zeros),
            $"allocator {allocator} group {g}: bg_free_bits_count");
          Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(0x0E, 2)), Is.EqualTo(c), $"allocator {allocator} group {g}: bg_chain");
          chainFree += zeros;
          chainTotal += bits;
          g = BinaryPrimitives.ReadInt64LittleEndian(group.AsSpan(0x18, 8));
        }
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec, 4)), Is.EqualTo(chainFree), $"allocator {allocator} chain {c}: c_free");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec + 4, 4)), Is.EqualTo(chainTotal), $"allocator {allocator} chain {c}: c_total");
        used += chainTotal - chainFree;
        total += chainTotal;
      }
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(0xB8, 4)), Is.EqualTo(used), $"allocator {allocator}: i_used");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(0xBC, 4)), Is.EqualTo(total), $"allocator {allocator}: i_total");
    }
  }

  /// <summary>A read-only stream of <see cref="Pattern"/>-like bytes that never holds them all.</summary>
  private sealed class PatternStream(long length) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => this._position; set => this._position = value; }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = (int)Math.Min(count, length - this._position);
      for (var i = 0; i < n; ++i) {
        var p = this._position + i;
        buffer[offset + i] = (byte)(p * 31 + 1 + (p >> 12));
      }
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
      SeekOrigin.Begin => offset, SeekOrigin.Current => this._position + offset, _ => length + offset,
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
