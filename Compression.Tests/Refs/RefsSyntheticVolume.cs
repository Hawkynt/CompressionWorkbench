#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Refs;

/// <summary>One file to place into a synthetic ReFS volume.</summary>
/// <param name="Name">Leaf name inside the root directory.</param>
/// <param name="Content">Stream bytes; the allocation is rounded up to a cluster.</param>
/// <param name="Fragments">Number of on-disk extents the stream is split into.</param>
/// <param name="Resident">Emit a type-0x30/flags-0x01 holder without any decodable
/// extent table, which the namespace reader reports as a resident stream.</param>
internal sealed record RefsSyntheticFile(
  string Name,
  byte[] Content,
  int Fragments = 1,
  bool Resident = false);

/// <summary>
/// Builds a byte-exact, structurally valid unmounted ReFS 3.14 image.
///
/// The package deliberately has no ReFS creator — its writer edits existing
/// volumes only — so the offline block-clone primitive had no image to run
/// against and therefore no executing test. This generator supplies one. It is
/// a test fixture, not a mkfs: it emits the smallest metadata graph that the
/// offline mutation stack needs (VBR, SUPB, two CHKP slots, Container Table,
/// Object Table, Schema Table, Medium Allocator, Block Refcount and a single
/// root-directory object), every tree being one leaf page so no B+ split, no
/// separator-key convention and no Upcase Table are involved.
///
/// Two fixture choices are worth naming because they differ from a Windows
/// volume: the Container Table publishes one identity container, so VLCN ==
/// PLCN throughout; and the root-directory table is stamped with the generic
/// attribute schema 0x120 rather than the filename schema 0x140, so key
/// ordering never needs the 128 KiB Upcase Table. Neither is reachable from
/// the behaviour under test — the cloner never splits a page and never
/// compares filename keys.
/// </summary>
internal sealed class RefsSyntheticVolume {
  public const int SectorSize = 512;
  public const int SectorsPerCluster = 8;
  public const int ClusterSize = SectorSize * SectorsPerCluster;
  public const int PageSize = 16 * 1024;
  public const int ClustersPerPage = PageSize / ClusterSize;
  public const int PageReferenceSize = 48;
  public const uint ClustersPerContainer = 16384;
  public const int TotalClusters = 1024;
  public const ulong RootDirectoryOid = 0x600;
  public const uint ExtentFlags = 0x180040;

  // Fixed placement. Every metadata page occupies ClustersPerPage clusters.
  public const ulong VbrCluster = 0;
  public const ulong SuperblockCluster = 0x1E;
  public const ulong CheckpointACluster = 36;
  public const ulong CheckpointBCluster = 40;
  public const ulong ContainerRootCluster = 44;
  public const ulong ObjectRootCluster = 48;
  public const ulong SchemaRootCluster = 52;
  public const ulong MediumAllocatorRootCluster = 56;
  public const ulong BlockRefcountRootCluster = 60;
  public const ulong DirectoryRootCluster = 64;
  public const ulong FirstDataCluster = 68;

  private const int NodeOffset = 0x80;
  private const int DataStart = 0xA8;
  private const int IndexEnd = PageSize;
  private const int SchemaDescriptorOffset = 0x54;

  private readonly List<RefsSyntheticFile> _files = [];
  private readonly List<ulong> _seededRefcountRows = [];

  /// <summary>Layout of a built volume: where every file's clusters landed.</summary>
  internal sealed record Layout(
    IReadOnlyDictionary<string, IReadOnlyList<ulong>> FileClusters,
    IReadOnlyDictionary<string, ulong> FileIds,
    ulong FirstFreeCluster);

  public Layout? BuiltLayout { get; private set; }

  public RefsSyntheticVolume WithFile(string name, byte[] content, int fragments = 1) {
    this._files.Add(new RefsSyntheticFile(name, content, fragments));
    return this;
  }

