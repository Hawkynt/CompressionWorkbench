using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Coherent;

namespace Compression.Tests.Coherent;

/// <summary>
/// R/W tests for the Coherent file system: Add/Remove against an existing
/// image, covering the three addressing tiers the writer/reader implement
/// (direct, single-indirect, double-indirect), free-zone/free-inode
/// scavenging, and mixed sequences.
/// </summary>
[TestFixture]
public class CoherentRwTests {

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new CoherentWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  /// <summary>
  /// Wraps the WORM bytes in an expandable <see cref="MemoryStream"/> so the
  /// modifier can grow it when free zones are exhausted. <c>new MemoryStream(byte[])</c>
  /// is fixed-size — we copy into the default ctor instead.
  /// </summary>
  private static MemoryStream Expandable(byte[] image) {
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    ms.Position = 0;
    return ms;
  }

  private static byte[] Pattern(int len, int seed) {
    var b = new byte[len];
    for (var i = 0; i < len; i++) b[i] = (byte)((i * seed + i / 7) & 0xFF);
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

  // ── Add: per-tier round-trips ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_DirectTier_RoundTrips() {
    // Pre-build a near-empty image with capacity for additions: seed it
    // with a small file so the writer's inode-list block has free slots.
    var image = Expandable(BuildImage(("seed", "seed-payload"u8.ToArray())));
    var payload = "fresh-add-direct"u8.ToArray();

    CoherentModifier.AddFile(image, "fresh.txt", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "seed", "fresh.txt" }));
    Assert.That(extracted["fresh.txt"], Is.EqualTo(payload));
    Assert.That(extracted["seed"], Is.EqualTo("seed-payload"u8.ToArray()));
  }

  // Boundary: payload exactly = DirectZones * BlockSize = 5120 bytes — fits
  // direct exactly with no spill into single-indirect.
  [Test, Category("Boundary")]
  public void Add_DirectTierAtCapacity_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(5120, seed: 31);

    CoherentModifier.AddFile(image, "max-direct.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["max-direct.bin"], Is.EqualTo(payload));
  }

  // Boundary: 5121 bytes — first byte that spills into single-indirect.
  [Test, Category("Boundary")]
  public void Add_SingleIndirectTier_FirstByteOverDirect_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(5121, seed: 47);

    CoherentModifier.AddFile(image, "spill.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["spill.bin"], Is.EqualTo(payload));
  }

  // Mid-range single-indirect: 7 KB — same band the WORM tests use.
  [Test, Category("HappyPath")]
  public void Add_SingleIndirectTier_7KB_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(7000, seed: 13);

    CoherentModifier.AddFile(image, "single.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["single.bin"], Is.EqualTo(payload));
  }

  // Boundary: single-indirect at capacity = (10 + 170) * 512 = 92,160 bytes.
  [Test, Category("Boundary")]
  public void Add_SingleIndirectTierAtCapacity_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(92_160, seed: 53);

    CoherentModifier.AddFile(image, "max-single.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["max-single.bin"], Is.EqualTo(payload));
  }

  // First byte that spills into double-indirect = 92,161 bytes.
  [Test, Category("Boundary")]
  public void Add_DoubleIndirectTier_FirstByteOverSingle_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(92_161, seed: 67);

    CoherentModifier.AddFile(image, "dbl-min.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["dbl-min.bin"], Is.EqualTo(payload));
  }

  // Same tier as the WORM Boundary test: 100 KB exercises double-indirect
  // with one full single-indirect row + spill row.
  [Test, Category("HappyPath")]
  public void Add_DoubleIndirectTier_100KB_RoundTrips() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var payload = Pattern(100_000, seed: 71);

    CoherentModifier.AddFile(image, "double.bin", payload);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted["double.bin"], Is.EqualTo(payload));
  }

  // ── Remove: per-tier ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_DirectTier_DisappearsAndStaysExtractable() {
    var keep = Pattern(1024, seed: 3);
    var doomed = Pattern(1024, seed: 5);
    var image = Expandable(BuildImage(("keep.bin", keep), ("doom.bin", doomed)));

    var removed = CoherentModifier.RemoveFile(image, "doom.bin", wipeData: true);

    Assert.That(removed, Is.True);
    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin" }));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));
  }

  [Test, Category("HappyPath")]
  public void Remove_SingleIndirectTier_DisappearsAndStaysExtractable() {
    var keep = Pattern(1024, seed: 3);
    var doomed = Pattern(7000, seed: 19); // forces single-indirect
    var image = Expandable(BuildImage(("keep.bin", keep), ("big.bin", doomed)));

    var removed = CoherentModifier.RemoveFile(image, "big.bin", wipeData: true);

    Assert.That(removed, Is.True);
    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin" }));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));
  }

  [Test, Category("HappyPath")]
  public void Remove_DoubleIndirectTier_DisappearsAndStaysExtractable() {
    var keep = Pattern(1024, seed: 3);
    var doomed = Pattern(100_000, seed: 23); // forces double-indirect
    var image = Expandable(BuildImage(("keep.bin", keep), ("very-big.bin", doomed)));

    var removed = CoherentModifier.RemoveFile(image, "very-big.bin", wipeData: true);

    Assert.That(removed, Is.True);
    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "keep.bin" }));
    Assert.That(extracted["keep.bin"], Is.EqualTo(keep));
  }

  // Equivalence class: Remove of a non-existent name → false, image unchanged.
  [Test, Category("ExceptionalCase")]
  public void Remove_NonExistent_ReturnsFalse() {
    var keep = "keep-content"u8.ToArray();
    var image = Expandable(BuildImage(("keep", keep)));
    var snapshot = image.ToArray();

    var removed = CoherentModifier.RemoveFile(image, "missing", wipeData: true);

    Assert.That(removed, Is.False);
    Assert.That(image.ToArray(), Is.EqualTo(snapshot));
  }

  // Wipe assertion: after Remove, the freed data block bytes are zero in
  // the on-disk image (the modifier was asked to wipeData:true).
  [Test, Category("HappyPath")]
  public void Remove_WipesFreedDataZones() {
    var sentinel = Encoding.ASCII.GetBytes("SECRET_SENTINEL_STRING_FOR_REMOVE_WIPE_CHECK_BLOCK_PATTERN");
    var payload = new byte[1024];
    // Fill first half with sentinel pattern.
    for (var i = 0; i < payload.Length; i++) payload[i] = sentinel[i % sentinel.Length];

    var image = Expandable(BuildImage(("doom.bin", payload)));
    var before = image.ToArray();
    Assert.That(IndexOfPattern(before, sentinel), Is.GreaterThanOrEqualTo(0), "sentinel should be present before Remove");

    CoherentModifier.RemoveFile(image, "doom.bin", wipeData: true);

    var after = image.ToArray();
    Assert.That(IndexOfPattern(after, sentinel), Is.LessThan(0), "sentinel must be wiped after Remove");
  }

  // ── Free-list scavenging: re-use of zones freed by Remove ───────────────

  [Test, Category("HappyPath")]
  public void Add_AfterRemove_ScavengesFreedZones() {
    // 1) Build image with one file. Record its size.
    var doomed = Pattern(1024, seed: 11);
    var image = Expandable(BuildImage(("doom.bin", doomed)));
    var sizeBefore = image.Length;

    // 2) Remove it. The image bytes shrink/stay same (we don't truncate),
    // but the zones it occupied are now unreferenced.
    CoherentModifier.RemoveFile(image, "doom.bin", wipeData: true);
    var sizeAfterRemove = image.Length;
    Assert.That(sizeAfterRemove, Is.EqualTo(sizeBefore));

    // 3) Add a fresh file of the same size — must scavenge into the freed
    // zones rather than grow the image.
    var fresh = Pattern(1024, seed: 13);
    CoherentModifier.AddFile(image, "fresh.bin", fresh);

    var sizeAfterAdd = image.Length;
    Assert.That(sizeAfterAdd, Is.EqualTo(sizeBefore),
      "Add of equivalent-size file after Remove must reuse the freed zones, not extend the image.");

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "fresh.bin" }));
    Assert.That(extracted["fresh.bin"], Is.EqualTo(fresh));
  }

  // ── Free-inode cache exhaustion: writer leaves caches empty on disk,
  //    so the modifier must rebuild allocation via inode-table scan. We
  //    verify that adding many files past the original inode footprint
  //    works because we grow the inode table indirectly — actually the
  //    inode table is pre-sized by the writer to the file count.
  //    We test the natural ceiling: adding beyond the inode-table capacity
  //    must surface a clean IOException (which the descriptor falls back
  //    from to ModifyRebuilder; the bare modifier surfaces it).

  [Test, Category("ExceptionalCase")]
  public void Add_BeyondInodeTableCapacity_ThrowsCleanly() {
    // Build with 1 file → isize = ceil((3 + 1 - 1) / 8) = 1 → 8 inode slots
    // in block 2. Inode 1 is SB-aliased (s_isize/s_fsize), inode 7 overlaps
    // s_ninode at byte 408, inode 8 overlaps s_time at 496 AND s_magic at 504
    // — all three are unsafe to write through. Inode 2 = root. Inode 3 used
    // by seed. So slots 4..6 = 3 free.
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));

    // Add 3 files — should all succeed.
    for (var i = 0; i < 3; i++)
      CoherentModifier.AddFile(image, $"f{i}", new byte[] { (byte)i });

    // 4th add must fail because the free inode slots in the SB-aliased block are exhausted.
    Assert.Throws<IOException>(() => CoherentModifier.AddFile(image, "boom", "x"u8.ToArray()));
  }

  // Descriptor-level fallback: when the modifier can't allocate inodes the
  // descriptor must rebuild the image so the Add still succeeds end-to-end.
  [Test, Category("HappyPath")]
  public void Descriptor_Add_FallsBackToRebuildOnInodeExhaustion() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var d = new CoherentFormatDescriptor();

    // Three files: fits via in-place. Fourth: triggers rebuild fallback
    // because inode 7+ overlap SB fields and are reserved.
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 4; i++)
      inputs.Add(ArchiveInputInfo.InMemory($"f{i}", new byte[] { (byte)i }));

    d.Add(image, inputs);

    image.Position = 0;
    var entries = d.List(image, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "seed", "f0", "f1", "f2", "f3" }));
  }

  // ── Mixed sequences across all three tiers ──────────────────────────────

  [Test, Category("HappyPath")]
  public void MixedSequence_AddRemoveAddAcrossTiers() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));

    var d1 = Pattern(2_000, seed: 1);     // direct
    var d2 = Pattern(80_000, seed: 2);    // single-indirect
    var d3 = Pattern(150_000, seed: 3);   // double-indirect

    CoherentModifier.AddFile(image, "direct.bin", d1);
    CoherentModifier.AddFile(image, "single.bin", d2);
    CoherentModifier.AddFile(image, "double.bin", d3);

    var afterAdds = ListAndExtract(image.ToArray());
    Assert.That(afterAdds["direct.bin"], Is.EqualTo(d1));
    Assert.That(afterAdds["single.bin"], Is.EqualTo(d2));
    Assert.That(afterAdds["double.bin"], Is.EqualTo(d3));

    CoherentModifier.RemoveFile(image, "single.bin", wipeData: true);
    var afterRemove = ListAndExtract(image.ToArray());
    Assert.That(afterRemove.Keys, Is.EquivalentTo(new[] { "seed", "direct.bin", "double.bin" }));
    Assert.That(afterRemove["double.bin"], Is.EqualTo(d3),
      "double-indirect file must survive removal of a single-indirect neighbour");

    var d4 = Pattern(60_000, seed: 4); // single-indirect again (name kept ≤14 chars for Coherent dirent)
    CoherentModifier.AddFile(image, "reborn.bin", d4);
    var final = ListAndExtract(image.ToArray());
    Assert.That(final.Keys, Is.EquivalentTo(new[] { "seed", "direct.bin", "double.bin", "reborn.bin" }));
    Assert.That(final["reborn.bin"], Is.EqualTo(d4));
    Assert.That(final["double.bin"], Is.EqualTo(d3));
    Assert.That(final["direct.bin"], Is.EqualTo(d1));
  }

  // Replace-by-name: AddFile with an existing name overwrites the previous
  // entry. The previous bytes are wiped and the new ones are reachable.
  [Test, Category("HappyPath")]
  public void Add_ReplacesExistingEntry_ByName() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    var v1 = "version-one-content"u8.ToArray();
    var v2 = Pattern(7000, seed: 91); // forces a tier promotion to single-indirect

    CoherentModifier.AddFile(image, "doc.txt", v1);
    CoherentModifier.AddFile(image, "doc.txt", v2);

    var extracted = ListAndExtract(image.ToArray());
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "seed", "doc.txt" }));
    Assert.That(extracted["doc.txt"], Is.EqualTo(v2));
  }

  // ── Descriptor-level capability + round-trip via IArchiveModifiable ─────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesModifyCapability() {
    var d = new CoherentFormatDescriptor();
    Assert.That(d, Is.AssignableTo<IArchiveModifiable>());
    Assert.That((d.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddAndRemove_RoundTrip() {
    var image = Expandable(BuildImage(("keep.txt", "keep me"u8.ToArray())));
    var d = new CoherentFormatDescriptor();

    var addPayload = "added by descriptor"u8.ToArray();
    d.Add(image, [ArchiveInputInfo.InMemory("added.txt", addPayload)]);
    image.Position = 0;
    var afterAddList = d.List(image, null).Select(e => e.Name).ToList();
    Assert.That(afterAddList, Is.EquivalentTo(new[] { "keep.txt", "added.txt" }));

    image.Position = 0;
    d.Remove(image, ["keep.txt"]);
    image.Position = 0;
    var afterRemoveList = d.List(image, null).Select(e => e.Name).ToList();
    Assert.That(afterRemoveList, Is.EquivalentTo(new[] { "added.txt" }));

    image.Position = 0;
    var got = d.ExtractEntryToMemory(image, "added.txt", null);
    Assert.That(got, Is.EqualTo(addPayload));
  }

  // The superblock magic must survive Add+Remove cycles — external tools
  // identify the FS by this signature.
  [Test, Category("HappyPath")]
  public void Magic_SurvivesAddRemoveCycle() {
    var image = Expandable(BuildImage(("seed", "s"u8.ToArray())));
    CoherentModifier.AddFile(image, "x.bin", Pattern(2048, seed: 7));
    CoherentModifier.RemoveFile(image, "x.bin", wipeData: true);
    CoherentModifier.AddFile(image, "y.bin", Pattern(50_000, seed: 9));

    var img = image.ToArray();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1528, 2));
    Assert.That(magic, Is.EqualTo((ushort)0xFD18));
  }

  // ── Utilities ────────────────────────────────────────────────────────────

  private static int IndexOfPattern(byte[] haystack, byte[] needle) {
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
