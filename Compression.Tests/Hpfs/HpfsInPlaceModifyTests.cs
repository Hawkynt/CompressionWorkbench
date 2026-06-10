using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Hpfs;

namespace Compression.Tests.Hpfs;

/// <summary>
/// True in-place mutation contract for the HPFS R/W descriptor. Each test
/// asserts the load-bearing property: sectors not touched by the mutation
/// are byte-identical between the pre-image and post-image, the bitmap
/// flips bits in the documented direction, and a round-trip extract recovers
/// the original payload byte-for-byte.
/// </summary>
[TestFixture]
public class HpfsInPlaceModifyTests {

  private const int LbaSize = HpfsReader.LbaSize;          // 512
  private const int DirBlockSize = HpfsReader.DirBlockSize; // 2048

  // Geometry from HpfsWriter (constant for any built image).
  private const uint RootFnodeLba = 18;
  private const uint RootDirLba = 20;
  private const uint BitmapLba = 24;

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new HpfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static bool IsBitmapBitFree(byte[] image, int lba) {
    var off = (int)BitmapLba * LbaSize + lba / 8;
    return (image[off] & (1 << (lba % 8))) != 0;
  }

  /// <summary>
  /// Returns the set of LBAs that differ between two same-sized images.
  /// Compares sector-by-sector — a sector is considered changed if any of
  /// its 512 bytes differ.
  /// </summary>
  private static HashSet<int> ChangedSectors(byte[] before, byte[] after) {
    Assert.That(after, Has.Length.EqualTo(before.Length), "Images differ in size");
    var changed = new HashSet<int>();
    var sectors = before.Length / LbaSize;
    for (var lba = 0; lba < sectors; lba++) {
      var off = lba * LbaSize;
      for (var i = 0; i < LbaSize; i++) {
        if (before[off + i] != after[off + i]) { changed.Add(lba); break; }
      }
    }
    return changed;
  }

  private static ArchiveInputInfo MakeInput(string name, byte[] content) {
    var tmp = Path.GetTempFileName();
    File.WriteAllBytes(tmp, content);
    return new ArchiveInputInfo(tmp, name, false);
  }

  // ── Add: untouched sectors stay byte-identical ───────────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Add_LeavesUntouchedSectorsByteIdentical() {
    // Given an image with one root-level file.
    var original = BuildImage(("FIRST.TXT", "first content"u8.ToArray()));
    var before = (byte[])original.Clone();

    // When a new root-level file is added via the in-place modifier.
    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    var input = MakeInput("SECOND.TXT", "second content"u8.ToArray());
    try {
      HpfsInPlaceModifier.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }
    var after = ms.ToArray();

    // Then only the bitmap (LBA 24), the root DIRBLK (LBA 20..23) and the
    // freshly-allocated new sectors changed; every other sector is byte-identical.
    var changed = ChangedSectors(before, after);
    Assert.That(changed, Does.Contain((int)BitmapLba),
      "Bitmap must change to mark new allocations");
    Assert.That(changed, Does.Contain((int)RootDirLba),
      "Root DIRBLK must change to host the new dirent");

    // The first file lives in the early-allocated sector range (LBA 32 = its
    // fnode, LBA 33 = its data). Those sectors must NOT change.
    Assert.That(changed, Does.Not.Contain(32),
      "Existing file's FNODE must stay byte-identical");
    Assert.That(changed, Does.Not.Contain(33),
      "Existing file's data must stay byte-identical");