  public RefsSyntheticVolume WithResidentFile(string name, byte[] content) {
    this._files.Add(new RefsSyntheticFile(name, content, 1, Resident: true));
    return this;
  }

  /// <summary>
  /// Pre-creates an all-zero Block Refcount row covering <paramref name="startVirtualLcn"/>.
  /// Without it root #6 is empty and the first clone must materialise the row;
  /// with it the row exists but the target slot is zero, which is the second
  /// first-clone case the codec distinguishes.
  /// </summary>
  public RefsSyntheticVolume WithEmptyRefcountRow(ulong startVirtualLcn = 0) {
    this._seededRefcountRows.Add(startVirtualLcn);
    return this;
  }

  public byte[] Build() {
    var image = new byte[(long)TotalClusters * ClusterSize];

    // ── data placement ───────────────────────────────────────────────────────
    var fileClusters = new Dictionary<string, IReadOnlyList<ulong>>();
    var fileExtents = new Dictionary<string, List<(ulong Lcn, uint Vcn, uint Run)>>();
    var fileIds = new Dictionary<string, ulong>();
    var cursor = FirstDataCluster;
    ulong nextFileId = 0x1000;

    foreach (var file in this._files) {
      fileIds[file.Name] = nextFileId++;
      if (file.Resident) {
        fileClusters[file.Name] = [];
        fileExtents[file.Name] = [];
        continue;
      }

      var clusterCount = (file.Content.Length + ClusterSize - 1) / ClusterSize;
      var clusters = new List<ulong>(clusterCount);
      var extents = new List<(ulong Lcn, uint Vcn, uint Run)>();
      if (clusterCount > 0) {
        var fragments = Math.Max(1, Math.Min(file.Fragments, clusterCount));
        var perFragment = clusterCount / fragments;
        var remainder = clusterCount % fragments;
        uint vcn = 0;
        for (var f = 0; f < fragments; ++f) {
          var run = perFragment + (f < remainder ? 1 : 0);
          extents.Add((cursor, vcn, (uint)run));
          for (var i = 0; i < run; ++i) clusters.Add(cursor + (ulong)i);
          vcn += (uint)run;
          cursor += (ulong)run;
          if (f + 1 < fragments) ++cursor; // deliberate hole so the runs stay separate
        }
      }
      fileClusters[file.Name] = clusters;
      fileExtents[file.Name] = extents;

      var written = 0;
      foreach (var lcn in clusters) {
        var take = Math.Min(ClusterSize, file.Content.Length - written);
        if (take <= 0) break;
        file.Content.AsSpan(written, take).CopyTo(image.AsSpan((int)(lcn * ClusterSize), take));
        written += take;
      }
    }

    var firstFree = Math.Max(cursor, FirstDataCluster);
    this.BuiltLayout = new Layout(fileClusters, fileIds, firstFree);

    // ── bootstrap structures ─────────────────────────────────────────────────
    WriteVbr(image);
    WriteSuperblock(image);
    WriteCheckpoint(image, CheckpointACluster, clock: 2);
    WriteCheckpoint(image, CheckpointBCluster, clock: 1);

    // ── metadata trees ───────────────────────────────────────────────────────
    WritePage(image, ContainerRootCluster, tableId: 0x0B, schemaId: 0xE0C0, physical: true,
      rows: [new Row(ContainerKey(), ContainerValue())]);

    WritePage(image, ObjectRootCluster, tableId: 0x02, schemaId: 0xE030, physical: false,
      rows: [new Row(ObjectKey(RootDirectoryOid), ObjectValue(DirectoryRootCluster))]);

    WritePage(image, SchemaRootCluster, tableId: 0x03, schemaId: 0xE060, physical: false,
      rows: SchemaRows());

    WritePage(image, MediumAllocatorRootCluster, tableId: 0x21, schemaId: 0xE010, physical: false,
      rows: [new Row(AllocatorKey(), AllocatorValue(firstFree))]);

    WritePage(image, BlockRefcountRootCluster, tableId: 0x0F, schemaId: 0xE0B0, physical: false,
      rows: this._seededRefcountRows
        .Distinct()
        .OrderBy(start => start)
        .Select(start => new Row(RefcountKey(start), RefcountValue(start)))
        .ToList());

    WritePage(image, DirectoryRootCluster, tableId: 0x300, schemaId: 0x120, physical: false,
      rows: this.DirectoryRows(fileIds, fileExtents));

    return image;
  }

