using System.Buffers.Binary;

namespace Compression.Tests.Qcow2;

[TestFixture]
public class Qcow2Tests {

  // ── Synthetic QCOW2 builder ────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal valid QCOW2 v2 image in memory.
  /// Uses cluster_bits=12 (4 KB clusters) for compact test images.
  /// The caller provides a map of cluster index -> raw data (or null for zero clusters).
  /// </summary>
  private static byte[] BuildQcow2(long virtualSize,
      int clusterBits = 12,
      Dictionary<int, byte[]>? clusterData = null) {
    var clusterSize = 1 << clusterBits;
    var l2Entries = clusterSize / 8; // 512 for cluster_bits=12

    var totalClusters = (int)((virtualSize + clusterSize - 1) / clusterSize);
    var l1Size = Math.Max(1, (totalClusters + l2Entries - 1) / l2Entries);

    // Layout (all clusters aligned to clusterSize):
    //   cluster 0: header (72 bytes, rest zeroed)
    //   cluster 1: L1 table
    //   cluster 2..2+l1Size-1: L2 tables (one per L1 entry)
    //   cluster 2+l1Size..: data clusters

    var l1Offset = clusterSize;                         // cluster 1
    var l2BaseCluster = 2;                              // clusters 2..
    var dataBaseCluster = l2BaseCluster + l1Size;       // after all L2 tables

    // Count data clusters needed
    var usedDataClusters = clusterData?.Count ?? 0;
    var totalImageClusters = dataBaseCluster + usedDataClusters;
    var imageSize = totalImageClusters * clusterSize;

    var img = new byte[imageSize];

    // ── Header (72 bytes) ─────────────────────────────────────────────
    // magic
    img[0] = 0x51; img[1] = 0x46; img[2] = 0x49; img[3] = 0xFB;
    // version = 2
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(4), 2);
    // backing_file_offset = 0, backing_file_size = 0 (already zero)
    // cluster_bits
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(20), (uint)clusterBits);
    // virtual size
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(24), (ulong)virtualSize);
    // crypt_method = 0 (already zero)
    // l1_size
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(36), (uint)l1Size);
    // l1_table_offset
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(40), (ulong)l1Offset);

    // ── L1 table ─────────────────────────────────────────────────────
    // Each L1 entry points to an L2 table cluster.
    for (var l1Idx = 0; l1Idx < l1Size; l1Idx++) {
      var l2ClusterOffset = (ulong)((l2BaseCluster + l1Idx) * clusterSize);
      var l1EntryOffset = l1Offset + l1Idx * 8;
      // L1 entry: host offset of L2 table (bits [9..55] significant; low 9 bits = flags = 0)
      BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(l1EntryOffset), l2ClusterOffset);
    }

    // ── L2 tables and data clusters ──────────────────────────────────
    var nextDataCluster = dataBaseCluster;

    if (clusterData != null) {
      foreach (var (clusterIdx, data) in clusterData) {
        var l1Idx = clusterIdx / l2Entries;
        var l2Idx = clusterIdx % l2Entries;

        if (l1Idx >= l1Size) continue;

        var l2ClusterOffset = (l2BaseCluster + l1Idx) * clusterSize;
        var l2EntryOffset = l2ClusterOffset + l2Idx * 8;

        var hostClusterOffset = (ulong)(nextDataCluster * clusterSize);
        // Uncompressed L2 entry: host cluster offset (low bits 0..8 are flags = 0)
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(l2EntryOffset), hostClusterOffset);

        // Write cluster data
        var writeLen = Math.Min(data.Length, clusterSize);
        data.AsSpan(0, writeLen).CopyTo(img.AsSpan((int)hostClusterOffset));

        nextDataCluster++;
      }
    }

    return img;
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>Creates a deterministic fill pattern: byte = (offset * 37 + seed) % 251.</summary>
  private static byte[] MakePattern(int length, byte seed = 0xAB) {
    var buf = new byte[length];
    for (var i = 0; i < length; i++)
      buf[i] = (byte)((i * 37 + seed) % 251);
    return buf;
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Qcow2"));
    Assert.That(desc.DisplayName, Is.EqualTo("QCOW2"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".qcow2"));
    Assert.That(desc.Extensions, Does.Contain(".qcow2"));
    Assert.That(desc.Extensions, Does.Contain(".qcow"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x51, 0x46, 0x49, 0xFB }));
    Assert.That(desc.MagicSignatures[0].Confidence, Is.EqualTo(0.95));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList),    Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest),    Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    var data = new byte[512];
    // Wrong magic: leave bytes 0-3 as 0x00
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Qcow2.Qcow2Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Qcow2.Qcow2Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Read_SyntheticImage_SingleCluster() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;
    var content = MakePattern(clusterSize);

    var img = BuildQcow2(
      virtualSize: clusterSize,
      clusterBits: clusterBits,
      clusterData: new Dictionary<int, byte[]> { [0] = content });

    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(clusterSize));
    Assert.That(reader.VirtualSize, Is.EqualTo(clusterSize));
  }

  [Test, Category("HappyPath")]
  public void Read_UncompressedCluster_ReturnsClusterData() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;
    var content = MakePattern(clusterSize, seed: 0x7C);

    var img = BuildQcow2(
      virtualSize: clusterSize,
      clusterBits: clusterBits,
      clusterData: new Dictionary<int, byte[]> { [0] = content });

    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);
    var extracted = reader.ExtractDisk();

    Assert.That(extracted.Length, Is.EqualTo(clusterSize));
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_ZeroCluster_ReturnsZeros() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;

    // Build image with virtualSize = 2 clusters but only cluster 1 has data; cluster 0 is zero
    var content = MakePattern(clusterSize, seed: 0x55);

    var img = BuildQcow2(
      virtualSize: clusterSize * 2,
      clusterBits: clusterBits,
      clusterData: new Dictionary<int, byte[]> { [1] = content });

    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);
    var extracted = reader.ExtractDisk();

    Assert.That(extracted.Length, Is.EqualTo(clusterSize * 2));
    // First cluster (index 0) has no L2 entry data → must be all zeros
    var firstCluster = extracted.AsSpan(0, clusterSize).ToArray();
    Assert.That(firstCluster, Is.EqualTo(new byte[clusterSize]));
    // Second cluster (index 1) must contain our pattern
    var secondCluster = extracted.AsSpan(clusterSize, clusterSize).ToArray();
    Assert.That(secondCluster, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleUncompressedClusters() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;

    var c0 = MakePattern(clusterSize, seed: 0x11);
    var c1 = MakePattern(clusterSize, seed: 0x22);
    var c2 = MakePattern(clusterSize, seed: 0x33);

    var img = BuildQcow2(
      virtualSize: clusterSize * 3,
      clusterBits: clusterBits,
      clusterData: new Dictionary<int, byte[]> {
        [0] = c0,
        [1] = c1,
        [2] = c2,
      });

    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);
    var extracted = reader.ExtractDisk();

    Assert.That(extracted.Length, Is.EqualTo(clusterSize * 3));
    Assert.That(extracted.AsSpan(0, clusterSize).ToArray(), Is.EqualTo(c0));
    Assert.That(extracted.AsSpan(clusterSize, clusterSize).ToArray(), Is.EqualTo(c1));
    Assert.That(extracted.AsSpan(clusterSize * 2, clusterSize).ToArray(), Is.EqualTo(c2));
  }

  [Test, Category("HappyPath")]
  public void Read_VirtualSizeSmallerthanCluster_PartialCluster() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;
    const int virtualSize = 100; // smaller than one cluster

    var content = MakePattern(clusterSize, seed: 0xDE);

    var img = BuildQcow2(
      virtualSize: virtualSize,
      clusterBits: clusterBits,
      clusterData: new Dictionary<int, byte[]> { [0] = content });

    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);

    Assert.That(reader.VirtualSize, Is.EqualTo(virtualSize));
    var extracted = reader.ExtractDisk();
    Assert.That(extracted.Length, Is.EqualTo(virtualSize));
    Assert.That(extracted, Is.EqualTo(content.AsSpan(0, virtualSize).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsSingleEntry() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;

    var img = BuildQcow2(virtualSize: clusterSize, clusterBits: clusterBits);
    using var ms = new MemoryStream(img);

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(clusterSize));
  }

  [Test, Category("HappyPath")]
  public void Read_EmptyVirtualDisk() {
    // virtualSize = 0 → ExtractDisk returns empty
    const int clusterBits = 12;
    var img = BuildQcow2(virtualSize: 0, clusterBits: clusterBits);
    using var ms = new MemoryStream(img);

    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);
    Assert.That(reader.VirtualSize, Is.EqualTo(0));
    var extracted = reader.ExtractDisk();
    Assert.That(extracted.Length, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Read_AllZeroClusters_ReturnsZeroImage() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;

    // No clusterData → all L2 entries are 0 → all zeros
    var img = BuildQcow2(virtualSize: clusterSize * 2, clusterBits: clusterBits);
    using var ms = new MemoryStream(img);

    var reader = new FileFormat.Qcow2.Qcow2Reader(ms);
    var extracted = reader.ExtractDisk();

    Assert.That(extracted.Length, Is.EqualTo(clusterSize * 2));
    Assert.That(extracted, Is.EqualTo(new byte[clusterSize * 2]));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SmallDisk_RoundTrips() {
    // 128 KB raw disk with recognisable pattern.
    var disk = new byte[128 * 1024];
    new Random(42).NextBytes(disk);

    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(disk);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Qcow2.Qcow2Reader(ms);
    Assert.That(r.VirtualSize, Is.EqualTo(disk.Length));
    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultiClusterDisk_RoundTrips() {
    // 3 clusters (65536 * 3 = 196608 bytes) — crosses an L2 entry boundary.
    var disk = new byte[3 * 65536];
    new Random(99).NextBytes(disk);

    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(disk);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Qcow2.Qcow2Reader(ms);
    Assert.That(r.ExtractDisk(), Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasQcow2Magic() {
    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(new byte[512]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(bytes[..4], Is.EqualTo(new byte[] { 0x51, 0x46, 0x49, 0xFB }));
  }

  // qemu-img check warns when refcount_table_offset is zero. Validate we now populate
  // it, and that the block it points to has refcount=1 for every cluster.
  [Test, Category("RealWorld")]
  public void Writer_PopulatesRefcountTableAndBlock() {
    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(new byte[70000]); // 2 data clusters at 65536-byte cluster size
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();

    var rtOffset = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(48));
    var rtClusters = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56));
    Assert.That(rtOffset, Is.Not.Zero, "refcount_table_offset must be set");
    Assert.That(rtClusters, Is.EqualTo(1u));

    var rbOffset = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)rtOffset));
    Assert.That(rbOffset, Is.Not.Zero, "first refcount table entry must point to a refcount block");

    // Every cluster covered by the refcount block should have refcount=1.
    var rcount0 = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)rbOffset));
    var rcount1 = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)rbOffset + 2));
    Assert.That(rcount0, Is.EqualTo(1));
    Assert.That(rcount1, Is.EqualTo(1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "qcow2 payload"u8.ToArray());
      var d = new FileFormat.Qcow2.Qcow2FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      // Virtual size is the FAT filesystem image (>= 1.44 MB by default).
      Assert.That(entries[0].OriginalSize, Is.GreaterThanOrEqualTo(1440 * 1024));
    } finally {
      File.Delete(tmp);
    }
  }
}
