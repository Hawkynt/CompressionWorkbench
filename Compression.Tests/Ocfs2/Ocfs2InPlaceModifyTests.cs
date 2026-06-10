#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Unit tests for <see cref="Ocfs2InPlaceModifier"/>'s in-flight Add / Remove /
/// Replace path — verifies true random-access I/O against an existing OCFS2 image:
/// only the global bitmap data block, the root directory dinode, the affected
/// file dinode block, and the file's data blocks should change. Untouched
/// blocks must remain byte-identical to the pre-modification image.
/// </summary>
[TestFixture]
public class Ocfs2InPlaceModifyTests {

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>Builds a writer-produced OCFS2 image seeded with the given files.</summary>
  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new Ocfs2Writer();
    foreach (var (name, data) in files) w.AddFile(name, data);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;
    return ms;
  }

  private const int BlockSize = 4096;
  private const int RootDirBlkno = 5;
  private const int BitmapDataBlkno = 4;
  private const int FirstFileBlkno = 8;

  /// <summary>Returns the set of block indices whose contents differ between two images.</summary>
  private static HashSet<int> DifferingBlocks(byte[] before, byte[] after) {
    var diffs = new HashSet<int>();
    var maxBlocks = Math.Max(before.Length, after.Length) / BlockSize;
    for (var b = 0; b < maxBlocks; b++) {
      var off = b * BlockSize;
      var len = BlockSize;
      var beforeEmpty = off + len > before.Length;
      var afterEmpty = off + len > after.Length;
      if (beforeEmpty != afterEmpty) { diffs.Add(b); continue; }
      if (beforeEmpty) continue;
      if (!before.AsSpan(off, len).SequenceEqual(after.AsSpan(off, len)))
        diffs.Add(b);
    }
    return diffs;
  }

  private static List<(string Name, byte[] Data)> ListFiles(byte[] image) {
    var d = new Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, null);
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_inplace_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);
      var result = new List<(string, byte[])>();
      foreach (var e in entries) {
        var p = Path.Combine(outDir, e.Name);
        if (!File.Exists(p)) continue;
        if (e.Name is "FULL.ocfs2" or "metadata.ini" or "superblock.bin") continue;
        result.Add((e.Name, File.ReadAllBytes(p)));
      }
      return result;
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Add ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_NewFile_ReadsBack() {
    using var ms = BuildImage(("seed.txt", "SEED-CONTENT"u8.ToArray()));
    Ocfs2InPlaceModifier.AddFile(ms, "added.txt", "ADDED-CONTENT"u8.ToArray());

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["seed.txt"]), Is.EqualTo("SEED-CONTENT"));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["added.txt"]), Is.EqualTo("ADDED-CONTENT"));
  }

  [Test, Category("Performance")]
  public void Add_LeavesUntouchedBlocksByteIdentical() {
    using var ms = BuildImage(("a.txt", "AAA"u8.ToArray()), ("b.txt", "BBB"u8.ToArray()));
    var before = ms.ToArray();
    Ocfs2InPlaceModifier.AddFile(ms, "c.txt", "CCC"u8.ToArray());
    var after = ms.ToArray();

    var diffs = DifferingBlocks(before, after);
    // Expected diffs: bitmap (block 4), root dir (block 5), new dinode block,
    // new data block. That's 4 changes for a small file in one cluster.
    Assert.That(diffs, Does.Contain(BitmapDataBlkno), "bitmap data block must change");
    Assert.That(diffs, Does.Contain(RootDirBlkno), "root dir dinode block must change");

    // No system blocks should change: superblock (2), global_bitmap dinode (3),
    // system dir (6), inode alloc (7).
    Assert.That(diffs, Does.Not.Contain(0));
    Assert.That(diffs, Does.Not.Contain(1));
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(3));
    Assert.That(diffs, Does.Not.Contain(6));
    Assert.That(diffs, Does.Not.Contain(7));

    // Existing file dinodes + data blocks (8, 9, 10, 11 for two seeded files) must not change.
    for (var b = FirstFileBlkno; b < FirstFileBlkno + 4; b++)
      Assert.That(diffs, Does.Not.Contain(b), $"seeded block {b} should remain byte-identical");
  }

  [Test, Category("Performance")]
  public void Add_UpdatesBitmap() {
    using var ms = BuildImage(("a.txt", "AAA"u8.ToArray()));
    var beforeBitmap = ReadBlock(ms, BitmapDataBlkno);
    Ocfs2InPlaceModifier.AddFile(ms, "b.txt", "BBB"u8.ToArray());
    var afterBitmap = ReadBlock(ms, BitmapDataBlkno);
    Assert.That(afterBitmap, Is.Not.EqualTo(beforeBitmap), "bitmap must change after Add");

    // Bits that were set in `before` must still be set in `after` (no spurious frees).
    for (var bit = 0; bit < beforeBitmap.Length * 8; bit++)
      if ((beforeBitmap[bit / 8] & (1 << (bit % 8))) != 0)
        Assert.That(afterBitmap[bit / 8] & (1 << (bit % 8)), Is.Not.Zero,
          $"bitmap bit {bit} was set before Add but cleared after — spurious free.");
  }

  [Test, Category("HappyPath")]
  public void Add_MultiClusterFile_ReadsBack() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    var bigData = new byte[3 * 4096 + 100]; // spans 4 clusters
    for (var i = 0; i < bigData.Length; i++) bigData[i] = (byte)((i * 7) & 0xFF);
    Ocfs2InPlaceModifier.AddFile(ms, "big.bin", bigData);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files["big.bin"], Is.EqualTo(bigData));
  }

  [Test, Category("ErrorHandling")]
  public void Add_DuplicateName_Throws() {
    using var ms = BuildImage(("dup.txt", "FIRST"u8.ToArray()));
    Assert.Throws<IOException>(() => Ocfs2InPlaceModifier.AddFile(ms, "dup.txt", "SECOND"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Add_SubdirPath_ThrowsNotSupported() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Assert.Throws<NotSupportedException>(() => Ocfs2InPlaceModifier.AddFile(ms, "sub/file.txt", "DATA"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Add_EmptyName_Throws() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Assert.Throws<ArgumentException>(() => Ocfs2InPlaceModifier.AddFile(ms, "", "DATA"u8.ToArray()));
  }

  // ── Remove ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_RemovesEntry() {
    using var ms = BuildImage(
      ("keep.txt", "KEEP"u8.ToArray()),
      ("drop.txt", "DROP"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "drop.txt"), Is.True);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "keep.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["keep.txt"]), Is.EqualTo("KEEP"));
  }

  [Test, Category("Performance")]
  public void Remove_LeavesUntouchedBlocksByteIdentical() {
    using var ms = BuildImage(
      ("keep.txt", "KEEP-CONTENT"u8.ToArray()),
      ("drop.txt", "DROP-CONTENT"u8.ToArray()));
    var before = ms.ToArray();
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "drop.txt"), Is.True);
    var after = ms.ToArray();

    var diffs = DifferingBlocks(before, after);
    Assert.That(diffs, Does.Contain(BitmapDataBlkno), "bitmap data block must change");
    Assert.That(diffs, Does.Contain(RootDirBlkno), "root dir dinode block must change");

    // System blocks unchanged.
    Assert.That(diffs, Does.Not.Contain(0));
    Assert.That(diffs, Does.Not.Contain(1));
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(3));
    Assert.That(diffs, Does.Not.Contain(6));
    Assert.That(diffs, Does.Not.Contain(7));

    // Writer lays out: block 8 = keep.txt dinode, block 9 = drop.txt dinode,
    // block 10 = keep.txt data, block 11 = drop.txt data.
    // After removing drop.txt: blocks 9 + 11 change (freed + zero-wiped); keep.txt's
    // dinode (8) + data (10) must NOT change.
    Assert.That(diffs, Does.Not.Contain(8), "keep.txt's dinode block must remain byte-identical");
    Assert.That(diffs, Does.Not.Contain(10), "keep.txt's data block must remain byte-identical");
  }

  [Test, Category("Performance")]
  public void Remove_UpdatesBitmap_AndFreesBits() {
    using var ms = BuildImage(
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", "BBB"u8.ToArray()));
    var beforeBitmap = ReadBlock(ms, BitmapDataBlkno);
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "b.txt"), Is.True);
    var afterBitmap = ReadBlock(ms, BitmapDataBlkno);

    Assert.That(afterBitmap, Is.Not.EqualTo(beforeBitmap), "bitmap must change after Remove");

    // Some bits set before must now be cleared (freed dinode + data clusters).
    var freedBits = 0;
    for (var bit = 0; bit < beforeBitmap.Length * 8; bit++) {
      var was = (beforeBitmap[bit / 8] & (1 << (bit % 8))) != 0;
      var now = (afterBitmap[bit / 8] & (1 << (bit % 8))) != 0;
      if (was && !now) freedBits++;
    }
    Assert.That(freedBits, Is.GreaterThanOrEqualTo(2),
      $"expected ≥2 freed bits (dinode + data cluster); freed {freedBits}");
  }

  [Test, Category("HappyPath")]
  public void Remove_NotFound_ReturnsFalse() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesDataBytes() {
    using var ms = BuildImage(("secret.txt", "TOPSECRET-MARKER-OCFS2"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "secret.txt", wipeData: true), Is.True);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-OCFS2"));
  }

  [Test, Category("HappyPath")]
  public void Remove_NoWipe_LeavesDataBytes() {
    using var ms = BuildImage(("linger.txt", "LINGER-MARKER-OCFS2"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "linger.txt", wipeData: false), Is.True);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Contain("LINGER-MARKER-OCFS2"));
  }

  // ── Replace (fits) ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_SameSize_UpdatesContents() {
    using var ms = BuildImage(("file.txt", "OLD-CONTENT-EXACT"u8.ToArray()));
    var newContent = "NEW-CONTENT-EXACT"u8.ToArray();
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "file.txt", newContent), Is.True);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(System.Text.Encoding.UTF8.GetString(files["file.txt"]), Is.EqualTo("NEW-CONTENT-EXACT"));
  }

  [Test, Category("HappyPath")]
  public void Replace_Smaller_UpdatesContents_AndSize() {
    var initial = new byte[4096]; // 1 full cluster
    for (var i = 0; i < initial.Length; i++) initial[i] = (byte)((i * 11) & 0xFF);
    using var ms = BuildImage(("file.bin", initial));

    var newData = "TINY"u8.ToArray();
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "file.bin", newData), Is.True);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files["file.bin"], Is.EqualTo(newData), "data should match the new payload (truncated to i_size)");
  }

  [Test, Category("Performance")]
  public void Replace_LeavesUntouchedBlocksByteIdentical() {
    using var ms = BuildImage(
      ("keep.txt", "KEEP"u8.ToArray()),
      ("change.txt", "OLD-CHANGE-DATA"u8.ToArray()));
    var before = ms.ToArray();
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "change.txt", "NEW-CHANGE-DATA"u8.ToArray()), Is.True);
    var after = ms.ToArray();

    var diffs = DifferingBlocks(before, after);
    // Bitmap should NOT change on a same-size replace (no alloc/free).
    Assert.That(diffs, Does.Not.Contain(BitmapDataBlkno),
      "Replace within existing extent must not touch the bitmap.");
    // Root dir should NOT change (dirent stays the same).
    Assert.That(diffs, Does.Not.Contain(RootDirBlkno),
      "Replace must not touch the root dir.");
    // System blocks unchanged.
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(3));

    // Writer lays out: block 8 = keep.txt dinode, block 9 = change.txt dinode,
    // block 10 = keep.txt data, block 11 = change.txt data.
    // Replace touches change.txt's data (block 11) and updates i_size in its
    // dinode (block 9). keep.txt's dinode + data must remain byte-identical.
    Assert.That(diffs, Does.Not.Contain(8), "keep.txt's dinode block must remain byte-identical");
    Assert.That(diffs, Does.Not.Contain(10), "keep.txt's data block must remain byte-identical");
  }

  [Test, Category("ErrorHandling")]
  public void Replace_LargerThanAlloc_Throws() {
    using var ms = BuildImage(("small.bin", "tiny"u8.ToArray())); // 1 cluster allocated
    var huge = new byte[2 * 4096 + 1]; // needs 3 clusters
    Assert.Throws<IOException>(() => Ocfs2InPlaceModifier.ReplaceFile(ms, "small.bin", huge));
  }

  [Test, Category("HappyPath")]
  public void Replace_NotFound_ReturnsFalse() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "ghost.txt", "X"u8.ToArray()), Is.False);
  }

  // ── Mutate-then-extract roundtrip ──────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateRoundTrip_AddRemoveReplace_AllReadBack() {
    using var ms = BuildImage(
      ("alpha.txt", "ALPHA"u8.ToArray()),
      ("beta.txt", "BETA"u8.ToArray()),
      ("gamma.txt", "GAMMA"u8.ToArray()));

    // Add a new file.
    Ocfs2InPlaceModifier.AddFile(ms, "delta.txt", "DELTA"u8.ToArray());
    // Replace one of the originals (fits in original 1-cluster allocation).
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "beta.txt", "BETA-V2"u8.ToArray()), Is.True);
    // Remove one of the originals.
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "gamma.txt"), Is.True);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "alpha.txt", "beta.txt", "delta.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["alpha.txt"]), Is.EqualTo("ALPHA"));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["beta.txt"]), Is.EqualTo("BETA-V2"));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["delta.txt"]), Is.EqualTo("DELTA"));
  }

  [Test, Category("RoundTrip")]
  public void MutateRoundTrip_RemoveAddSameName_ReclaimsClusters() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "seed.txt"), Is.True);
    Ocfs2InPlaceModifier.AddFile(ms, "seed.txt", "RE-ADDED"u8.ToArray());

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(System.Text.Encoding.UTF8.GetString(files["seed.txt"]), Is.EqualTo("RE-ADDED"));
  }

  // ── Descriptor-routing tests ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    var before = ms.ToArray();

    var d = new Ocfs2FormatDescriptor();
    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("added.txt", "ADDED"u8.ToArray())]);

    // Verify the in-place path (root dir block 5 must change, system blocks must not).
    var after = ms.ToArray();
    var diffs = DifferingBlocks(before, after);
    Assert.That(diffs, Does.Contain(RootDirBlkno),
      "Descriptor.Add should route through in-place modifier and touch the root dir block.");
    Assert.That(diffs, Does.Not.Contain(2), "Descriptor.Add should NOT touch the superblock.");

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_RemoveViaInterface_UsesInPlacePath() {
    using var ms = BuildImage(
      ("keep.txt", "KEEP"u8.ToArray()),
      ("drop.txt", "DROP"u8.ToArray()));
    var before = ms.ToArray();

    var d = new Ocfs2FormatDescriptor();
    ((IArchiveModifiable)d).Remove(ms, ["drop.txt"]);

    var after = ms.ToArray();
    var diffs = DifferingBlocks(before, after);
    Assert.That(diffs, Does.Contain(RootDirBlkno),
      "Descriptor.Remove should route through in-place modifier and touch the root dir block.");
    Assert.That(diffs, Does.Not.Contain(2), "Descriptor.Remove should NOT touch the superblock.");

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "keep.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddReplacesExistingByName_ViaInPlacePath() {
    using var ms = BuildImage(("dup.txt", "OLD"u8.ToArray()));

    var d = new Ocfs2FormatDescriptor();
    ((IArchiveModifiable)d).Add(ms, [ArchiveInputInfo.InMemory("dup.txt", "NEW"u8.ToArray())]);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "dup.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["dup.txt"]), Is.EqualTo("NEW"));
  }

  // ── Edge cases / invariants ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_EmptyFile_Works() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    Ocfs2InPlaceModifier.AddFile(ms, "empty.txt", []);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Does.Contain("empty.txt"));
    Assert.That(files["empty.txt"], Is.EqualTo(Array.Empty<byte>()));
  }

  [Test, Category("ErrorHandling")]
  public void Add_ToInvalidImage_Throws() {
    var garbage = new byte[64 * 1024];
    using var ms = new MemoryStream(garbage);
    Assert.Throws<InvalidDataException>(() => Ocfs2InPlaceModifier.AddFile(ms, "x.txt", "X"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Remove_NonSeekableStream_Throws() {
    using var ms = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    using var nonSeek = new NonSeekableStream(ms);
    Assert.Throws<ArgumentException>(() => Ocfs2InPlaceModifier.RemoveFile(nonSeek, "seed.txt"));
  }

  // ── Test infra ────────────────────────────────────────────────────────

  private static byte[] ReadBlock(Stream image, long blkno) {
    var saved = image.Position;
    try {
      image.Position = blkno * BlockSize;
      var buf = new byte[BlockSize];
      image.ReadExactly(buf);
      return buf;
    } finally {
      image.Position = saved;
    }
  }

  /// <summary>Wrapper hiding CanSeek to verify the modifier rejects non-seekable streams.</summary>
  private sealed class NonSeekableStream : Stream {
    private readonly Stream _inner;
    public NonSeekableStream(Stream inner) { _inner = inner; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
  }
}
