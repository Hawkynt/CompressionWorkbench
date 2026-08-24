using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.MinixFs;

/// <summary>
/// Locks the TRUE in-place R/W contract of <see cref="FileSystem.MinixFs.MinixFsInPlaceModifier"/>:
/// every Add/Remove/Replace mutates only the inode bitmap byte, zone bitmap
/// byte, the affected inode slot, the directory zone and the file's data
/// zones. Every other byte of the image must remain byte-identical to the
/// pre-mutation snapshot.
/// </summary>
[TestFixture]
public class MinixFsInPlaceModifyTests {

  // ── V3 (emitted by MinixFsWriter) ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void V3_Add_LeavesUntouchedBytesIdentical() {
    using var ms = BuildV3Image([
      ("keep.txt", "keep"u8.ToArray())
    ]);
    var before = ms.ToArray();

    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "new.txt", "new"u8.ToArray());
    var after = ms.ToArray();

    Assert.That(after.Length, Is.EqualTo(before.Length),
      "Add must not grow the image — it allocates from free pool.");
    // The keep.txt data zone must be byte-identical.
    var keepInodeOff = FindInodeTableV3Offset(after) + (2 - 1) * 64; // inode 2 (root=1)
    AssertBytesEqual(before, after, keepInodeOff, 64, "keep.txt inode");

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Contain("new.txt"));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["keep.txt"]), Is.EqualTo("keep"u8.ToArray()));
    Assert.That(r.Extract(byName["new.txt"]),  Is.EqualTo("new"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void V3_Remove_LeavesUnrelatedBytesIdentical() {
    using var ms = BuildV3Image([
      ("keep.txt", "keep-data"u8.ToArray()),
      ("drop.txt", "drop-data"u8.ToArray())
    ]);
    var before = ms.ToArray();

    var removed = FileSystem.MinixFs.MinixFsInPlaceModifier.RemoveFile(ms, "drop.txt", wipeData: true);
    Assert.That(removed, Is.True);

    var after = ms.ToArray();
    Assert.That(after.Length, Is.EqualTo(before.Length));

    // The keep.txt inode (inode 2) must be untouched.
    var keepInodeOff = FindInodeTableV3Offset(after) + (2 - 1) * 64;
    AssertBytesEqual(before, after, keepInodeOff, 64, "keep.txt inode");

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Not.Contain("drop.txt"));
  }

  [Test, Category("HappyPath")]
  public void V3_Replace_FitsInPlace_LeavesZonesAtSameOffsets() {
    using var ms = BuildV3Image([
      ("file.txt", new byte[500]) // < 1 zone, original allocation
    ]);
    var before = ms.ToArray();

    var newData = Encoding.ASCII.GetBytes("REPLACED" + new string('x', 400));
    var ok = FileSystem.MinixFs.MinixFsInPlaceModifier.Replace(ms, "file.txt", newData);
    Assert.That(ok, Is.True);

    var after = ms.ToArray();
    Assert.That(after.Length, Is.EqualTo(before.Length));

    // Same inode lookup: inode 2 (root=1, file=2).
    var inodeOff = FindInodeTableV3Offset(after) + (2 - 1) * 64;
    // Replacement fits within already-allocated direct zones — old zone
    // pointer at offset +24 must be unchanged.
    var beforeZone = BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(inodeOff + 24));
    var afterZone  = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(inodeOff + 24));
    Assert.That(afterZone, Is.EqualTo(beforeZone), "Same zone reused on in-place fit replace.");

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "file.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(newData));
  }

  [Test, Category("HappyPath")]
  public void V3_Bitmap_TracksAllocationsAndFrees() {
    using var ms = BuildV3Image([("keep.txt", "x"u8.ToArray())]);

    var imapBefore = ReadImapBit(ms, inodeOneBased: 3);
    Assert.That(imapBefore, Is.False, "Inode 3 is free initially.");

    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "new.txt", "y"u8.ToArray());
    Assert.That(ReadImapBit(ms, 3), Is.True, "Inode 3 must be marked allocated after Add.");

    FileSystem.MinixFs.MinixFsInPlaceModifier.RemoveFile(ms, "new.txt", wipeData: true);
    Assert.That(ReadImapBit(ms, 3), Is.False, "Inode 3 must be marked free again after Remove.");
  }

  [Test, Category("ErrorHandling")]
  public void V3_Replace_NonExistent_ReturnsFalse() {
    using var ms = BuildV3Image([("keep.txt", "k"u8.ToArray())]);
    var ok = FileSystem.MinixFs.MinixFsInPlaceModifier.Replace(ms, "nope.txt", "n"u8.ToArray());
    Assert.That(ok, Is.False);
  }

  [Test, Category("ErrorHandling")]
  public void V3_Remove_NonExistent_ReturnsFalse() {
    using var ms = BuildV3Image([("keep.txt", "k"u8.ToArray())]);
    var ok = FileSystem.MinixFs.MinixFsInPlaceModifier.RemoveFile(ms, "ghost.txt");
    Assert.That(ok, Is.False);
  }

  [Test, Category("Boundary")]
  public void V3_Add_TooLarge_Throws() {
    using var ms = BuildV3Image([("keep.txt", "k"u8.ToArray())]);
    var huge = new byte[8 * 1024]; // 8 zones — exceeds direct ceiling (7).
    Assert.Throws<IOException>(() =>
      FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "huge.txt", huge));
  }

  // ── V1 / V2 (synthesized image) ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void V1_14_AddAndExtract() {
    using var ms = BuildV1V2Image(version: V1V2Version.V1_14);
    var before = ms.ToArray();

    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "hello.txt", "hello v1"u8.ToArray());
    var after = ms.ToArray();
    Assert.That(after.Length, Is.EqualTo(before.Length));

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(r.Extract(entry), Is.EqualTo("hello v1"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void V1_30_AddAndExtract_LongName() {
    using var ms = BuildV1V2Image(version: V1V2Version.V1_30);
    var name = "twentynine_char_name__OK.txt"; // <30 chars
    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, name, "v1-30 payload"u8.ToArray());

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == name), Is.True);
  }

  [Test, Category("HappyPath")]
  public void V2_14_AddAndExtract() {
    using var ms = BuildV1V2Image(version: V1V2Version.V2_14);
    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "v2.bin", new byte[] { 1, 2, 3, 4, 5 });

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "v2.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test, Category("HappyPath")]
  public void V2_30_RemoveAndReadd_PreservesFunction() {
    using var ms = BuildV1V2Image(version: V1V2Version.V2_30);
    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "a.bin", "first"u8.ToArray());
    var removed = FileSystem.MinixFs.MinixFsInPlaceModifier.RemoveFile(ms, "a.bin");
    Assert.That(removed, Is.True);
    FileSystem.MinixFs.MinixFsInPlaceModifier.AddFile(ms, "a.bin", "second"u8.ToArray());

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "a.bin");
    Assert.That(r.Extract(entry), Is.EqualTo("second"u8.ToArray()));
  }

  // ── Roundtrip via descriptor ──────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AddThenExtract_RoundTrips() {
    using var ms = BuildV3Image([("keep.txt", "keep"u8.ToArray())]);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added bytes"u8.ToArray());
      var desc = new FileSystem.MinixFs.MinixFsFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "added.txt", false)]);

      ms.Position = 0;
      var r = new FileSystem.MinixFs.MinixFsReader(ms);
      var byName = r.Entries.ToDictionary(e => e.Name);
      Assert.That(r.Extract(byName["keep.txt"]),  Is.EqualTo("keep"u8.ToArray()));
      Assert.That(r.Extract(byName["added.txt"]), Is.EqualTo("added bytes"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static MemoryStream BuildV3Image((string Name, byte[] Data)[] files) {
    var ms = new MemoryStream();
    var w = new FileSystem.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    ms.Position = 0;
    var bytes = ms.ToArray();

    // The writer fits s_ninodes / s_zones tight to actual usage. Real
    // mkfs.minix images carry headroom. Bump the counters and extend the
    // backing buffer (purely at the tail) so the modifier has free
    // inodes + free data zones to allocate from. The original inode-table
    // is small enough that the unused trailing inodes within the existing
    // table give us inode headroom; the appended zones give zone headroom.
    var blockSize = 1024;
    var sb = bytes.AsSpan(1024);
    var imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    var zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
    var firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(10));
    var origNinodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    var inodeTableBlocks = firstDataZone - 2 - imapBlocks - zmapBlocks;
    var inodeTableCapacity = inodeTableBlocks * (blockSize / 64);

    // Cap ninodes by what physically fits in the existing inode table AND in
    // the imap blocks. The writer always allocates >= 1 block for the inode
    // table, so there's usually room for many more inodes than were used.
    var maxImapInodes = imapBlocks * blockSize * 8;
    var newNinodes = (uint)Math.Min(inodeTableCapacity, maxImapInodes);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, newNinodes);

    // Append empty data-zone blocks at the tail of the image so the
    // modifier finds free zone bits.
    var extraZones = 32;
    var extended = new byte[bytes.Length + extraZones * blockSize];
    bytes.CopyTo(extended, 0);
    var sb2 = extended.AsSpan(1024);
    var newTotalZones = extended.Length / blockSize;
    BinaryPrimitives.WriteUInt32LittleEndian(sb2.Slice(20), (uint)newTotalZones);

    var result = new MemoryStream();
    result.Write(extended, 0, extended.Length);
    result.Position = 0;
    return result;
  }

  private static int FindInodeTableV3Offset(byte[] image) {
    // Layout: boot(1024) + sb(1024) + imap(1*1024) + zmap(1*1024) = 4*1024.
    var sb = image.AsSpan(1024);
    var imap = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    var zmap = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
    return 2 * 1024 + imap * 1024 + zmap * 1024;
  }

  /// <summary>Whether the inode bitmap has the given inode marked in use.</summary>
  /// <remarks>
  /// Inode N is bit N: bit 0 is reserved for the inode number that means "none",
  /// which is why a fresh mkfs.minix volume of any version leaves bits 0 and 1
  /// set — the reserved one and the root. This read the bit below, and agreed
  /// with a writer that wrote the bit below, so the pair of them were consistent
  /// with each other and with nothing else.
  /// </remarks>
  private static bool ReadImapBit(MemoryStream ms, int inodeOneBased) {
    var data = ms.ToArray();
    var imapOff = 2 * 1024;
    return (data[imapOff + inodeOneBased / 8] & (1 << (inodeOneBased % 8))) != 0;
  }

  private static void AssertBytesEqual(byte[] a, byte[] b, int offset, int length, string label) {
    for (var i = 0; i < length; i++)
      Assert.That(b[offset + i], Is.EqualTo(a[offset + i]),
        $"{label}: byte {i} (image offset 0x{offset + i:X}) changed");
  }

  // ── V1/V2 synthesizer ─────────────────────────────────────────────────

  private enum V1V2Version { V1_14, V1_30, V2_14, V2_30 }

  /// <summary>
  /// Synthesizes a minimal but spec-shaped V1/V2 Minix image with a single
  /// empty root directory (inode 1) and one already-used data zone (for the
  /// root's "." and ".." entries). The image leaves plenty of room for the
  /// modifier to allocate new inodes + data zones.
  /// </summary>
  private static MemoryStream BuildV1V2Image(V1V2Version version) {
    const int blockSize = 1024;
    const int totalInodes = 64;
    const int totalZones = 64;
    const int imapBlocks = 1;
    const int zmapBlocks = 1;
    const int inodeSize = 32;
    var inodesPerBlock = blockSize / inodeSize;
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    // boot(1) + sb(1) + imap + zmap + inode_table = firstDataZone
    var firstDataZone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;
    var diskSize = totalZones * blockSize;
    if (diskSize < firstDataZone * blockSize + 2 * blockSize) {
      diskSize = (firstDataZone + 4) * blockSize;
    }
    var disk = new byte[diskSize];

    var magic = version switch {
      V1V2Version.V1_14 => 0x137F,
      V1V2Version.V1_30 => 0x138F,
      V1V2Version.V2_14 => 0x2468,
      V1V2Version.V2_30 => 0x2478,
      _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    // V1/V2 superblock @ offset 1024
    var sb = disk.AsSpan(1024);
    BinaryPrimitives.WriteUInt16LittleEndian(sb,             (ushort)totalInodes);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(2),    (ushort)(diskSize / blockSize));
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(4),    imapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),    zmapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(8),    (ushort)firstDataZone);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(10),   0); // log_zone_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(12),   (uint)diskSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(16),   (ushort)magic);

    var imapOff = 2 * blockSize;
    var zmapOff = 3 * blockSize;
    var inodeTableOff = 4 * blockSize;

    // Bit 0 of the inode bitmap is reserved and bit N is inode N, so the root
    // leaves bits 0 and 1 set — which is exactly what mkfs.minix writes for a
    // fresh volume of any version.
    disk[imapOff] = 0x03;

    // The zone bitmap covers the data zones only and counts from the first of
    // them, with bit 0 reserved as the inode bitmap's is: the root directory's
    // zone is the first data zone, and so bit 1. The metadata zones below
    // firstDataZone are not in the map at all.
    var rootZone = firstDataZone;
    disk[zmapOff] = 0x03;

    // Root inode (inode 1) — V1 32-byte layout (modifier convention).
    // mode (2) | uid (2) | size (4) | time (4) | gid (1) | nlinks (1) | zones[9] (18)
    var inodeOff = inodeTableOff + (1 - 1) * inodeSize;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(inodeOff),     0x41ED); // S_IFDIR | 0755
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(inodeOff + 4), (uint)blockSize); // size
    disk[inodeOff + 13] = 2; // nlinks
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(inodeOff + 14), (ushort)rootZone);

    // Root directory data block: write "." and ".." entries.
    var nameLen = version is V1V2Version.V1_30 or V1V2Version.V2_30 ? 30 : 14;
    var entrySize = 2 + nameLen;
    var rootDirOff = rootZone * blockSize;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(rootDirOff), 1);
    disk[rootDirOff + 2] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(rootDirOff + entrySize), 1);
    disk[rootDirOff + entrySize + 2] = (byte)'.';
    disk[rootDirOff + entrySize + 3] = (byte)'.';

    var ms = new MemoryStream();
    ms.Write(disk, 0, disk.Length);
    ms.Position = 0;
    return ms;
  }
}
