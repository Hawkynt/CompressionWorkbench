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

  /// <summary>
  /// Deterministic payload large enough to force an extent-backed file (the writer
  /// stores files up to ~3896 bytes inline in the dinode). Used where a test needs
  /// the seeded file to own real data clusters so the in-place modifier — which
  /// operates on extent-backed files — can replace/free them.
  /// </summary>
  private static byte[] Extentful(int length = 5000) {
    var d = new byte[length];
    for (var i = 0; i < length; i++) d[i] = (byte)((i * 7 + 3) & 0xFF);
    return d;
  }

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
  // The global cluster allocation bitmap lives in the global_bitmap group
  // descriptor at block 3 (its bg_bitmap region); the modifier mutates that block.
  private const int BitmapDataBlkno = 3;

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
    // Expected diffs: the cluster bitmap (block 3), root dir (block 5), the new
    // file's dinode block, and its data block.
    Assert.That(diffs, Does.Contain(BitmapDataBlkno), "cluster bitmap block must change");
    Assert.That(diffs, Does.Contain(RootDirBlkno), "root dir dinode block must change");

    // No untouched system blocks should change: reserved (0,1), superblock (2),
    // system dir (6), global_inode_alloc (8).
    Assert.That(diffs, Does.Not.Contain(0));
    Assert.That(diffs, Does.Not.Contain(1));
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(6));
    Assert.That(diffs, Does.Not.Contain(8));
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
    Assert.That(diffs, Does.Contain(BitmapDataBlkno), "cluster bitmap block must change");
    Assert.That(diffs, Does.Contain(RootDirBlkno), "root dir dinode block must change");

    // Untouched system blocks unchanged: reserved (0,1), superblock (2),
    // system dir (6), global_inode_alloc (8).
    Assert.That(diffs, Does.Not.Contain(0));
    Assert.That(diffs, Does.Not.Contain(1));
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(6));
    Assert.That(diffs, Does.Not.Contain(8));

    // keep.txt's dinode must remain byte-identical and still read back.
    var files = ListFiles(after).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "keep.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["keep.txt"]), Is.EqualTo("KEEP-CONTENT"));
  }

  [Test, Category("Performance")]
  public void Remove_UpdatesBitmap_AndFreesBits() {
    // Seed b.txt large enough to be extent-backed (> inline area) so removing it
    // frees both a dinode bit and at least one data cluster bit.
    var big = new byte[5000];
    for (var i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);
    using var ms = BuildImage(
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", big));
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
    // Marker must live in a data cluster (extent-backed file) so wipeData:false
    // leaves it intact — inline files keep their bytes in the dinode, which is
    // always cleared when the inode is freed.
    var marker = "LINGER-MARKER-OCFS2"u8.ToArray();
    var data = Extentful();
    marker.CopyTo(data, 0);
    using var ms = BuildImage(("linger.txt", data));
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "linger.txt", wipeData: false), Is.True);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Contain("LINGER-MARKER-OCFS2"));
  }

  // ── Replace (fits) ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_SameSize_UpdatesContents() {
    var old = Extentful(4096);
    using var ms = BuildImage(("file.txt", old)); // extent-backed (1 cluster)
    var newContent = (byte[])old.Clone();
    for (var i = 0; i < newContent.Length; i++) newContent[i] ^= 0x5A;
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "file.txt", newContent), Is.True);

    var files = ListFiles(ms.ToArray()).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files["file.txt"], Is.EqualTo(newContent));
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
    var keep = Extentful();
    var oldChange = Extentful();
    var newChange = Extentful(); // same length → same-size replace, no alloc/free
    newChange[0] ^= 0xFF;        // make the payload distinct
    using var ms = BuildImage(("keep.txt", keep), ("change.txt", oldChange));
    var before = ms.ToArray();
    Assert.That(Ocfs2InPlaceModifier.ReplaceFile(ms, "change.txt", newChange), Is.True);
    var after = ms.ToArray();

    var diffs = DifferingBlocks(before, after);
    // Bitmap should NOT change on a same-size replace (no alloc/free).
    Assert.That(diffs, Does.Not.Contain(BitmapDataBlkno),
      "Replace within existing extent must not touch the cluster bitmap.");
    // Root dir should NOT change (dirent stays the same).
    Assert.That(diffs, Does.Not.Contain(RootDirBlkno),
      "Replace must not touch the root dir.");
    // System blocks unchanged.
    Assert.That(diffs, Does.Not.Contain(2));
    Assert.That(diffs, Does.Not.Contain(3));

    // keep.txt must still read back byte-identical.
    var files = ListFiles(after).ToDictionary(f => f.Name, f => f.Data);
    Assert.That(files["keep.txt"], Is.EqualTo(keep), "keep.txt must remain byte-identical.");
    Assert.That(files["change.txt"], Is.EqualTo(newChange), "change.txt must hold the new payload.");
  }

  [Test, Category("ErrorHandling")]
  public void Replace_LargerThanAlloc_Throws() {
    using var ms = BuildImage(("small.bin", Extentful(4096))); // exactly 1 cluster allocated
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
      ("beta.txt", Extentful(4096)), // extent-backed so it can be replaced in place
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
