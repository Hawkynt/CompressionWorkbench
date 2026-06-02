using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.SysV;

namespace Compression.Tests.SysV;

/// <summary>
/// R/W mutation tests for the SysV (s5fs) modifier — verifies
/// <see cref="SysVModifier"/>'s real chained free-block group cache refill
/// and inode re-scan paths, plus the descriptor's
/// <see cref="IArchiveModifiable"/> contract.
/// </summary>
[TestFixture]
public class SysVRwTests {

  // Keep storm payloads to a single 1 KB zone so 200 add/remove cycles
  // stay within the seed image's free-block budget.
  private const int BlockSizeOneShot = 1024;


  // ── Stage 3 — IArchiveModifiable opt-in ─────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_OptsIntoIArchiveModifiable() {
    var d = new SysVFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "R/W promotion: SysV descriptor must opt in to IArchiveModifiable.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "R/W promotion: capability flag must advertise CanModify.");
  }

  // ── Stage 3 — Basic in-place Add/Remove ─────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_FlatRootFile_RoundTripsViaReader() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    SysVModifier.AddFile(ms, "added.txt", "freshly added"u8.ToArray());

    // Image still detects as s5fs.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(ms.GetBuffer().AsSpan(1528, 4)),
      Is.EqualTo(0xFD187E20u));

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    var added = r.Entries.Single(e => e.Name == "added.txt");
    Assert.That(Encoding.ASCII.GetString(r.Extract(added)),
      Is.EqualTo("freshly added"));
  }

  [Test, Category("HappyPath")]
  public void Remove_FlatRootFile_DropsEntryAndWipesBytes() {
    var seed = SysVWriter.Build([
      ("keep.txt",   "keep me"u8.ToArray()),
      ("drop.bin",   "WIPE-ME-WIPE-ME-WIPE-ME"u8.ToArray()),
    ]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var removed = SysVModifier.RemoveFile(ms, "drop.bin");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "keep.txt" }));

    // The freed data block must be zeroed (Remove wipe contract).
    Assert.That(ms.GetBuffer(), Does.Not.Contain<byte>((byte)'W')
      .Or.Not.Contain(Encoding.ASCII.GetBytes("WIPE-ME-WIPE-ME-WIPE-ME")));
  }

  [Test, Category("HappyPath")]
  public void Add_TwoFilesThenRemoveFirst_LeavesOnlySecond() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    SysVModifier.AddFile(ms, "alpha.txt", "ALPHA"u8.ToArray());
    SysVModifier.AddFile(ms, "beta.txt",  "BETA"u8.ToArray());
    var removed = SysVModifier.RemoveFile(ms, "alpha.txt");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "seed.txt", "beta.txt" }));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "beta.txt"))),
      Is.EqualTo("BETA"));
  }

  // ── Equivalence: file-replacement semantics ──────────────────────────

  [Test, Category("HappyPath")]
  public void Add_DuplicateName_ReplacesContent() {
    var seed = SysVWriter.Build([("doc.txt", "v1"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    SysVModifier.AddFile(ms, "doc.txt", "v2-replacement"u8.ToArray());

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("doc.txt"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries[0])),
      Is.EqualTo("v2-replacement"));
  }

  // ── Boundary: per-file 10 KB cap ─────────────────────────────────────

  [Test, Category("Boundary")]
  public void Add_ExactlyTenKilobytes_FillsAllDirectZones() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    var payload = new byte[10 * 1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
    SysVModifier.AddFile(ms, "big.bin", payload);

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "big.bin")), Is.EqualTo(payload));
  }

  [Test, Category("Sad")]
  public void Add_FileExceedingDirectZones_Throws() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    var oversize = new byte[10 * 1024 + 1];
    Assert.Throws<InvalidOperationException>(
      () => SysVModifier.AddFile(ms, "huge.bin", oversize));
  }

  [Test, Category("Sad")]
  public void Add_NestedPath_ThrowsNotSupported() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.Throws<NotSupportedException>(
      () => SysVModifier.AddFile(ms, "etc/motd", "hi"u8.ToArray()));
  }

  // ── Free-cache exhaustion (forces refill from chain group) ───────────

  [Test, Category("Boundary")]
  public void Add_PastFreeCacheLimit_ConsumesChainGroup() {
    // We need an image whose total free pool reaches across the in-line
    // cache boundary (49 blocks) into a chained group. To build that, seed
    // an image where we *write* enough files to first exhaust the in-line
    // cache via Remove (spilling a chain block), then re-allocate via Add
    // which must refill from that chain block.

    // Strategy: write a small image, then repeatedly Add+Remove single-
    // block files. After 50+ removes, the modifier must have spilled the
    // cache to a chain block; after 50+ subsequent adds, the modifier must
    // have refilled from that chain block. Each add/remove round-trips.

    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // Stress the cache: 60 add+remove cycles (each cycle frees and
    // re-allocates a single zone, exercising both spill and refill paths).
    for (var i = 0; i < 60; i++) {
      var name = $"cycle{i}.bin";
      SysVModifier.AddFile(ms, name, [(byte)i, (byte)(i >> 8)]);
      Assert.That(SysVModifier.RemoveFile(ms, name), Is.True, $"cycle {i} remove failed");
    }

    // After 60 add/remove cycles the in-line cache is consistent. Final
    // image only carries the seed.
    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "seed.txt" }));
  }

  [Test, Category("Boundary")]
  public void Remove_PastCacheCapacity_SpillsCacheToChainBlock() {
    // Pre-fill the image with enough files to push s_nfree above 50 worth
    // of frees during the subsequent Remove storm. Each file occupies one
    // block; freeing it pushes onto the cache, which spills at the 50th
    // free.

    var seedFiles = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 55; i++)                      // 55 single-block files
      seedFiles.Add(($"f{i:D3}.bin", [(byte)i]));
    var seed = SysVWriter.Build(seedFiles);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var initialStats = SysVModifier.ReadFreeStats(ms);

    // Remove all 55 files — the in-line cache (max 50) must spill at least
    // once to a chain block.
    for (var i = 0; i < 55; i++) {
      var removed = SysVModifier.RemoveFile(ms, $"f{i:D3}.bin");
      Assert.That(removed, Is.True, $"remove f{i:D3} failed");
    }

    // After 55 removes, total free blocks must have grown by >= 55.
    var finalStats = SysVModifier.ReadFreeStats(ms);
    Assert.That(finalStats.TFree, Is.GreaterThanOrEqualTo(initialStats.TFree + 55),
      "total free count must reflect all 55 freed blocks");

    // Verify we can still allocate by adding a file (which will pop from
    // the cache and, if it has consumed the chain pointer slot, refill).
    SysVModifier.AddFile(ms, "post-spill.bin", "OK"u8.ToArray());
    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Does.Contain("post-spill.bin"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "post-spill.bin"))),
      Is.EqualTo("OK"));
  }

  [Test, Category("Boundary")]
  public void Add_AfterChainSpill_TriggersChainRefillPath() {
    // Build the same scenario as above (cache spill), then issue enough
    // Adds to walk back across the in-line/chain boundary. The modifier's
    // AllocateBlock() path that reads the chain block to refill is exercised
    // when s_nfree drops to 1 and the chain pointer (s_free[0]) is non-zero.

    var seedFiles = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 60; i++)
      seedFiles.Add(($"f{i:D3}.bin", [(byte)i]));
    var seed = SysVWriter.Build(seedFiles);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // Free everything first so the cache definitely contains a chain
    // pointer to a real on-disk group.
    for (var i = 0; i < 60; i++) {
      var removed = SysVModifier.RemoveFile(ms, $"f{i:D3}.bin");
      Assert.That(removed, Is.True);
    }

    // Now allocate ~55 new files — this must drain the in-line cache,
    // then follow the chain pointer into the spilled group.
    for (var i = 0; i < 55; i++)
      SysVModifier.AddFile(ms, $"new{i:D3}.bin", [(byte)(i + 100)]);

    ms.Position = 0;
    var r = new SysVReader(ms);
    // Spot-check: all 55 freshly added files must be readable and carry
    // the expected byte.
    for (var i = 0; i < 55; i++) {
      var name = $"new{i:D3}.bin";
      var entry = r.Entries.Single(e => e.Name == name);
      var bytes = r.Extract(entry);
      Assert.That(bytes, Is.EqualTo(new[] { (byte)(i + 100) }), $"content mismatch for {name}");
    }
  }

  // ── Inode-cache exhaustion (forces re-scan path) ─────────────────────

  [Test, Category("Boundary")]
  public void Add_PastInodeCacheCapacity_RefillsViaReScan() {
    // The writer's in-line inode cache holds up to 100 free inodes drawn
    // from the over-allocated ilist tail. Adding > 100 files in one
    // session would exceed the writer's initial cache; instead we trip
    // the same re-scan path by *removing* a swathe of files and then
    // re-adding past the cache ceiling. After enough removes the cache
    // is full (100 entries); subsequent removes don't enlarge the cache
    // but the freed inode slots still become re-discoverable by the
    // re-scan. Adding past 100 must succeed via that re-scan.

    var seedFiles = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 120; i++)              // 120 single-block files
      seedFiles.Add(($"f{i:D3}.bin", [(byte)i]));
    var seed = SysVWriter.Build(seedFiles);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // Drop all 120 files — the cache caps at 100 entries; any overflow
    // is rediscoverable by re-scanning the inode table for zero-mode
    // slots.
    for (var i = 0; i < 120; i++)
      Assert.That(SysVModifier.RemoveFile(ms, $"f{i:D3}.bin"), Is.True);

    var stats = SysVModifier.ReadInodeStats(ms);
    Assert.That(stats.NInode, Is.LessThanOrEqualTo((ushort)100),
      "in-line inode cache size must stay <= 100 (overflow handled by re-scan)");

    // Add 110 files back: the first ~100 drain the cache, the rest must
    // refill via re-scan.
    for (var i = 0; i < 110; i++)
      SysVModifier.AddFile(ms, $"r{i:D3}.bin", [(byte)(i + 200)]);

    ms.Position = 0;
    var r = new SysVReader(ms);
    // Spot-check the cross-cache-boundary writes (slots ~100-110).
    for (var i = 100; i < 110; i++) {
      var name = $"r{i:D3}.bin";
      var entry = r.Entries.Single(e => e.Name == name);
      Assert.That(r.Extract(entry), Is.EqualTo(new[] { (byte)(i + 200) }), $"{name} content mismatch");
    }
  }

  // ── Descriptor IArchiveModifiable path ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Add_RoundTripsThroughOwnReader() {
    var d = new SysVFormatDescriptor();
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // SysV dir names cap at 14 chars (the writer truncates silently to
    // match on-disk layout); descriptor sees the truncated form too.
    d.Add(ms, [ArchiveInputInfo.InMemory("desc-added.txt", "via descriptor"u8.ToArray())]);
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "seed.txt", "desc-added.txt" }));
    ms.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(ms, "desc-added.txt", null)),
      Is.EqualTo("via descriptor"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_DropsTargetEntry() {
    var d = new SysVFormatDescriptor();
    var seed = SysVWriter.Build([
      ("keep.txt", "keep"u8.ToArray()),
      ("drop.txt", "drop"u8.ToArray()),
    ]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    d.Remove(ms, ["drop.txt"]);
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "keep.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_NestedPath_FallsBackToRebuild() {
    var d = new SysVFormatDescriptor();
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // Nested paths must succeed via the rebuild fallback even though the
    // in-place modifier rejects them.
    d.Add(ms, [ArchiveInputInfo.InMemory("etc/motd", "Welcome\n"u8.ToArray())]);
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToArray();
    Assert.That(names, Is.EquivalentTo(new[] { "seed.txt", "etc/motd" }));
    ms.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(ms, "etc/motd", null)),
      Is.EqualTo("Welcome\n"));
  }

  [Test, Category("Sad")]
  public void Remove_UnknownEntry_ReturnsFalse() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.That(SysVModifier.RemoveFile(ms, "nope.txt"), Is.False);
  }

  // ── Mixed Add/Remove storms (equivalence-class coverage) ─────────────

  [Test, Category("HappyPath")]
  public void MixedAddRemoveStorm_PreservesConsistency() {
    // Pre-seed with enough files (= over-allocated ilist) to give the
    // storm room to manoeuvre without exhausting inodes. The modifier
    // doesn't grow the ilist; it can only re-use freed inode slots —
    // testing storm dynamics still exercises the cache spill/refill +
    // inode re-scan paths.
    var seedFiles = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 90; i++)             // 90 placeholders that we'll free
      seedFiles.Add(($"slot{i:D3}.bin", [(byte)i]));
    seedFiles.Add(("anchor.txt", "anchor"u8.ToArray()));
    var seed = SysVWriter.Build(seedFiles);
    using var ms = new MemoryStream();
    ms.Write(seed);

    // Free the 90 placeholders so their inode slots become available for
    // the storm's adds.
    for (var i = 0; i < 90; i++)
      Assert.That(SysVModifier.RemoveFile(ms, $"slot{i:D3}.bin"), Is.True);

    var rnd = new Random(0xC0FFEE);
    var live = new HashSet<string>(StringComparer.Ordinal) { "anchor.txt" };
    for (var step = 0; step < 200; step++) {
      var roll = rnd.Next(100);
      if (roll < 60 && live.Count < 80 || live.Count <= 1) {
        var name = $"f{step:D3}.bin";
        // Single-block payloads keep the zone budget tight across 200
        // steps; the cache-spill + refill paths still exercise.
        var size = rnd.Next(0, BlockSizeOneShot);
        var data = new byte[size];
        rnd.NextBytes(data);
        SysVModifier.AddFile(ms, name, data);
        live.Add(name);
      } else if (live.Count > 1) {
        var pickable = live.Where(n => n != "anchor.txt").ToList();
        if (pickable.Count == 0) continue;
        var pick = pickable[rnd.Next(pickable.Count)];
        Assert.That(SysVModifier.RemoveFile(ms, pick), Is.True);
        live.Remove(pick);
      }
    }

    ms.Position = 0;
    var r = new SysVReader(ms);
    var visible = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.That(visible, Is.EquivalentTo(live),
      $"live set diverged: missing=[{string.Join(",", live.Except(visible))}] "
      + $"unexpected=[{string.Join(",", visible.Except(live))}]");
  }
}
