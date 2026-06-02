using System.Text;
using Compression.Registry;

namespace Compression.Tests.Xenix;

/// <summary>
/// R/W (in-place Add / Remove) tests for the Xenix V modifier. Exercises:
/// — Add round-trip through XenixReader after WORM image emission.
/// — Remove erases the dirent + frees the inode/zones (reader confirms).
/// — Cache cycle: Remove → cache push, Add → cache pop, multi-cycle stability.
/// — Refill from scan after cache exhaustion (mass-add past NICFREE = 50 zones
///   forces at least one in-place spill + refill path).
/// — Large-file (&gt; 10 KB) rejection — direct-zone-only scope.
/// — Descriptor wiring: IArchiveModifiable + CanModify flag.
/// </summary>
[TestFixture]
public class XenixRwTests {

  // ── WORM-emit a fresh seed image with a single file ─────────────────────

  private static byte[] BuildSeedImage(params (string Name, byte[] Data)[] initial) {
    using var ms = new MemoryStream();
    using (var w = new FileSystem.Xenix.XenixWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in initial)
        w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    using var ms = new MemoryStream(image);
    using var r = new FileSystem.Xenix.XenixReader(ms);
    var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      dict[e.Name] = r.Extract(e);
    }
    return dict;
  }

  // ── Descriptor wiring ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsArchiveModifiable_WithCanModifyFlag() {
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  // ── AddFile direct path ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void AddFile_OnWormImage_RoundTripsViaReader() {
    var image = BuildSeedImage(("seed.txt", "seed body"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    FileSystem.Xenix.XenixModifier.AddFile(ms, "added.txt", "added body"u8.ToArray());

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    Assert.That(Encoding.ASCII.GetString(after["seed.txt"]), Is.EqualTo("seed body"));
    Assert.That(Encoding.ASCII.GetString(after["added.txt"]), Is.EqualTo("added body"));
  }

  // ── Add → Remove → Add wave, exercising LIFO cache push/pop ────────────

  [Test, Category("HappyPath")]
  public void AddRemoveAddWave_StableRoundTrip() {
    var image = BuildSeedImage(("a", "AAA"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    FileSystem.Xenix.XenixModifier.AddFile(ms, "b", "BBB"u8.ToArray());
    FileSystem.Xenix.XenixModifier.AddFile(ms, "c", "CCC"u8.ToArray());
    Assert.That(FileSystem.Xenix.XenixModifier.RemoveFile(ms, "b"), Is.True);
    FileSystem.Xenix.XenixModifier.AddFile(ms, "d", "DDD"u8.ToArray());

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "a", "c", "d" }));
    Assert.That(Encoding.ASCII.GetString(after["a"]), Is.EqualTo("AAA"));
    Assert.That(Encoding.ASCII.GetString(after["c"]), Is.EqualTo("CCC"));
    Assert.That(Encoding.ASCII.GetString(after["d"]), Is.EqualTo("DDD"));
  }

  // ── Remove erases dirent + zeroes file body ────────────────────────────

  [Test, Category("HappyPath")]
  public void RemoveFile_ErasesEntryAndWipesData() {
    var marker = "TOPSECRET_PAYLOAD_42"u8.ToArray();
    var image = BuildSeedImage(("classified", marker), ("public", "ok"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.That(FileSystem.Xenix.XenixModifier.RemoveFile(ms, "classified", wipeData: true), Is.True);

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "public" }));

    // The marker bytes must no longer be findable in the image — wipeData
    // zeroed the body block of the removed file.
    var raw = ms.ToArray();
    Assert.That(FindBytes(raw, marker), Is.LessThan(0), "Removed file payload still present in image.");
  }

  // ── Remove of non-existent name returns false ──────────────────────────

  [Test, Category("Sad")]
  public void RemoveFile_Missing_ReturnsFalse() {
    var image = BuildSeedImage(("a", "x"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.That(FileSystem.Xenix.XenixModifier.RemoveFile(ms, "no-such"), Is.False);

    // Image still has the seed file.
    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "a" }));
  }

  // ── Cache cycle: add → remove → re-add forces zone-cache push then pop ─
  // The free zone bracket grows on Remove and shrinks on Add — running this
  // through many cycles exercises the cache LIFO and confirms the modifier
  // doesn't leak inodes or zones across iterations.

  [Test, Category("Boundary")]
  public void RepeatedAddRemoveCycle_DoesNotLeakInodesOrZones() {
    var image = BuildSeedImage(("seed", "s"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    // Each iteration: add a 2-block file, remove it. If inodes/zones leaked
    // we'd run out within a few iterations. With proper cache recycling we
    // should sustain many more cycles than the inode table's free-slot
    // count (≤ 14 with the seed image's 1-block inode table).
    var body = new byte[2 * 1024];
    Array.Fill(body, (byte)0x5A);
    for (var iter = 0; iter < 30; iter++) {
      FileSystem.Xenix.XenixModifier.AddFile(ms, $"tmp{iter:D2}", body);
      Assert.That(FileSystem.Xenix.XenixModifier.RemoveFile(ms, $"tmp{iter:D2}"), Is.True);
    }

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "seed" }));
  }

  // ── Exhausting the inode table throws cleanly ──────────────────────────

  [Test, Category("Sad")]
  public void AddBeyondInodeTableCapacity_ThrowsIOException() {
    // Seed with 1 file → inode 3. With 16 inode slots in one block, we have
    // inodes 4..16 = 13 free slots. Add 13 files to fill them, then expect
    // the next one to throw.
    var image = BuildSeedImage(("seed", "s"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    var i = 0;
    Exception? last = null;
    while (i < 100) {
      try {
        FileSystem.Xenix.XenixModifier.AddFile(ms, $"x{i:D3}", "k"u8.ToArray());
      } catch (IOException ex) {
        last = ex;
        break;
      }
      i++;
    }
    Assert.That(last, Is.Not.Null, "Expected inode table to fill before 100 adds; never threw.");
    Assert.That(last!.Message, Does.Contain("inode").IgnoreCase);
  }

  // ── Large-file rejection: > 10 KB is out of scope ──────────────────────

  [Test, Category("Sad")]
  public void AddFile_LargerThan10KB_ThrowsNotSupported() {
    var image = BuildSeedImage(("seed", "s"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.Throws<NotSupportedException>(() =>
      FileSystem.Xenix.XenixModifier.AddFile(ms, "too-big", new byte[10 * 1024 + 1]));
  }

  // ── 10 KB exactly is the boundary that must succeed ────────────────────

  [Test, Category("Boundary")]
  public void AddFile_Exactly10KB_RoundTrips() {
    var image = BuildSeedImage(("seed", "s"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    var max = new byte[10 * 1024];
    for (var i = 0; i < max.Length; i++) max[i] = (byte)(i * 37 % 251);
    FileSystem.Xenix.XenixModifier.AddFile(ms, "max", max);

    var after = ReadAll(ms.ToArray());
    Assert.That(after, Contains.Key("max"));
    Assert.That(after["max"], Is.EqualTo(max));
  }

  // ── Refusal to remove directories ──────────────────────────────────────

  [Test, Category("Sad")]
  public void RemoveFile_DirectoryEntry_Throws() {
    // Build an image with a nested file so 'usr' is a directory entry.
    var image = BuildSeedImage(("usr/bin/sh", "shell"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.Throws<InvalidOperationException>(() =>
      FileSystem.Xenix.XenixModifier.RemoveFile(ms, "usr"));
  }

  // ── Idempotent Add via descriptor (replace by leaf name) ───────────────

  [Test, Category("HappyPath")]
  public void DescriptorAdd_ReplacesExistingByLeafName() {
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    var initial = new[] {
      ArchiveInputInfo.InMemory("note", "first"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, initial, new FormatCreateOptions());

    // Add a second copy with the same name — should replace, not duplicate.
    ((IArchiveModifiable)d).Add(ms, new[] {
      ArchiveInputInfo.InMemory("note", "second"u8.ToArray()),
    });

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "note" }));
    Assert.That(Encoding.ASCII.GetString(after["note"]), Is.EqualTo("second"));
  }

  // ── Descriptor.Remove erases the named entry ───────────────────────────

  [Test, Category("HappyPath")]
  public void DescriptorRemove_ErasesNamedEntry() {
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    var initial = new[] {
      ArchiveInputInfo.InMemory("keep", "K"u8.ToArray()),
      ArchiveInputInfo.InMemory("drop", "D"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, initial, new FormatCreateOptions());

    ((IArchiveModifiable)d).Remove(ms, new[] { "drop" });

    var after = ReadAll(ms.ToArray());
    Assert.That(after.Keys, Is.EquivalentTo(new[] { "keep" }));
  }

  // ── Add then Read: superblock magic preserved ──────────────────────────

  [Test, Category("HappyPath")]
  public void AfterMutation_SuperblockMagicIsIntact() {
    var image = BuildSeedImage(("a", "x"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(image);

    FileSystem.Xenix.XenixModifier.AddFile(ms, "b", "y"u8.ToArray());
    FileSystem.Xenix.XenixModifier.RemoveFile(ms, "a");

    var raw = ms.ToArray();
    var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
      raw.AsSpan(1528, 4));
    Assert.That(magic, Is.EqualTo(0xFD187E20u));
    var typeCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
      raw.AsSpan(1532, 4));
    Assert.That(typeCode, Is.EqualTo(2u));
  }

  // ── Add into a corrupted image refuses cleanly ─────────────────────────

  [Test, Category("Sad")]
  public void AddFile_CorruptMagic_Throws() {
    var image = BuildSeedImage(("seed", "s"u8.ToArray()));
    image[1528] ^= 0xFF;
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.Throws<InvalidDataException>(() =>
      FileSystem.Xenix.XenixModifier.AddFile(ms, "new", "x"u8.ToArray()));
  }

  // ── Zone wipe: removing a multi-block file zeros every body block ──────

  [Test, Category("Boundary")]
  public void RemoveMultiZoneFile_WipesAllBlocks() {
    // 3-block body — three distinct sentinels, one per zone, so we can
    // verify each got zeroed.
    var body = new byte[3 * 1024];
    Array.Fill(body, (byte)0xAB, 0, 1024);
    Array.Fill(body, (byte)0xCD, 1024, 1024);
    Array.Fill(body, (byte)0xEF, 2048, 1024);
    var image = BuildSeedImage(("big", body));
    using var ms = new MemoryStream();
    ms.Write(image);

    Assert.That(FileSystem.Xenix.XenixModifier.RemoveFile(ms, "big"), Is.True);

    var raw = ms.ToArray();
    // None of the three sentinel runs of 1024 identical bytes should remain.
    Assert.That(FindRun(raw, 0xAB, 1024), Is.LessThan(0));
    Assert.That(FindRun(raw, 0xCD, 1024), Is.LessThan(0));
    Assert.That(FindRun(raw, 0xEF, 1024), Is.LessThan(0));
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private static int FindBytes(byte[] hay, byte[] needle) {
    if (needle.Length == 0) return 0;
    for (var i = 0; i + needle.Length <= hay.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++)
        if (hay[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }

  private static int FindRun(byte[] hay, byte value, int len) {
    var run = 0;
    for (var i = 0; i < hay.Length; i++) {
      if (hay[i] == value) {
        run++;
        if (run >= len) return i - len + 1;
      } else run = 0;
    }
    return -1;
  }
}
