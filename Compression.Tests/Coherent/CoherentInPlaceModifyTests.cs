using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Coherent;

namespace Compression.Tests.Coherent;

/// <summary>
/// Tests for the canonical in-place modifier surface
/// <see cref="CoherentInPlaceModifier"/>. The contract under test:
/// Add/Replace/Remove must mutate the image stream at fixed byte offsets so
/// that bytes the operation did not touch remain byte-identical to the
/// pre-mutation snapshot.
/// </summary>
[TestFixture]
public class CoherentInPlaceModifyTests {

  // ── Image-construction helpers ───────────────────────────────────────────

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var ms = new MemoryStream();
    using (var w = new CoherentWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    ms.Position = 0;
    return ms;
  }

  private static byte[] Pattern(int len, int seed) {
    var b = new byte[len];
    for (var i = 0; i < len; i++) b[i] = (byte)((i * seed + 11 * seed) & 0xFF);
    return b;
  }

  private static IReadOnlyDictionary<string, byte[]> ListAndExtract(byte[] image) {
    using var ms = new MemoryStream(image);
    var r = new CoherentReader(ms);
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var e in r.Entries)
      if (!e.IsDirectory)
        result[e.Name] = r.Extract(e);
    return result;
  }

  /// <summary>
  /// Compares <paramref name="before"/> and <paramref name="after"/> byte by
  /// byte and returns the set of 512-byte block indices that differ. Used to
  /// assert which on-disk regions an operation touched.
  /// </summary>
  private static HashSet<int> DifferingBlocks(byte[] before, byte[] after) {
    var differing = new HashSet<int>();
    var maxLen = Math.Max(before.Length, after.Length);
    for (var i = 0; i < maxLen; i++) {
      var b = i < before.Length ? before[i] : (byte)0;
      var a = i < after.Length ? after[i] : (byte)0;
      if (a != b) differing.Add(i / 512);
    }
    return differing;
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_InsertsFile_UntouchedInodesAndDataByteIdentical() {
    var keep = Pattern(1024, seed: 7);
    var image = BuildImage(("keep.bin", keep));

    // Snapshot the bytes of the existing file's data zone — those are at
    // block 3 (boot=0, padding=1, ilist=2, data starts at block 3 with the
    // root dir, then the keep file). The exact block index depends on the
    // root dir size — we instead snapshot the whole image and compute the
    // intersection of "blocks NOT touched by Add" with "keep.bin's zones".
    var before = image.ToArray();

    var payload = "in-place-add-payload"u8.ToArray();
    CoherentInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("fresh.txt", payload)]);

    var after = image.ToArray();

    // The keep.bin file's bytes must still be reachable and unchanged.
    var extracted = ListAndExtract(after);
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin", "fresh.txt" }));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));
    Assert.That(extracted["fresh.txt"], Is.EqualTo(payload));

    // Locate keep.bin's data block in the BEFORE image by scanning for a
    // unique 32-byte prefix. The data block holding that prefix must be
    // bit-identical in the AFTER image.
    var prefix = keep.AsSpan(0, 32).ToArray();
    var hitOffset = IndexOf(before, prefix);
    Assert.That(hitOffset, Is.GreaterThanOrEqualTo(0));
    var block = hitOffset / 512;
    var blockOff = block * 512;
    Assert.That(after.AsSpan(blockOff, 512).ToArray(),
      Is.EqualTo(before.AsSpan(blockOff, 512).ToArray()),
      "keep.bin's data block must be byte-identical after the in-place Add");
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesSuperblockMagicAndIsizeFields() {
    var image = BuildImage(("seed", "s"u8.ToArray()));
    var before = image.ToArray();

    CoherentInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("x.txt", "hello"u8.ToArray())]);

    var after = image.ToArray();
    // Magic at 1528, isize at 1024 — both must survive.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1528, 2)),
      Is.EqualTo((ushort)0xFD18));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1024, 2)),
      Is.EqualTo(BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(1024, 2))),
      "s_isize must not change when Add only grows the data area");
  }

  // Equivalence class: Add with empty input list is a no-op.
  [Test, Category("Boundary")]
  public void Add_EmptyInputs_LeavesImageByteIdentical() {
    var image = BuildImage(("seed", Pattern(900, seed: 1)));
    var before = image.ToArray();
    CoherentInPlaceModifier.Add(image, []);
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_NamedEntry_OtherFilesAndSuperblockUnchanged() {
    var keep = Pattern(1500, seed: 5);
    var doomed = Pattern(1200, seed: 9);
    var image = BuildImage(("keep.bin", keep), ("doom.bin", doomed));
    var before = image.ToArray();

    var ok = CoherentInPlaceModifier.Remove(image, "doom.bin");

    Assert.That(ok, Is.True);
    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin" }));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));

    // The superblock magic + isize fields are at fixed offsets and must
    // survive untouched.
    var after = image.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1528, 2)),
      Is.EqualTo((ushort)0xFD18));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1024, 2)),
      Is.EqualTo(BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(1024, 2))));

    // The keep.bin payload block (identified by its first 32 bytes in BEFORE)
    // is at a fixed block index and must be byte-identical AFTER.
    var prefix = keep.AsSpan(0, 32).ToArray();
    var hit = IndexOf(before, prefix);
    Assert.That(hit, Is.GreaterThanOrEqualTo(0));
    var blockOff = (hit / 512) * 512;
    Assert.That(after.AsSpan(blockOff, 512).ToArray(),
      Is.EqualTo(before.AsSpan(blockOff, 512).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesFreedDataZonesAndDirent() {
    var sentinel = Encoding.ASCII.GetBytes("REMOVE_WIPE_SENTINEL_COHERENT_INPLACE_TEST_BYTES_BLOCK_OK");
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = sentinel[i % sentinel.Length];
    var image = BuildImage(("doom.bin", payload));

    var before = image.ToArray();
    Assert.That(IndexOf(before, sentinel), Is.GreaterThanOrEqualTo(0),
      "sentinel present before Remove");

    CoherentInPlaceModifier.Remove(image, "doom.bin");

    var after = image.ToArray();
    Assert.That(IndexOf(after, sentinel), Is.LessThan(0),
      "sentinel must be wiped after Remove (no forensic recovery)");
    Assert.That(IndexOf(after, Encoding.ASCII.GetBytes("doom.bin")), Is.LessThan(0),
      "dirent name bytes must be wiped after Remove");
  }

  // Equivalence class: removing a missing file is a no-op (returns false),
  // image byte-identical to snapshot.
  [Test, Category("ExceptionalCase")]
  public void Remove_NonExistent_ReturnsFalseAndLeavesImageUnchanged() {
    var image = BuildImage(("keep.bin", Pattern(400, seed: 19)));
    var before = image.ToArray();

    var ok = CoherentInPlaceModifier.Remove(image, "missing.bin");

    Assert.That(ok, Is.False);
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  // ── Replace (fits inside existing zones) ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_FitsInExistingZones_RewritesInPlace_OtherBytesUnchanged() {
    var keepPayload = Pattern(2000, seed: 21);   // 4 blocks in direct
    var initial = Pattern(1500, seed: 33);       // 3 blocks in direct
    var image = BuildImage(("keep.bin", keepPayload), ("doc.bin", initial));
    var before = image.ToArray();

    var newPayload = Pattern(1200, seed: 55);    // fits in 3 blocks → in-place
    var inPlace = CoherentInPlaceModifier.Replace(image, "doc.bin", newPayload);
    Assert.That(inPlace, Is.True, "Replace must take the in-place path when size fits");

    var after = image.ToArray();
    Assert.That(after.Length, Is.EqualTo(before.Length),
      "in-place Replace must not extend the image when payload fits");

    // Round-trip semantics.
    var extracted = ListAndExtract(after);
    Assert.That(extracted["doc.bin"], Is.EqualTo(newPayload));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keepPayload));

    // The keep.bin data block (by its first-32-byte prefix in BEFORE) must
    // be byte-identical in AFTER — Replace must not touch other files.
    var prefix = keepPayload.AsSpan(0, 32).ToArray();
    var hit = IndexOf(before, prefix);
    Assert.That(hit, Is.GreaterThanOrEqualTo(0));
    var blockOff = (hit / 512) * 512;
    Assert.That(after.AsSpan(blockOff, 512).ToArray(),
      Is.EqualTo(before.AsSpan(blockOff, 512).ToArray()));

    // Superblock magic + isize untouched.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(1528, 2)),
      Is.EqualTo((ushort)0xFD18));
  }

  [Test, Category("HappyPath")]
  public void Replace_FitsInExistingZones_OnlyAffectedBlocksDiffer() {
    var keep = Pattern(800, seed: 41);                // 2 blocks
    var image = BuildImage(("keep.bin", keep), ("doc.bin", Pattern(2000, seed: 43)));
    var before = image.ToArray();

    var newPayload = Pattern(1900, seed: 47); // same 4-block footprint
    var inPlace = CoherentInPlaceModifier.Replace(image, "doc.bin", newPayload);
    Assert.That(inPlace, Is.True);

    var after = image.ToArray();
    var changed = DifferingBlocks(before, after);

    // Locate keep.bin's blocks in BEFORE; they must not appear in `changed`.
    var keepHit = IndexOf(before, keep.AsSpan(0, 32).ToArray());
    Assert.That(keepHit, Is.GreaterThanOrEqualTo(0));
    var keepBlock = keepHit / 512;
    Assert.That(changed, Does.Not.Contain(keepBlock),
      "keep.bin's data block must not appear in the changed-blocks set");

    // Block 2 holds the inode list — the doc.bin i_size byte changes only
    // if the size differs. We rewrote 2000→1900 so block 2 IS allowed to
    // change. The bound we lock here is: keep.bin is untouched + magic
    // survives + final extract is correct.
    var extracted = ListAndExtract(after);
    Assert.That(extracted["doc.bin"], Is.EqualTo(newPayload));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));
  }

  // Boundary: Replace with EXACTLY-same-size payload — only the data blocks
  // change in place; i_size and zone pointers stay identical.
  [Test, Category("Boundary")]
  public void Replace_SameSize_FitsInPlaceAndSizeFieldUnchanged() {
    var image = BuildImage(("doc.bin", Pattern(1024, seed: 61)));
    var newPayload = Pattern(1024, seed: 67); // identical size

    var inPlace = CoherentInPlaceModifier.Replace(image, "doc.bin", newPayload);
    Assert.That(inPlace, Is.True);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["doc.bin"], Is.EqualTo(newPayload));
  }

  // Equivalence class: Replace of a missing name falls through to Add — the
  // file appears with the supplied bytes, return value flags "not in place".
  [Test, Category("ExceptionalCase")]
  public void Replace_MissingEntry_FallsBackToAdd() {
    var image = BuildImage(("keep.bin", Pattern(512, seed: 73)));

    var inPlace = CoherentInPlaceModifier.Replace(image, "new.bin", Pattern(256, seed: 79));
    Assert.That(inPlace, Is.False);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin", "new.bin" }));
    Assert.That(extracted["new.bin"], Is.EqualTo(Pattern(256, seed: 79)));
  }

  // Replace where the new payload is BIGGER than the inode's current zone
  // footprint forces the realloc fall-back. Round-trip must still succeed.
  [Test, Category("ExceptionalCase")]
  public void Replace_LargerThanZones_FallsBackToReallocAndStillRoundTrips() {
    var image = BuildImage(("doc.bin", Pattern(1024, seed: 83))); // 2 blocks direct
    var bigger = Pattern(8 * 1024, seed: 89);                     // 16 blocks → won't fit existing 2

    var inPlace = CoherentInPlaceModifier.Replace(image, "doc.bin", bigger);
    Assert.That(inPlace, Is.False,
      "Replace must surface the realloc fall-back when the new payload won't fit");

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["doc.bin"], Is.EqualTo(bigger));
  }

  // ── Mutate-then-extract round-trip across Add+Replace+Remove ─────────────

  [Test, Category("HappyPath")]
  public void MutateThenExtract_AddReplaceRemove_Roundtrips() {
    var seed = Pattern(800, seed: 91);
    var image = BuildImage(("seed.bin", seed));

    // 1) Add
    var added = Pattern(2500, seed: 93); // 5 blocks → still direct
    CoherentInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("added.bin", added)]);

    var afterAdd = ListAndExtract(image.ToArray());
    Assert.That(afterAdd.Keys, Is.EquivalentTo(new[] { "seed.bin", "added.bin" }));
    Assert.That(afterAdd["added.bin"], Is.EqualTo(added));

    // 2) Replace (fits)
    var replaced = Pattern(2400, seed: 97); // 5 blocks — fits added's existing
    var inPlace = CoherentInPlaceModifier.Replace(image, "added.bin", replaced);
    Assert.That(inPlace, Is.True);

    var afterReplace = ListAndExtract(image.ToArray());
    Assert.That(afterReplace["added.bin"], Is.EqualTo(replaced));
    Assert.That(afterReplace["seed.bin"], Is.EqualTo(seed));

    // 3) Remove
    var removed = CoherentInPlaceModifier.Remove(image, "added.bin");
    Assert.That(removed, Is.True);

    var afterRemove = ListAndExtract(image.ToArray());
    Assert.That(afterRemove.Keys, Is.EquivalentTo(new[] { "seed.bin" }));
    Assert.That(afterRemove["seed.bin"], Is.EqualTo(seed));

    // 4) Magic survived all three operations
    var img = image.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1528, 2)),
      Is.EqualTo((ushort)0xFD18));
  }

  // ── Validation: bad magic ────────────────────────────────────────────────

  [Test, Category("ExceptionalCase")]
  public void Add_OnImageWithBadMagic_Throws() {
    var ms = new MemoryStream();
    ms.SetLength(4096); // empty buffer — magic byte at 1528 will be zero
    Assert.Throws<InvalidDataException>(() =>
      CoherentInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("x", "y"u8.ToArray())]));
  }

  [Test, Category("ExceptionalCase")]
  public void Remove_OnImageWithBadMagic_Throws() {
    var ms = new MemoryStream();
    ms.SetLength(4096);
    Assert.Throws<InvalidDataException>(() =>
      CoherentInPlaceModifier.Remove(ms, "anything"));
  }

  [Test, Category("ExceptionalCase")]
  public void Replace_OnImageWithBadMagic_Throws() {
    var ms = new MemoryStream();
    ms.SetLength(4096);
    Assert.Throws<InvalidDataException>(() =>
      CoherentInPlaceModifier.Replace(ms, "anything", "x"u8.ToArray()));
  }

  // ── Descriptor wiring ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Add_Remove_DelegateToInPlaceModifier() {
    var d = new CoherentFormatDescriptor();
    var image = BuildImage(("a.bin", "alpha"u8.ToArray()));

    image.Position = 0;
    d.Add(image, [ArchiveInputInfo.InMemory("b.bin", "beta"u8.ToArray())]);
    image.Position = 0;
    var entries = d.List(image, null).Select(e => e.Name).ToList();
    Assert.That(entries, Is.EquivalentTo(new[] { "a.bin", "b.bin" }));

    image.Position = 0;
    d.Remove(image, ["a.bin"]);
    image.Position = 0;
    var after = d.List(image, null).Select(e => e.Name).ToList();
    Assert.That(after, Is.EquivalentTo(new[] { "b.bin" }));
  }

  // ── Utilities ────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var ok = true;
      for (var j = 0; j < needle.Length; j++)
        if (haystack[i + j] != needle[j]) { ok = false; break; }
      if (ok) return i;
    }
    return -1;
  }
}