    // Superblock (LBA 16), spare block (LBA 17), root fnode (LBA 18) and the
    // boot sector (LBA 0) are pure metadata: also byte-identical.
    Assert.That(changed, Does.Not.Contain(0));
    Assert.That(changed, Does.Not.Contain(16));
    Assert.That(changed, Does.Not.Contain(17));
    Assert.That(changed, Does.Not.Contain((int)RootFnodeLba));
  }

  [Test, Category("HappyPath"), Category("InPlace"), Category("RoundTrip")]
  public void Add_FileExtracts_RoundTrips() {
    var original = BuildImage(("EXISTING.TXT", "old"u8.ToArray()));
    var payload = "freshly added bytes"u8.ToArray();

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    var input = MakeInput("ADDED.TXT", payload);
    try {
      HpfsInPlaceModifier.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("EXISTING.TXT"));
    Assert.That(names, Does.Contain("ADDED.TXT"));

    var added = r.Entries.First(e => e.Name == "ADDED.TXT");
    Assert.That(r.Extract(added), Is.EqualTo(payload));
    var existing = r.Entries.First(e => e.Name == "EXISTING.TXT");
    Assert.That(r.Extract(existing), Is.EqualTo("old"u8.ToArray()));
  }

  // ── Replace (fits in alloc): in-place data rewrite ───────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Replace_SameSize_RewritesDataSectorInPlace() {
    // Given an image with a single-sector file.
    var original = BuildImage(("DATA.BIN", "AAAAAAAAAAAAAAAA"u8.ToArray()));
    var before = (byte[])original.Clone();

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    HpfsInPlaceModifier.Replace(ms, "DATA.BIN", "BBBBBBBBBBBBBBBB"u8.ToArray());
    var after = ms.ToArray();

    var changed = ChangedSectors(before, after);
    // The data sector (LBA 33 — first alloc after fnode at 32) must change.
    Assert.That(changed, Does.Contain(33),
      "Data sector must be rewritten in place");
    // Bitmap must NOT change — same allocation, no new sectors taken.
    Assert.That(changed, Does.Not.Contain((int)BitmapLba),
      "Bitmap must NOT change for a same-size in-place replace");
    // FNODE doesn't move and length doesn't shift — staying byte-identical
    // is fine for an equal-size replace.
    Assert.That(changed, Does.Not.Contain(0));
    Assert.That(changed, Does.Not.Contain(16));
    Assert.That(changed, Does.Not.Contain(17));
    Assert.That(changed, Does.Not.Contain((int)RootFnodeLba));

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    var e = r.Entries.Single();
    Assert.That(r.Extract(e), Is.EqualTo("BBBBBBBBBBBBBBBB"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Replace_SmallerContent_RewritesDataInPlaceAndUpdatesDirentSize() {
    // Given an image with a single-sector file holding 100 bytes.
    var bigPayload = new byte[100];
    for (var i = 0; i < bigPayload.Length; i++) bigPayload[i] = (byte)i;
    var original = BuildImage(("DATA.BIN", bigPayload));

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    var smaller = "small"u8.ToArray();
    HpfsInPlaceModifier.Replace(ms, "DATA.BIN", smaller);

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    var e = r.Entries.Single();
    Assert.That(e.Size, Is.EqualTo(smaller.Length),
      "Dirent size must reflect the new logical content length");
    Assert.That(r.Extract(e), Is.EqualTo(smaller));
  }

  // ── Remove: untouched sectors stay byte-identical, dirent slot reused ────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Remove_LeavesUntouchedSectorsByteIdentical() {
    // Given an image with two root-level files.
    var keep = "keep me"u8.ToArray();
    var del = "delete me"u8.ToArray();
    var original = BuildImage(("KEEP.TXT", keep), ("DELETE.TXT", del));
    var before = (byte[])original.Clone();

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    HpfsInPlaceModifier.Remove(ms, ["DELETE.TXT"]);
    var after = ms.ToArray();

    var changed = ChangedSectors(before, after);
    // Bitmap and root DIRBLK change.
    Assert.That(changed, Does.Contain((int)BitmapLba));
    Assert.That(changed, Does.Contain((int)RootDirLba));

    // KEEP.TXT's fnode + data sectors must stay byte-identical.
    // Layout: writer assigns fnodes/data depth-first sorted by name.
    // After sorting: DELETE.TXT (fnode=32, data=33), KEEP.TXT (fnode=34, data=35).
    Assert.That(changed, Does.Not.Contain(34),
      "Surviving file's FNODE must stay byte-identical");
    Assert.That(changed, Does.Not.Contain(35),
      "Surviving file's data must stay byte-identical");
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Remove_ZerosFreedSectors() {
    var del = "delete me"u8.ToArray();
    var original = BuildImage(("DELETE.TXT", del));

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    HpfsInPlaceModifier.Remove(ms, ["DELETE.TXT"]);
    var after = ms.ToArray();

    // DELETE.TXT's data sector was at LBA 33 — its bytes must now be zero.
    var dataOff = 33 * LbaSize;
    var allZero = true;
    for (var i = 0; i < LbaSize; i++)
      if (after[dataOff + i] != 0) { allZero = false; break; }
    Assert.That(allZero, Is.True, "Freed data sector must be zeroed");

    // The FNODE sector (LBA 32) must also be zero (no fnode magic left).
    var fnodeOff = 32 * LbaSize;
    for (var i = 0; i < 4; i++)
      Assert.That(after[fnodeOff + i], Is.EqualTo(0),
        "Freed FNODE magic must be wiped");
  }

  // ── Bitmap: tracks frees + allocs after mutations ───────────────────────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Bitmap_TracksAllocationsAfterAdd() {
    var original = BuildImage(("INIT.TXT", "init"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;

    var payload = "new content"u8.ToArray();
    var input = MakeInput("NEW.TXT", payload);
    try {
      HpfsInPlaceModifier.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    var after = ms.ToArray();
    // The fresh sectors used by NEW.TXT must now be marked USED (bit=0).
    // Find the freshly-allocated sectors by reading the reader's entries.
    ms.Position = 0;
    using var r = new HpfsReader(ms);
    var e = r.Entries.First(x => x.Name == "NEW.TXT");
    var dataLba = (int)e.DataLba;
    var fnodeLba = (int)e.FnodeLba;

    Assert.That(IsBitmapBitFree(after, dataLba), Is.False,
      $"Bitmap bit for newly-allocated data sector LBA {dataLba} must be USED (0)");
    Assert.That(IsBitmapBitFree(after, fnodeLba), Is.False,
      $"Bitmap bit for newly-allocated FNODE sector LBA {fnodeLba} must be USED (0)");
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Bitmap_TracksFreesAfterRemove() {
    var original = BuildImage(
      ("KEEP.TXT", "keep"u8.ToArray()),
      ("DROP.TXT", "drop"u8.ToArray()));

    // Capture pre-remove bitmap state of DROP.TXT's sectors.
    using var ms = new MemoryStream();
    ms.Write(original);
    ms.Position = 0;
    using (var pre = new HpfsReader(ms)) {
      var dropEntry = pre.Entries.First(x => x.Name == "DROP.TXT");
      Assert.That(IsBitmapBitFree(original, (int)dropEntry.DataLba), Is.False,
        "DROP.TXT's data sector starts allocated");
      Assert.That(IsBitmapBitFree(original, (int)dropEntry.FnodeLba), Is.False,
        "DROP.TXT's FNODE sector starts allocated");

      ms.Position = 0;
      HpfsInPlaceModifier.Remove(ms, ["DROP.TXT"]);

      var after = ms.ToArray();
      Assert.That(IsBitmapBitFree(after, (int)dropEntry.DataLba), Is.True,
        "DROP.TXT's data sector must be free after remove");
      Assert.That(IsBitmapBitFree(after, (int)dropEntry.FnodeLba), Is.True,
        "DROP.TXT's FNODE sector must be free after remove");
    }
  }

  // ── Round-trip: mutate-then-extract preserves everything ─────────────────

  [Test, Category("HappyPath"), Category("InPlace"), Category("RoundTrip")]
  public void Mutate_AddThenExtract_RoundTrip() {
    var initialPayload = "alpha"u8.ToArray();
    var addedPayload = new byte[400];
    for (var i = 0; i < addedPayload.Length; i++) addedPayload[i] = (byte)(i ^ 0x5A);

    using var ms = new MemoryStream(BuildImage(("A.TXT", initialPayload)));
    ms.Position = 0;
    var input = MakeInput("B.BIN", addedPayload);
    try {
      HpfsInPlaceModifier.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["A.TXT"]), Is.EqualTo(initialPayload));
    Assert.That(r.Extract(byName["B.BIN"]), Is.EqualTo(addedPayload));
  }

  [Test, Category("HappyPath"), Category("InPlace"), Category("RoundTrip")]
  public void Mutate_AddThenRemove_RoundTrip() {
    using var ms = new MemoryStream(BuildImage(("KEEP.TXT", "keep"u8.ToArray())));
    ms.Position = 0;
    var added = "added"u8.ToArray();
    var input = MakeInput("TMP.TXT", added);
    try {
      HpfsInPlaceModifier.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    ms.Position = 0;
    HpfsInPlaceModifier.Remove(ms, ["TMP.TXT"]);

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("KEEP.TXT"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("keep"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("InPlace"), Category("RoundTrip")]
  public void Mutate_ReplaceThenExtract_RoundTrip() {
    using var ms = new MemoryStream(BuildImage(("FILE.DAT", "original payload"u8.ToArray())));
    ms.Position = 0;
    var replacement = "replaced"u8.ToArray();
    HpfsInPlaceModifier.Replace(ms, "FILE.DAT", replacement);

    ms.Position = 0;
    using var r = new HpfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(replacement));
  }

  // ── Descriptor integration: Add/Remove delegate to the in-place path ─────

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Descriptor_Add_UsesInPlace_PreservingUntouchedSectors() {
    var original = BuildImage(("FIRST.TXT", "first"u8.ToArray()));
    var before = (byte[])original.Clone();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(original);
    ms.SetLength(original.Length);

    var input = MakeInput("SECOND.TXT", "second"u8.ToArray());
    try {
      d.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    var after = ms.ToArray();
    Assert.That(after, Has.Length.EqualTo(before.Length),
      "In-place Add must preserve total image size");
    var changed = ChangedSectors(before, after);
    // FIRST.TXT's bytes survive untouched.
    Assert.That(changed, Does.Not.Contain(32));
    Assert.That(changed, Does.Not.Contain(33));
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Descriptor_Remove_UsesInPlace_PreservingUntouchedSectors() {
    var original = BuildImage(
      ("KEEP.TXT", "keep"u8.ToArray()),
      ("DROP.TXT", "drop"u8.ToArray()));
    var before = (byte[])original.Clone();

    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(original);
    ms.SetLength(original.Length);
    d.Remove(ms, ["DROP.TXT"]);

    var after = ms.ToArray();
    Assert.That(after, Has.Length.EqualTo(before.Length),
      "In-place Remove must preserve total image size");
    var changed = ChangedSectors(before, after);
    // KEEP.TXT survives at LBA 34/35.
    Assert.That(changed, Does.Not.Contain(34));
    Assert.That(changed, Does.Not.Contain(35));
  }

  // ── Scope guard: subdirectory paths fall back to rebuild via descriptor ──

  [Test, Category("ErrorHandling"), Category("InPlace")]
  public void InPlaceModifier_RejectsSubdirectoryPath() {
    using var ms = new MemoryStream(BuildImage(("X.TXT", "x"u8.ToArray())));
    var input = MakeInput("dir/SUB.TXT", "sub"u8.ToArray());
    try {
      Assert.Throws<NotSupportedException>(() => HpfsInPlaceModifier.Add(ms, [input]));
    } finally {
      File.Delete(input.FullPath);
    }
  }

  [Test, Category("HappyPath"), Category("InPlace")]
  public void Descriptor_SubdirectoryAdd_FallsBackToRebuild() {
    // The descriptor catches NotSupportedException from the in-place path and
    // falls back to the rebuild path so nested paths still work end-to-end.
    var d = new HpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildImage(("ROOT.TXT", "root"u8.ToArray())));
    var input = MakeInput("docs/guide.txt", "guide"u8.ToArray());
    try {
      d.Add(ms, [input]);
    } finally {
      File.Delete(input.FullPath);
    }

    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("ROOT.TXT"));
    Assert.That(names.Any(n => n.Replace('\\', '/').EndsWith("guide.txt")), Is.True,
      "Nested file should be reachable via the rebuild-fallback path");
  }
}
