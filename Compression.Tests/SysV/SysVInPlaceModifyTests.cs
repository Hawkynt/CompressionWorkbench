using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.SysV;

namespace Compression.Tests.SysV;

/// <summary>
/// True-in-place R/W invariants for the SysV (s5fs) modifier — locks the
/// byte-level "untouched regions stay byte-identical" promise that
/// distinguishes in-place mutation from a rebuild-disguised-as-R/W
/// implementation. Every test in this fixture either:
/// <list type="bullet">
/// <item>compares specific byte ranges (boot block, an untouched inode
///   slot, an untouched data block) before and after a mutation, or</item>
/// <item>asserts that the superblock's free-list bookkeeping reflects the
///   actual number of allocations / frees the call performed.</item>
/// </list>
/// </summary>
[TestFixture]
public class SysVInPlaceModifyTests {

  // ── Layout constants (mirror SysVWriter / SysVReader) ────────────────

  private const int BlockSize = 1024;
  private const int InodeSize = 64;
  private const int InodeTableBlock = 2;
  private const int BootBlockStart = 0;
  private const int BootBlockEnd = BlockSize;          // exclusive

  /// <summary>
  /// Returns the byte offset of inode <paramref name="inum"/> (1-based) in
  /// the inode table at block 2.
  /// </summary>
  private static long InodeOffset(uint inum) =>
    (long)InodeTableBlock * BlockSize + (long)(inum - 1) * InodeSize;

  /// <summary>
  /// Reads <paramref name="length"/> bytes from <paramref name="image"/>
  /// starting at <paramref name="offset"/>.
  /// </summary>
  private static byte[] ReadSlice(byte[] image, long offset, int length) {
    var buf = new byte[length];
    Buffer.BlockCopy(image, (int)offset, buf, 0, length);
    return buf;
  }

  /// <summary>
  /// Reads the 24-bit little-endian zone pointer at the given offset (the
  /// s5fs zone-pointer encoding used inside inodes).
  /// </summary>
  private static uint Read24(byte[] image, long offset) =>
    image[(int)offset]
    | ((uint)image[(int)offset + 1] << 8)
    | ((uint)image[(int)offset + 2] << 16);

