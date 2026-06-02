using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Fatx;

namespace Compression.Tests.Fatx;

/// <summary>
/// FATX R/W (Add / Remove) round-trip tests. The modifier mutates an existing
/// FATX image in place. Coverage spans both FAT16 and FAT32 widths, FAT chain
/// consistency post-mutation, deleted-name (0xE5 tombstone) handling on
/// subsequent re-add, secure-wipe verification, and standard error / boundary
/// equivalence classes.
/// </summary>
/// <remarks>
/// HappyPath: single Add round-trip, Remove round-trip, descriptor wiring.<br/>
/// Boundary: zero-byte Add, multi-cluster Add, alternating Add/Remove waves,
///   slot reuse after tombstone, FAT32 width.<br/>
/// Sad: free-cluster exhaustion, Remove non-existent file.
/// </remarks>
[TestFixture]
public class FatxRwTests {

  // ── Helpers ──────────────────────────────────────────────────────────────

  /// <summary>Builds a FATX image holding <paramref name="files"/> plus
  /// enough free clusters that subsequent <see cref="FatxModifier.AddFile"/>
  /// calls have room to work. The writer auto-sizes images to exactly fit
  /// the requested files, so without slack the FAT has no free entries and
  /// every Add throws "no free run available". We seed slack by writing a
  /// large dummy file then tombstoning + freeing its chain via
  /// <see cref="FatxModifier.RemoveFile"/> — the resulting image has the
  /// dummy's clusters marked free, exactly the configuration a real Xbox
  /// volume sits in after deletions.</summary>
  private static byte[] BuildBaseImage(params (string Name, byte[] Data)[] files) {
    // Default slack: 64 KiB = 32 clusters at 2 KiB each. Tests that hit
    // boundary cases override this via BuildBaseImageWithSlack.
    return BuildBaseImageWithSlack(slackBytes: 64 * 1024, files);
  }

  private static byte[] BuildBaseImageWithSlack(int slackBytes, params (string Name, byte[] Data)[] files) {
    var w = new FatxWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    // Sacrificial slack file — gets removed below to free its clusters.
    const string SlackName = "__slack__.tmp";
    w.AddFile(SlackName, new byte[slackBytes]);
    var image = w.Build();
    // Free the slack file's clusters so the modifier sees room to work.
    var removed = FatxModifier.RemoveFile(image, SlackName);
    if (!removed) throw new InvalidOperationException(
      "FATX test seed: slack file removal failed (modifier could not find dirent).");
    return image;
  }