  // ── bootstrap ──────────────────────────────────────────────────────────────

  private static void WriteVbr(byte[] image) {
    var vbr = image.AsSpan(0, 512);
    Encoding.ASCII.GetBytes("ReFS").CopyTo(vbr[3..]);
    Encoding.ASCII.GetBytes("FSRS").CopyTo(vbr[0x10..]);
    BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(0x14, 2), 0x200);
    BinaryPrimitives.WriteUInt64LittleEndian(vbr.Slice(0x18, 8), (ulong)TotalClusters * SectorsPerCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(0x20, 4), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(0x24, 4), SectorsPerCluster);
    vbr[0x28] = 3;
    vbr[0x29] = 14;
    BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(0x2A, 2), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(0x2C, 4), 0x66);
    BinaryPrimitives.WriteUInt64LittleEndian(vbr.Slice(0x38, 8), 0x0123456789ABCDEFUL);
    BinaryPrimitives.WriteUInt64LittleEndian(vbr.Slice(0x40, 8), (ulong)ClustersPerContainer * ClusterSize);
    BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(0x16, 2), VbrChecksum(vbr));
  }

  internal static ushort VbrChecksum(ReadOnlySpan<byte> vbr) {
    ushort sum = 0;
    for (var i = 3; i < 512; ++i) {
      if (i is 0x16 or 0x17) continue;
      sum = (ushort)((sum >> 1) | (sum << 15));
      sum = unchecked((ushort)(sum + vbr[i]));
    }
    return sum;
  }

  private static void WriteSuperblock(byte[] image) {
    var supb = image.AsSpan((int)(SuperblockCluster * ClusterSize), ClusterSize);
    Encoding.ASCII.GetBytes("SUPB").CopyTo(supb);
    BinaryPrimitives.WriteUInt64LittleEndian(supb.Slice(0x68, 8), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(supb.Slice(0x70, 4), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(supb.Slice(0x74, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(supb.Slice(0x78, 4), 0x100);
    BinaryPrimitives.WriteUInt32LittleEndian(supb.Slice(0x7C, 4), PageReferenceSize);
    BinaryPrimitives.WriteUInt64LittleEndian(supb.Slice(0x80, 8), CheckpointACluster);
    BinaryPrimitives.WriteUInt64LittleEndian(supb.Slice(0x88, 8), CheckpointBCluster);
    // Self-checksum descriptor: type 0 / length 0, so every Refresh* call is a
    // no-op. Nothing on the ReFS read path verifies a digest.
  }

  private static void WriteCheckpoint(byte[] image, ulong headCluster, ulong clock) {
    var page = new byte[PageSize];
    Encoding.ASCII.GetBytes("CHKP").CopyTo(page.AsSpan());
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x10, 8), clock);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x20, 8), headCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x58, 4), 0x380);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x5C, 4), PageReferenceSize);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x60, 8), clock);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x68, 8), clock);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x78, 4), 0x0002);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x90, 4), 13);
    for (var i = 0; i < 13; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x94 + i * 4, 4), (uint)(0x100 + i * PageReferenceSize));

    StampRoot(page, 0, ObjectRootCluster);
    StampRoot(page, 1, MediumAllocatorRootCluster);
    StampRoot(page, 3, SchemaRootCluster);
    StampRoot(page, 6, BlockRefcountRootCluster);
    StampRoot(page, 7, ContainerRootCluster);

    WriteClusters(image, headCluster, page);
  }

  private static void StampRoot(byte[] checkpoint, int rootIndex, ulong headCluster) {
    var descriptor = 0x100 + rootIndex * PageReferenceSize;
    for (var i = 0; i < ClustersPerPage; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(
        checkpoint.AsSpan(descriptor + i * 8, 8),
        headCluster + (ulong)i);
    // reference[0x22] = checksum type 0, reference[0x24] = digest length 0.
  }

  private static void WriteClusters(byte[] image, ulong headCluster, ReadOnlySpan<byte> page) {
    for (var i = 0; i < page.Length; i += ClusterSize)
      page.Slice(i, Math.Min(ClusterSize, page.Length - i))
        .CopyTo(image.AsSpan((int)(headCluster * ClusterSize) + i));
  }

  // ── B+ leaf pages ──────────────────────────────────────────────────────────

  internal sealed record Row(byte[] Key, byte[] Value);

  private static void WritePage(
      byte[] image,
      ulong headCluster,
      ulong tableId,
      ushort schemaId,
      bool physical,
      IReadOnlyList<Row> rows) {
    var page = new byte[PageSize];
    Encoding.ASCII.GetBytes("MSB+").CopyTo(page.AsSpan());
    for (var i = 0; i < ClustersPerPage; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x20 + i * 8, 8), headCluster + (ulong)i);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x48, 8), tableId);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x50, 4), NodeOffset - 0x50);

    // IndexRoot descriptor: RefsCowBTree.FindRootSchema scans [0x50, nodeOffset)
    // for a 0x28-byte descriptor and reads the schema id from +0x0C.
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(SchemaDescriptorOffset, 4), 0x28);
    BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(SchemaDescriptorOffset + 0x0C, 2), schemaId);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(SchemaDescriptorOffset + 0x20, 8), (ulong)rows.Count);

    var serialized = rows.Select(SerializeRow).ToArray();
    var rowBytes = serialized.Sum(r => r.Length);
    var indexStart = IndexEnd - rows.Count * 4;
    if (DataStart + rowBytes > indexStart)
      throw new InvalidOperationException("Synthetic ReFS page overflows; the fixture needs a smaller tree.");

    var offset = DataStart;
    for (var i = 0; i < serialized.Length; ++i) {
      serialized[i].CopyTo(page, offset);
      BinaryPrimitives.WriteUInt32LittleEndian(
        page.AsSpan(indexStart + i * 4, 4),
        0xFFFF0000U | (uint)(offset - NodeOffset));
      offset += serialized[i].Length;
    }

    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x00, 4), DataStart - NodeOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x04, 4), (uint)(offset - NodeOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x08, 4), (uint)(indexStart - offset));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x0C, 4), 0); // leaf
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x10, 4), (uint)(indexStart - NodeOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x14, 4), (uint)rows.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(NodeOffset + 0x20, 4), IndexEnd - NodeOffset);

    WriteClusters(image, headCluster, page);
  }

  private static byte[] SerializeRow(Row row) {
    const int keyOffset = 16;
    var valueOffset = Align8(keyOffset + row.Key.Length);
    var rowSize = Align8(valueOffset + row.Value.Length);
    var result = new byte[rowSize];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)rowSize);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), keyOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), (ushort)row.Key.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), (ushort)valueOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12, 2), (ushort)row.Value.Length);
    row.Key.CopyTo(result, keyOffset);
    row.Value.CopyTo(result, valueOffset);
    return result;
  }

  private static int Align8(int value) => (value + 7) & ~7;

  // ── row builders ───────────────────────────────────────────────────────────

  private static byte[] ContainerKey() => new byte[16];

  private static byte[] ContainerValue() {
    var value = new byte[0x98];
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x18, 4), ClustersPerContainer);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(value.Length - 16, 8), 0UL);
    return value;
  }

  private static byte[] ObjectKey(ulong oid) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), oid);
    return key;
  }

  private static byte[] ObjectValue(ulong rootHeadCluster) {
    var value = new byte[0x20 + PageReferenceSize];
    for (var i = 0; i < ClustersPerPage; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x20 + i * 8, 8), rootHeadCluster + (ulong)i);
    return value;
  }

  private static readonly uint[] SchemaIds = [
    0x110, 0x120, 0x130, 0x140,
    0xE010, 0xE030, 0xE040, 0xE060, 0xE080, 0xE090, 0xE0B0, 0xE0C0,
  ];

  private static List<Row> SchemaRows() {
    var rows = new List<Row>(SchemaIds.Length);
    foreach (var schemaId in SchemaIds.OrderBy(id => id)) {
      var key = new byte[8];
      BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0, 4), schemaId);
      var value = new byte[0x50];
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x00, 4), 0x50);
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x04, 4), 0x18);
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x18, 4), 0x38);
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x1C, 4), 1);
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x24, 4), schemaId);
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x48, 4), 1);
      rows.Add(new Row(key, value));
    }
    return rows;
  }

  private static byte[] AllocatorKey() {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), 0UL);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), TotalClusters);
    return key;
  }

  /// <summary>
  /// One bitmap Medium Allocator row covering the whole volume. Everything below
  /// <paramref name="firstFree"/> is metadata or file data; the rest is the pool
  /// the CoW page store reserves its replacement pages from.
  /// </summary>
  private static byte[] AllocatorValue(ulong firstFree) {
    var value = new byte[0x18 + 2048];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x00, 8), 0UL);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x08, 8), TotalClusters);

    var used = 0;
    for (ulong lcn = 0; lcn < firstFree; ++lcn) {
      value[0x18 + (int)(lcn >> 3)] |= (byte)(1 << (int)(lcn & 7));
      ++used;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x10, 2), (ushort)(TotalClusters - used));
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x12, 2), 0x01);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x14, 2), 0x0218);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x16, 2), (ushort)used);
    return value;
  }

  private static byte[] RefcountKey(ulong startVirtualLcn) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), startVirtualLcn);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), 0x400);
    return key;
  }

  private static byte[] RefcountValue(ulong startVirtualLcn) {
    var value = new byte[0x820];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x00, 8), startVirtualLcn);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x08, 8), 0x400);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x10, 8), 1UL);
    return value;
  }

  private List<Row> DirectoryRows(
      IReadOnlyDictionary<string, ulong> fileIds,
      IReadOnlyDictionary<string, List<(ulong Lcn, uint Vcn, uint Run)>> fileExtents) {
    var rows = new List<Row>();
    foreach (var file in this._files) {
      var fileId = fileIds[file.Name];
      var size = (ulong)file.Content.Length;
      var allocated = (ulong)(((file.Content.Length + ClusterSize - 1) / ClusterSize) * (long)ClusterSize);

      if (file.Resident) {
        rows.Add(new Row(NameKey(file.Name, 0x01), ResidentHolderValue(size)));
        continue;
      }

      rows.Add(new Row(NameKey(file.Name, 0x02), FileRecordValue(fileId, size, allocated)));
      rows.Add(new Row(BackingKey(fileId), BackingValue(size, allocated, fileExtents[file.Name])));
    }
    rows.Sort((a, b) => CompareAttributeKey(a.Key, b.Key));
    return rows;
  }

  private static byte[] NameKey(string name, ushort keyFlags) {
    var encoded = Encoding.Unicode.GetBytes(name);
    var key = new byte[4 + encoded.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(0, 2), 0x30);
    BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(2, 2), keyFlags);
    encoded.CopyTo(key, 4);
    return key;
  }

  private static byte[] FileRecordValue(ulong fileId, ulong size, ulong allocated) {
    var value = new byte[0x48];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x00, 8), fileId);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x08, 8), RootDirectoryOid);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x18, 8), 0x01D0_0000_0000_0000UL);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x30, 8), allocated);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x38, 8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x40, 4), 0x20);
    return value;
  }

  private static byte[] ResidentHolderValue(ulong size) {
    var value = new byte[0x80];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x30, 8), 0x01D0_0000_0000_0000UL);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x48, 4), 0x20);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x58, 8), size);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x60, 8), 0UL);
    return value;
  }

  private static byte[] BackingKey(ulong fileId) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(0, 2), 0x40);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), fileId);
    return key;
  }

  // Native 3.10+ extent-holder value. The sub-record starts at +0xA8, its extent
  // table header at +0xAC and the 24-byte entries at +0xD0 — the layout the
  // offline writer's TryRewriteNativeHolder recognises and rewrites in place.
  public const int BackingSubRecordOffset = 0xA8;
  public const int BackingTableHeaderOffset = 0xAC;
  public const int BackingEntriesOffset = 0xD0;

  internal static byte[] BackingValue(
      ulong size,
      ulong allocated,
      IReadOnlyList<(ulong Lcn, uint Vcn, uint Run)> extents) {
    var length = BackingEntriesOffset + extents.Count * 24;
    var value = new byte[Math.Max(length, 0x68)];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x58, 8), size);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x60, 8), allocated);
    if (extents.Count == 0) return value;

    const int start = BackingEntriesOffset - BackingTableHeaderOffset;
    BinaryPrimitives.WriteUInt32LittleEndian(
      value.AsSpan(BackingSubRecordOffset, 4), (uint)(length - BackingSubRecordOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(BackingTableHeaderOffset + 0x00, 4), start);
    BinaryPrimitives.WriteUInt32LittleEndian(
      value.AsSpan(BackingTableHeaderOffset + 0x04, 4), (uint)(start + extents.Count * 24));
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(BackingTableHeaderOffset + 0x0C, 4), 0xE00);
    BinaryPrimitives.WriteUInt32LittleEndian(
      value.AsSpan(BackingTableHeaderOffset + 0x14, 4), (uint)extents.Count);

    for (var i = 0; i < extents.Count; ++i) {
      var entry = value.AsSpan(BackingEntriesOffset + i * 24, 24);
      BinaryPrimitives.WriteUInt64LittleEndian(entry[0x00..0x08], extents[i].Lcn);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[0x08..0x0C], ExtentFlags);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[0x0C..0x10], extents[i].Vcn);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[0x14..0x18], extents[i].Run);
    }
    return value;
  }

  /// <summary>
  /// Replica of the key ordering ReFS uses for the generic attribute schemas:
  /// u16 type, u16 flags, then little-endian u64 chunks, then a byte tail. The
  /// fixture must emit rows in exactly this order or the CoW engine rejects the
  /// tree before it mutates anything.
  /// </summary>
  internal static int CompareAttributeKey(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    if (left.Length >= 4 && right.Length >= 4) {
      var type = BinaryPrimitives.ReadUInt16LittleEndian(left[..2])
        .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right[..2]));
      if (type != 0) return type;
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(left.Slice(2, 2))
        .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right.Slice(2, 2)));
      if (flags != 0) return flags;
    }

    var cursor = 4;
    while (cursor + 8 <= left.Length && cursor + 8 <= right.Length) {
      var cmp = BinaryPrimitives.ReadUInt64LittleEndian(left.Slice(cursor, 8))
        .CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(right.Slice(cursor, 8)));
      if (cmp != 0) return cmp;
      cursor += 8;
    }

    var tailLeft = left[cursor..];
    var tailRight = right[cursor..];
    var count = Math.Min(tailLeft.Length, tailRight.Length);
    for (var i = 0; i < count; ++i) {
      var cmp = tailLeft[i].CompareTo(tailRight[i]);
      if (cmp != 0) return cmp;
    }
    return tailLeft.Length.CompareTo(tailRight.Length);
  }
}