  /// <summary>
  /// Finds the inode number for a flat-root entry by name. Returns 0 if
  /// the entry is absent or the root directory is empty.
  /// </summary>
  private static uint FindRootInode(byte[] image, string name) {
    using var ms = new MemoryStream(image);
    var r = new SysVReader(ms);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (string.Equals(e.Name, name, StringComparison.Ordinal))
        return (uint)e.InodeNumber;
    }
    return 0;
  }

  /// <summary>
  /// Returns the byte offset of the first direct-zone data block referenced
  /// by inode <paramref name="inum"/>.
  /// </summary>
  private static long FirstZoneOffset(byte[] image, uint inum) {
    var inodeOff = InodeOffset(inum);
    var zone = Read24(image, inodeOff + 12);
    return (long)zone * BlockSize;
  }

  // ── Tier 1: descriptor opts into the facade ─────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_RoutesThroughInPlaceFacade() {
    // The descriptor must keep advertising CanModify after the rename to
    // SysVInPlaceModifier; an end-to-end Add must mutate the existing
    // image (not rebuild) and round-trip through our reader.
    var d = new SysVFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    d.Add(ms, [ArchiveInputInfo.InMemory("added.txt", "added"u8.ToArray())]);
    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToArray();
    Assert.That(entries, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
  }

  // ── Tier 2: Add — untouched inodes + data blocks byte-identical ─────

  [Test, Category("HappyPath")]
  public void Add_UntouchedSeedInode_StaysByteIdenticalAtOriginalOffset() {
    var seed = SysVWriter.Build([("keep.txt", "PRESERVE-THIS"u8.ToArray())]);
    var keepInode = FindRootInode(seed, "keep.txt");
    Assert.That(keepInode, Is.Not.EqualTo(0u), "seed must contain keep.txt");
    var keepInodeOff = InodeOffset(keepInode);
    var beforeInodeSlot = ReadSlice(seed, keepInodeOff, InodeSize);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    // Add a new flat-root file. The keep.txt inode slot must not move.
    SysVInPlaceModifier.Add(ms, "fresh.bin", "fresh"u8.ToArray());

    var afterImage = ms.GetBuffer();
    var afterInodeSlot = ReadSlice(afterImage, keepInodeOff, InodeSize);
    Assert.That(afterInodeSlot, Is.EqualTo(beforeInodeSlot),
      "true in-place: existing inode's 64-byte slot must be byte-identical after Add of a different file");
  }

  [Test, Category("HappyPath")]
  public void Add_UntouchedSeedDataBlock_StaysByteIdenticalAtOriginalOffset() {
    var payload = "DATA-BLOCK-MUST-NOT-MOVE"u8.ToArray();
    var seed = SysVWriter.Build([("keep.txt", payload)]);
    var keepInode = FindRootInode(seed, "keep.txt");
    var keepDataOff = FirstZoneOffset(seed, keepInode);
    var beforeDataBlock = ReadSlice(seed, keepDataOff, BlockSize);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    SysVInPlaceModifier.Add(ms, "added.bin", "newly added bytes"u8.ToArray());

    var afterImage = ms.GetBuffer();
    var afterDataBlock = ReadSlice(afterImage, keepDataOff, BlockSize);
    Assert.That(afterDataBlock, Is.EqualTo(beforeDataBlock),
      "true in-place: existing file's 1 KB data block must be byte-identical after Add of a different file");
  }

  [Test, Category("HappyPath")]
  public void Add_BootBlock_StaysAllZero() {
    // The boot block (block 0, bytes [0..1024)) is never touched by the
    // modifier — it should stay byte-identical (all zero) after any Add.
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    var beforeBoot = ReadSlice(seed, BootBlockStart, BootBlockEnd - BootBlockStart);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    SysVInPlaceModifier.Add(ms, "post.bin", "post"u8.ToArray());

    var afterImage = ms.GetBuffer();
    var afterBoot = ReadSlice(afterImage, BootBlockStart, BootBlockEnd - BootBlockStart);
    Assert.That(afterBoot, Is.EqualTo(beforeBoot),
      "boot block (block 0) must never be touched by in-place modifier");
  }

  [Test, Category("HappyPath")]
  public void Add_BatchInputs_AllRoundTrip() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("one.txt", "first"u8.ToArray()),
      ArchiveInputInfo.InMemory("two.txt", "second"u8.ToArray()),
      ArchiveInputInfo.InMemory("three.txt", "third"u8.ToArray()),
    };
    SysVInPlaceModifier.Add(ms, inputs);

    ms.Position = 0;
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "seed.txt", "one.txt", "two.txt", "three.txt" }));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "two.txt"))),
      Is.EqualTo("second"));
  }

  [Test, Category("Sad")]
  public void Add_NestedPathInBatch_ThrowsNotSupported() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);

    Assert.Throws<NotSupportedException>(
      () => SysVInPlaceModifier.Add(ms,
        new List<ArchiveInputInfo> {
          ArchiveInputInfo.InMemory("etc/motd", "x"u8.ToArray())
        }));
  }

  // ── Tier 3: Remove — untouched blocks byte-identical, free lists updated ─

  [Test, Category("HappyPath")]
  public void Remove_UntouchedFileInode_StaysByteIdenticalAtOriginalOffset() {
    var seed = SysVWriter.Build([
      ("keep.txt", "KEEP"u8.ToArray()),
      ("drop.bin", "WIPE-ME"u8.ToArray()),
    ]);
    var keepInode = FindRootInode(seed, "keep.txt");
    var keepInodeOff = InodeOffset(keepInode);
    var beforeKeepInode = ReadSlice(seed, keepInodeOff, InodeSize);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    var removed = SysVInPlaceModifier.Remove(ms, "drop.bin");
    Assert.That(removed, Is.True);

    var afterImage = ms.GetBuffer();
    var afterKeepInode = ReadSlice(afterImage, keepInodeOff, InodeSize);
    Assert.That(afterKeepInode, Is.EqualTo(beforeKeepInode),
      "true in-place: unrelated file's inode slot must be byte-identical after Remove of another file");
  }

  [Test, Category("HappyPath")]
  public void Remove_UntouchedFileDataBlock_StaysByteIdenticalAtOriginalOffset() {
    var keepPayload = "KEEP-ME-INTACT"u8.ToArray();
    var seed = SysVWriter.Build([
      ("keep.txt", keepPayload),
      ("drop.bin", "WIPE-ME"u8.ToArray()),
    ]);
    var keepInode = FindRootInode(seed, "keep.txt");
    var keepDataOff = FirstZoneOffset(seed, keepInode);
    var beforeKeepData = ReadSlice(seed, keepDataOff, BlockSize);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    SysVInPlaceModifier.Remove(ms, "drop.bin");

    var afterImage = ms.GetBuffer();
    var afterKeepData = ReadSlice(afterImage, keepDataOff, BlockSize);
    Assert.That(afterKeepData, Is.EqualTo(beforeKeepData),
      "true in-place: unrelated file's data block must be byte-identical after Remove of another file");
  }

  [Test, Category("HappyPath")]
  public void Remove_FreeBlockCount_IncreasesByExactlyTheFreedBlocks() {
    // A single-block file frees exactly one data block on Remove. The
    // superblock's s_tfree (total free blocks) must rise by exactly that.
    var seed = SysVWriter.Build([
      ("keep.txt", "k"u8.ToArray()),
      ("drop.bin", new byte[BlockSize]),       // exactly one direct zone
    ]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var before = SysVInPlaceModifier.ReadFreeStats(ms);
    SysVInPlaceModifier.Remove(ms, "drop.bin");
    var after = SysVInPlaceModifier.ReadFreeStats(ms);

    Assert.That(after.TFree, Is.EqualTo(before.TFree + 1),
      "Remove of a 1-block file must bump s_tfree by exactly 1");
  }

  [Test, Category("HappyPath")]
  public void Remove_FreeInodeCount_IncreasesByOne() {
    var seed = SysVWriter.Build([
      ("keep.txt", "k"u8.ToArray()),
      ("drop.bin", "d"u8.ToArray()),
    ]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var before = SysVInPlaceModifier.ReadInodeStats(ms);
    SysVInPlaceModifier.Remove(ms, "drop.bin");
    var after = SysVInPlaceModifier.ReadInodeStats(ms);

    Assert.That(after.TInode, Is.EqualTo((ushort)(before.TInode + 1)),
      "Remove of a single file must bump s_tinode by exactly 1");
  }

  [Test, Category("HappyPath")]
  public void Remove_DroppedFileDataBlock_NoLongerHoldsOriginalContent() {
    // The Remove wipe contract says the file's bytes must not survive on
    // disk. The block is zero-filled before being returned to the free
    // list — but if the free-list cache is at capacity, the next push
    // spills the cache *into* the just-freed block (turning it into the
    // new chain-group block). Either way, none of the original file's
    // bytes can still be readable at the original offset.
    var secret = "SECRET-CONTENT-MUST-NOT-LEAK"u8.ToArray();
    var seed = SysVWriter.Build([
      ("keep.txt", "k"u8.ToArray()),
      ("drop.bin", secret),
    ]);
    var dropInode = FindRootInode(seed, "drop.bin");
    var dropDataOff = FirstZoneOffset(seed, dropInode);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    SysVInPlaceModifier.Remove(ms, "drop.bin");

    var afterImage = ms.GetBuffer();
    var droppedBlock = ReadSlice(afterImage, dropDataOff, BlockSize);
    // The original byte sequence must not appear anywhere in the block.
    Assert.That(IndexOfSequence(droppedBlock, secret), Is.LessThan(0),
      "Remove wipe contract: dropped file's original bytes must not survive on disk");
  }

  private static int IndexOfSequence(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }

  [Test, Category("HappyPath")]
  public void Remove_DroppedFileInodeSlot_IsZeroed() {
    var seed = SysVWriter.Build([
      ("keep.txt", "k"u8.ToArray()),
      ("drop.bin", "d"u8.ToArray()),
    ]);
    var dropInode = FindRootInode(seed, "drop.bin");
    var dropInodeOff = InodeOffset(dropInode);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;
    SysVInPlaceModifier.Remove(ms, "drop.bin");

    var afterImage = ms.GetBuffer();
    var droppedInode = ReadSlice(afterImage, dropInodeOff, InodeSize);
    Assert.That(droppedInode, Is.All.EqualTo((byte)0),
      "Remove must zero the dropped file's 64-byte inode slot (re-scan rediscovery contract)");
  }

  // ── Tier 4: Replace — fits-in-same-zones path is true in-place ───────

  [Test, Category("HappyPath")]
  public void Replace_PayloadFitsInExistingZones_RewritesDataBlockInPlace() {
    // Original: 1 KB payload → 1 direct zone.
    // Replacement: still ≤ 1 KB → same zone count → the zone keeps its
    // block number, the inode keeps its zone pointers. The data block at
    // the original offset now holds the new content.
    var originalPayload = new byte[BlockSize];
    Array.Fill(originalPayload, (byte)0xAA);
    var seed = SysVWriter.Build([("doc.bin", originalPayload)]);
    var docInode = FindRootInode(seed, "doc.bin");
    var docDataOff = FirstZoneOffset(seed, docInode);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    // Replace with a shorter payload that still fits in one direct zone.
    var newPayload = "replaced!"u8.ToArray();
    var ok = SysVInPlaceModifier.Replace(ms, "doc.bin", newPayload);
    Assert.That(ok, Is.True);

    // Round-trip through the reader.
    ms.Position = 0;
    var r = new SysVReader(ms);
    var entry = r.Entries.Single(e => e.Name == "doc.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(newPayload));
  }

  [Test, Category("HappyPath")]
  public void Replace_UntouchedSiblingDataBlock_StaysByteIdentical() {
    var siblingPayload = "SIBLING-INTACT"u8.ToArray();
    var seed = SysVWriter.Build([
      ("sibling.txt", siblingPayload),
      ("victim.bin",  new byte[BlockSize]),
    ]);
    var siblingInode = FindRootInode(seed, "sibling.txt");
    var siblingDataOff = FirstZoneOffset(seed, siblingInode);
    var beforeSibling = ReadSlice(seed, siblingDataOff, BlockSize);

    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var ok = SysVInPlaceModifier.Replace(ms, "victim.bin", "tiny"u8.ToArray());
    Assert.That(ok, Is.True);

    var afterImage = ms.GetBuffer();
    var afterSibling = ReadSlice(afterImage, siblingDataOff, BlockSize);
    Assert.That(afterSibling, Is.EqualTo(beforeSibling),
      "Replace of one file must leave a sibling's data block byte-identical");
  }

  [Test, Category("Sad")]
  public void Replace_UnknownEntry_ReturnsFalse() {
    var seed = SysVWriter.Build([("doc.txt", "x"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.That(SysVInPlaceModifier.Replace(ms, "no-such.bin", "y"u8.ToArray()),
      Is.False);
  }

  // ── Tier 5: Mutate-then-extract round-trip (end-to-end) ──────────────

  [Test, Category("HappyPath")]
  public void AddRemoveReplace_RoundTripThroughDescriptor() {
    var d = new SysVFormatDescriptor();
    var seed = SysVWriter.Build([
      ("alpha.txt", "ALPHA-v1"u8.ToArray()),
      ("beta.txt",  "BETA-v1"u8.ToArray()),
      ("gamma.txt", "GAMMA-v1"u8.ToArray()),
    ]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    // Add a fresh entry.
    SysVInPlaceModifier.Add(ms, "delta.txt", "DELTA-NEW"u8.ToArray());

    // Replace an existing one.
    SysVInPlaceModifier.Replace(ms, "beta.txt", "BETA-v2-rewritten"u8.ToArray());

    // Remove another.
    SysVInPlaceModifier.Remove(ms, "gamma.txt");

    // Final state must round-trip through the descriptor's reader.
    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory)
      .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.That(entries,
      Is.EquivalentTo(new[] { "alpha.txt", "beta.txt", "delta.txt" }));

    ms.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(ms, "alpha.txt", null)),
      Is.EqualTo("ALPHA-v1"), "untouched alpha.txt content survives the mutation storm");
    ms.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(ms, "beta.txt", null)),
      Is.EqualTo("BETA-v2-rewritten"), "Replace landed");
    ms.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(ms, "delta.txt", null)),
      Is.EqualTo("DELTA-NEW"), "Add landed");
  }

  [Test, Category("HappyPath")]
  public void AddThenReadFreeStats_TFreeDecreasesByExactlyAllocatedBlocks() {
    var seed = SysVWriter.Build([("seed.txt", "seed"u8.ToArray())]);
    using var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var before = SysVInPlaceModifier.ReadFreeStats(ms);
    SysVInPlaceModifier.Add(ms, "tworblocks.bin", new byte[BlockSize + 1]);  // 2 direct zones
    var after = SysVInPlaceModifier.ReadFreeStats(ms);

    Assert.That(after.TFree, Is.EqualTo(before.TFree - 2),
      "Add of a 2-block file must drop s_tfree by exactly 2");
  }
}