  private static List<FatxEntry> ReadEntries(byte[] image) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    return [..r.Entries];
  }

  private static byte[] Extract(byte[] image, string name) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == name && !e.IsDirectory)
      ?? throw new InvalidOperationException($"FATX R/W: entry '{name}' not found.");
    return r.Extract(entry);
  }

  // ── Descriptor wiring ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRWCapability() {
    var d = new FatxFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify));
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
  }

  // ── HappyPath round-trips ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_SingleFile_AppearsInListAndExtractsCorrectly() {
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    FatxModifier.AddFile(image, "added.txt", "Hello, FATX!"u8.ToArray());

    var entries = ReadEntries(image).Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(entries, Does.Contain("seed.txt"));
    Assert.That(entries, Does.Contain("added.txt"));
    Assert.That(Extract(image, "added.txt"), Is.EqualTo("Hello, FATX!"u8.ToArray()));
    Assert.That(Extract(image, "seed.txt"), Is.EqualTo("seed"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Remove_ExistingFile_LeavesOthersIntact() {
    var image = BuildBaseImage(
      ("keep.txt", "kept-bytes"u8.ToArray()),
      ("drop.bin", new byte[] { 1, 2, 3, 4, 5 }));
    var ok = FatxModifier.RemoveFile(image, "drop.bin");
    Assert.That(ok, Is.True);

    var entries = ReadEntries(image);
    Assert.That(entries.Select(e => e.Name), Does.Contain("keep.txt"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("drop.bin"));
    Assert.That(Extract(image, "keep.txt"), Is.EqualTo("kept-bytes"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void AddViaDescriptor_RoundTripsThroughStream() {
    var seed = BuildBaseImage(("seed.txt", "S"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(seed);

    var d = new FatxFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", "via descriptor"u8.ToArray()),
    };
    d.Add(ms, inputs);

    ms.Position = 0;
    var listed = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(listed, Does.Contain("readme.txt"));
    Assert.That(listed, Does.Contain("seed.txt"));

    ms.Position = 0;
    var extracted = d.ExtractEntryToMemory(ms, "readme.txt", null);
    Assert.That(Encoding.ASCII.GetString(extracted), Is.EqualTo("via descriptor"));
  }

  [Test, Category("HappyPath")]
  public void RemoveViaDescriptor_TombstonesEntry() {
    var seed = BuildBaseImage(
      ("a.txt", "alpha"u8.ToArray()),
      ("b.txt", "bravo"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(seed);

    var d = new FatxFormatDescriptor();
    d.Remove(ms, ["a.txt"]);

    ms.Position = 0;
    var listed = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(listed, Does.Contain("b.txt"));
    Assert.That(listed, Does.Not.Contain("a.txt"));
  }

  // ── Boundary: zero-byte + multi-cluster + alternating waves ─────────────

  [Test, Category("Boundary")]
  public void Add_ZeroByteFile_RecordedWithZeroSize() {
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    FatxModifier.AddFile(image, "empty.bin", []);

    var entries = ReadEntries(image);
    var empty = entries.Single(e => e.Name == "empty.bin");
    Assert.That(empty.Size, Is.EqualTo(0));
    Assert.That(empty.IsDirectory, Is.False);
    Assert.That(Extract(image, "empty.bin"), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Add_LargeFile_SpansMultipleClusters() {
    // Default tiny-image cluster is 2 KiB. 10 KiB payload = 5 clusters.
    var payload = new byte[10 * 1024];
    var rng = new Random(0x5A7);
    rng.NextBytes(payload);

    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    FatxModifier.AddFile(image, "big.bin", payload);
    Assert.That(Extract(image, "big.bin"), Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void Add_Remove_Add_ReusesTombstonedDirentSlot() {
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    FatxModifier.AddFile(image, "victim.txt", "first"u8.ToArray());

    // Snapshot the dirent slot offset chosen for "victim.txt" by scanning.
    var beforeRemove = FindDirentSlotOf(image, "victim.txt");
    Assert.That(beforeRemove, Is.GreaterThanOrEqualTo(0), "victim.txt dirent must exist before remove");

    Assert.That(FatxModifier.RemoveFile(image, "victim.txt"), Is.True);

    // After remove, the byte at the slot must be 0xE5 (FATX tombstone).
    Assert.That(image[beforeRemove], Is.EqualTo((byte)0xE5));

    // Re-add a different file under a different name; it should land in the
    // first reusable slot — which is exactly the tombstoned one we just
    // wiped (FindFreeDirentSlot returns the first 0xE5/0xFF/0x00 slot it sees).
    FatxModifier.AddFile(image, "second.txt", "second"u8.ToArray());
    var afterAdd = FindDirentSlotOf(image, "second.txt");
    Assert.That(afterAdd, Is.EqualTo(beforeRemove),
      "Re-added file should reuse the freshly tombstoned dirent slot.");

    var entries = ReadEntries(image).Select(e => e.Name).ToHashSet();
    Assert.That(entries, Does.Contain("second.txt"));
    Assert.That(entries, Does.Not.Contain("victim.txt"));
    Assert.That(Extract(image, "second.txt"), Is.EqualTo("second"u8.ToArray()));
  }

  [Test, Category("Boundary")]
  public void Add_Remove_Wave_FatChainStaysConsistent() {
    var image = BuildBaseImage(("anchor.txt", "anchor"u8.ToArray()));
    // Capture the initial set of cluster counts before mutation.
    var anchorBefore = Extract(image, "anchor.txt");

    // Add → remove → add → remove → add. The anchor must survive every cycle
    // and its bytes must always come back identical.
    var inputs = new[] {
      ("alpha.dat", new byte[] { 0xA0, 0xA1, 0xA2 }),
      ("bravo.dat", new byte[2048]),  // exact cluster size
      ("charlie.dat", new byte[3000]),
    };

    foreach (var (n, d) in inputs) {
      FatxModifier.AddFile(image, n, d);
      Assert.That(Extract(image, n), Is.EqualTo(d));
      Assert.That(Extract(image, "anchor.txt"), Is.EqualTo(anchorBefore));
    }
    foreach (var (n, _) in inputs) {
      Assert.That(FatxModifier.RemoveFile(image, n), Is.True);
      Assert.That(Extract(image, "anchor.txt"), Is.EqualTo(anchorBefore));
    }
    // Final state: only anchor remains, reader walks every reachable cluster.
    var entries = ReadEntries(image);
    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(new[] { "anchor.txt" }));
  }

  // ── FAT chain integrity ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_LinksClustersAsContiguousChain() {
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    var payload = new byte[6 * 1024]; // 3 clusters at 2 KiB each
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
    FatxModifier.AddFile(image, "chain.bin", payload);

    // Locate the file's first cluster via its dirent + check the FAT links.
    var slot = FindDirentSlotOf(image, "chain.bin");
    var first = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slot + 0x2C));
    Assert.That(first, Is.GreaterThanOrEqualTo(2u));

    // FAT16 width — 2 bytes per entry. The allocator wrote `first → first+1 → first+2 → EoC(0xFFFF)`.
    var fatBase = 0x1000;
    var e0 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fatBase + (int)first * 2));
    var e1 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fatBase + ((int)first + 1) * 2));
    var e2 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fatBase + ((int)first + 2) * 2));
    Assert.That(e0, Is.EqualTo((ushort)(first + 1)));
    Assert.That(e1, Is.EqualTo((ushort)(first + 2)));
    Assert.That(e2 >= 0xFFF8, Is.True, $"last cluster's FAT entry must be EoC (got 0x{e2:X4})");
  }

  [Test, Category("HappyPath")]
  public void Remove_FreesEveryClusterInChain_AndWipesData() {
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    var payload = new byte[5 * 1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = 0xCC;
    FatxModifier.AddFile(image, "wipe.bin", payload);

    var slot = FindDirentSlotOf(image, "wipe.bin");
    var first = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slot + 0x2C));

    Assert.That(FatxModifier.RemoveFile(image, "wipe.bin"), Is.True);

    // FAT entries for every cluster in the freed chain must now be 0 (free).
    var fatBase = 0x1000;
    for (var k = 0u; k < 3; k++) {
      var e = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fatBase + (int)(first + k) * 2));
      Assert.That(e, Is.EqualTo((ushort)0), $"cluster {first + k} should be freed (entry == 0)");
    }
    // Data clusters must be zeroed (no 0xCC bytes remain).
    var clusterSize = 2048;
    var dataRegionStart = ComputeDataRegionStart(image);
    for (var k = 0u; k < 3; k++) {
      var off = dataRegionStart + (long)(first + k - 1) * clusterSize;
      for (var b = 0; b < clusterSize; b++)
        Assert.That(image[off + b], Is.EqualTo((byte)0x00),
          $"freed cluster {first + k} byte {b} should be zero, got 0x{image[off + b]:X2}");
    }
  }

  // ── FAT32 width ─────────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Add_FAT32Width_RoundTrips() {
    // Force FAT32 by building an image with >= 0xFFF4 clusters. With
    // sectorsPerCluster=1 (smallest legal value) each cluster is 512 bytes,
    // so we need at least 0xFFF4 (= 65524) clusters → ~32 MiB image.
    // Insert one tiny seed file; the writer pads the image to fit the chosen
    // sectorsPerCluster and the planning math, but to hit the FAT32 threshold
    // we drive the writer with an explicit "must reach this many clusters"
    // dummy file whose payload happens to be sized exactly to cross the
    // reader's heuristic.
    var w = new FatxWriter();
    w.AddFile("seed.txt", "seed"u8.ToArray());
    // 0xFFF4 clusters * 512 bytes ≈ 32 MiB. Round up to be safely past the
    // threshold (the FAT region itself shifts a few clusters away).
    var bigBytes = (0xFFF8L) * 512;
    var big = new byte[bigBytes];
    // Sparse: only fill a few markers so the test stays fast — Buffer.BlockCopy
    // of 32 MiB zero bytes is fine, but we want a few non-zero bytes to verify
    // they survive round-trip.
    big[0] = 0xDE; big[1] = 0xAD; big[bigBytes - 2] = 0xBE; big[bigBytes - 1] = 0xEF;
    w.AddFile("filler.bin", big);
    // Slack file so the modifier has free clusters to work with — same trick
    // BuildBaseImageWithSlack uses for FAT16. 16 KiB / 512 B/cluster = 32
    // clusters of slack.
    const string SlackName = "__slack__.tmp";
    w.AddFile(SlackName, new byte[16 * 1024]);
    var image = w.Build(sectorsPerCluster: 1);
    // Free the slack file's clusters.
    Assert.That(FatxModifier.RemoveFile(image, SlackName), Is.True,
      "FAT32 seed: slack file removal failed.");

    // Sanity check: reader must classify this as FAT32.
    using (var ms = new MemoryStream(image)) {
      using var r = new FatxReader(ms);
      Assert.That(r.FatType, Is.EqualTo(32),
        $"Expected FAT32 width; reader saw FAT{r.FatType}. Image size: {image.Length}, cluster size: {r.ClusterSize}.");
    }

    // Now Add a small file via the modifier.
    FatxModifier.AddFile(image, "fat32-add.txt", "FAT32 add works"u8.ToArray());

    var entries = ReadEntries(image).Select(e => e.Name).ToHashSet();
    Assert.That(entries, Does.Contain("fat32-add.txt"));
    Assert.That(entries, Does.Contain("seed.txt"));
    Assert.That(Extract(image, "fat32-add.txt"),
      Is.EqualTo("FAT32 add works"u8.ToArray()));
    Assert.That(Extract(image, "seed.txt"), Is.EqualTo("seed"u8.ToArray()));

    // And the FAT32 EoC sentinel must be 0xFFFFFFFF-class for the new file's
    // chain — verify by walking its dirent.
    var slot = FindDirentSlotOf(image, "fat32-add.txt");
    var first = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slot + 0x2C));
    Assert.That(first, Is.GreaterThanOrEqualTo(2u));
    var fatEntry = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x1000 + (int)first * 4));
    Assert.That(fatEntry >= 0xFFFFFFF8u, Is.True,
      $"FAT32 EoC sentinel expected on the single-cluster chain (got 0x{fatEntry:X8}).");

    // Remove + verify cluster freed in FAT32.
    Assert.That(FatxModifier.RemoveFile(image, "fat32-add.txt"), Is.True);
    var freed = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x1000 + (int)first * 4));
    Assert.That(freed, Is.EqualTo(0u), "FAT32 cluster should be freed after RemoveFile.");
  }

  // ── Sad path ────────────────────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Remove_UnknownFile_ReturnsFalseAndLeavesImageUntouched() {
    var image = BuildBaseImage(("only.txt", "only"u8.ToArray()));
    var copy = (byte[])image.Clone();
    Assert.That(FatxModifier.RemoveFile(image, "missing.txt"), Is.False);
    Assert.That(image, Is.EqualTo(copy));
  }

  [Test, Category("Sad")]
  public void Add_FreeClusterExhaustion_Throws() {
    // Build the smallest possible image: one cluster (root) + one extra
    // cluster of slack from the writer's planning. We then try to add a file
    // whose chain needs more clusters than the image actually contains.
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));

    // Drown the FAT in allocations until we exhaust free space. The writer
    // sized the image tightly around the seed file; a few KB of extra data
    // should saturate. Keep adding until AllocateChain throws.
    var ex = Assert.Throws<InvalidOperationException>(() => {
      for (var i = 0; i < 1024; i++) {
        var payload = new byte[16 * 1024]; // 8 clusters of 2 KiB each per add
        FatxModifier.AddFile(image, $"f{i:000}.bin", payload);
      }
    });
    Assert.That(ex!.Message, Does.Contain("FATX modifier"));
  }

  [Test, Category("Sad")]
  public void Add_RootDirentSlotExhaustion_Throws() {
    // Root cluster is one cluster = clusterSize bytes / 64 = N dirent slots.
    // For the default 2 KiB cluster that's 32 slots. After the seed + 31 adds
    // the next add must throw because there's no free slot left and v1 of the
    // modifier doesn't extend the root chain.
    var image = BuildBaseImage(("seed.txt", "seed"u8.ToArray()));
    // The seed occupies 1 slot. 2 KiB / 64 = 32 slots total, so add 30 more
    // zero-byte entries (no cluster allocation pressure) — the 31st must
    // succeed (filling the last slot) and the 32nd must throw.
    for (var i = 0; i < 30; i++)
      FatxModifier.AddFile(image, $"e{i:00}.txt", []);

    // The 32nd file goes into the very last slot of the root cluster.
    FatxModifier.AddFile(image, "last.txt", []);
    var ex = Assert.Throws<InvalidOperationException>(() => {
      FatxModifier.AddFile(image, "overflow.txt", []);
    });
    Assert.That(ex!.Message, Does.Contain("dirent slot"));
  }

  // ── Internal scanning helpers (test-only mirror of FatxModifier privates) ─

  /// <summary>Scans the root cluster for a live dirent with the given name and
  /// returns its absolute byte offset in <paramref name="image"/>, or -1.</summary>
  private static int FindDirentSlotOf(byte[] image, string name) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    var clusterSize = r.ClusterSize;
    var rootCluster = r.RootDirCluster;
    // Mirror FatxReader.DataRegionStart since it's internal/private.
    var dataRegionStart = ComputeDataRegionStart(image);
    var rootOff = dataRegionStart + (long)(rootCluster - 1) * clusterSize;
    for (var off = 0; off < clusterSize; off += 0x40) {
      var slot = (int)(rootOff + off);
      var nameLen = image[slot];
      if (nameLen == 0xFF || nameLen == 0x00) return -1;
      if (nameLen == 0xE5) continue;
      if (nameLen > 42) continue;
      var disk = Encoding.ASCII.GetString(image.AsSpan(slot + 2, nameLen));
      if (string.Equals(disk, name, StringComparison.OrdinalIgnoreCase))
        return slot;
    }
    return -1;
  }

  /// <summary>Test-only mirror of FatxReader.DataRegionStart. Computes the
  /// byte offset of cluster 1 from the image geometry.</summary>
  private static long ComputeDataRegionStart(byte[] image) {
    var spc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x08));
    var clusterSize = (int)spc * 512;
    var dataBytes = (long)image.Length - 0x1000;
    var clusterCount = Math.Max(1L, dataBytes / clusterSize);
    var fatType = clusterCount < 0xFFF4 ? 16 : 32;
    var entryBytes = fatType == 16 ? 2 : 4;
    var fatRaw = (clusterCount + 2) * entryBytes;
    var fatRounded = (fatRaw + 0xFFF) & ~0xFFFL;
    return 0x1000 + fatRounded;
  }
}
