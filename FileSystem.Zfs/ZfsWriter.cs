#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Writes a minimum-viable WORM ZFS pool image — single-vdev, single-dataset, flat root
/// directory, Fletcher-4 checksums, no compression/encryption/snapshots. Validates
/// round-trip through <see cref="ZfsReader"/>.
/// <para>
/// Image layout:
/// <code>
///   0 .. 256 KB          L0 vdev label
/// 256K .. 512K            L1 vdev label
/// 512K .. (end - 512K)    Data area (MOS, DSL, ZAP, file data)
/// end-512K .. end-256K    L2 vdev label
/// end-256K .. end         L3 vdev label
/// </code>
/// </para>
/// </summary>
public sealed class ZfsWriter {
  private readonly List<(string Name, byte[] Data)> _files = new();
  private string _poolName = "compworkbench";
  private string _datasetName = "data";

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  public void SetPoolName(string name) { this._poolName = name; }
  public void SetDatasetName(string name) { this._datasetName = name; }

  public void WriteTo(Stream output, long imageSize = 64L * 1024 * 1024) {
    const int labelSize = ZfsConstants.LabelSize;
    if (imageSize < 4L * labelSize + 1024 * 1024)
      throw new ArgumentException("Image size too small; must be >= ~5 MB.", nameof(imageSize));
    // Round down to sector-aligned.
    imageSize &= ~(long)(ZfsConstants.SectorSize - 1);

    // Allocate data area between the label pairs.
    var dataAreaStart = 2L * labelSize;
    var dataAreaEnd = imageSize - 2L * labelSize;
    var alloc = new SectorAllocator(dataAreaStart, dataAreaEnd, ZfsConstants.SectorSize);

    // ---------- Build file dnodes + data blocks ----------

    const ulong txg = 4;
    var datasetDnodes = new List<Dnode.Builder>();
    // Slot 0 reserved (null), 1 = master node ZAP, 2 = root dir ZAP, 3+ = files.
    datasetDnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtNone });           // obj 0
    var masterNodeSlot = datasetDnodes.Count;
    datasetDnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtMasterNode });     // obj 1 (ZAP) — placeholder
    var rootDirSlot = datasetDnodes.Count;
    datasetDnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtDirectoryContents }); // obj 2 (ZAP) — placeholder

    var rootDirEntries = new List<(string, ulong)>();
    foreach (var (name, data) in this._files) {
      var fileObjId = (ulong)datasetDnodes.Count;
      var fileDnode = BuildFileDnode(data, alloc, output, txg);
      datasetDnodes.Add(fileDnode);
      rootDirEntries.Add((name, fileObjId));
    }

    // Now fill in root dir ZAP
    var rootDirZapBytes = MicroZap.Encode(rootDirEntries, ZfsConstants.SectorSize);
    datasetDnodes[rootDirSlot] = BuildZapDnode(rootDirZapBytes, alloc, output, txg);

    // Master node ZAP → "ROOT" = rootDirSlot
    var masterZapBytes = MicroZap.Encode(new[] { ("ROOT", (ulong)rootDirSlot) }, ZfsConstants.SectorSize);
    datasetDnodes[masterNodeSlot] = BuildZapDnode(masterZapBytes, alloc, output, txg);

    // ---------- Pack dataset dnode array ----------

    var datasetDnodeBlock = PackDnodes(datasetDnodes);
    var datasetDnodeBp = WriteBlock(datasetDnodeBlock, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4);

    // Dataset meta-dnode describes the dnode array.
    var datasetMetaDnode = new Dnode.Builder {
      Type = ZfsConstants.DmuOtNone,  // meta
      Levels = 1,
      NumBlkPtr = 1,
      DataBlockSizeInSectors = (uint)(datasetDnodeBlock.Length / ZfsConstants.SectorSize),
      MaxBlockId = 0,
      UsedBytes = (ulong)datasetDnodeBlock.Length,
      BlkPtr0 = datasetDnodeBp,
    };

    // Dataset objset block.
    var datasetObjsetBlock = new byte[ObjsetPhys.Size];
    ObjsetPhys.Write(datasetObjsetBlock, datasetMetaDnode, ZfsConstants.DmuOstZfs);
    var datasetObjsetBp = WriteBlock(datasetObjsetBlock, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4);

    // ---------- Build MOS dnodes ----------

    var mosDnodes = new List<Dnode.Builder>();
    mosDnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtNone });              // obj 0
    var objDirSlot = mosDnodes.Count;
    mosDnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtObjectDirectory });   // obj 1 placeholder
    var dslDirSlot = mosDnodes.Count;
    mosDnodes.Add(BuildDslDirDnode(dslDirSlot + 1 /*head ds at next slot*/));        // obj 2
    var dslDsSlot = mosDnodes.Count;
    mosDnodes.Add(BuildDslDatasetDnode((ulong)dslDirSlot, datasetObjsetBp, txg));    // obj 3

    // Object directory ZAP (obj 1) entries
    var objDirEntries = new List<(string, ulong)> {
      ("root_dataset", (ulong)dslDirSlot),
    };
    var objDirZapBytes = MicroZap.Encode(objDirEntries, ZfsConstants.SectorSize);
    mosDnodes[objDirSlot] = BuildZapDnode(objDirZapBytes, alloc, output, txg);

    var mosDnodeBlock = PackDnodes(mosDnodes);
    var mosDnodeBp = WriteBlock(mosDnodeBlock, alloc, output, txg, ZfsConstants.ZioChecksumFletcher4);

    var mosMetaDnode = new Dnode.Builder {
      Type = ZfsConstants.DmuOtNone,
      Levels = 1,
      NumBlkPtr = 1,
      DataBlockSizeInSectors = (uint)(mosDnodeBlock.Length / ZfsConstants.SectorSize),
      MaxBlockId = 0,
      UsedBytes = (ulong)mosDnodeBlock.Length,
      BlkPtr0 = mosDnodeBp,
    };

    var mosObjsetBlock = new byte[ObjsetPhys.Size];
    ObjsetPhys.Write(mosObjsetBlock, mosMetaDnode, ZfsConstants.DmuOstMeta);
    var mosObjsetBp = WriteBlock(mosObjsetBlock, alloc, output, txg, ZfsConstants.ZioChecksumFletcher4);

    // ---------- Build uberblock ----------

    var vdevGuid = HashToGuid($"{this._poolName}-vdev");
    var poolGuid = HashToGuid($"{this._poolName}-pool");

    var ub = new Uberblock.Builder {
      Version = ZfsConstants.PoolVersion,
      Txg = txg,
      GuidSum = vdevGuid,        // single vdev → sum == vdev guid
      Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      RootBp = mosObjsetBp,
      SoftwareVersion = ZfsConstants.PoolVersion,
    };

    // ---------- Build NVList ----------

    var asize = (ulong)(dataAreaEnd - dataAreaStart);
    var nv = BuildVdevLabelNvList(poolGuid, vdevGuid, txg, asize);
    var nvBytes = XdrNvList.Encode(nv);
    if (nvBytes.Length > ZfsConstants.NvListSize)
      throw new InvalidOperationException("NvList exceeds 112 KB.");

    // ---------- Assemble 4 identical vdev labels ----------

    var label = BuildLabel(nvBytes, ub);

    // Write L0 at 0, L1 at 256K — data area already written to output directly via Seek.
    // Because we streamed data-area blocks via WriteBlock above, the output stream has
    // expanded to whatever the highest written offset is. We now need to:
    //  (a) ensure the final stream is exactly `imageSize` bytes long,
    //  (b) write L0, L1 at front and L2, L3 at back.
    output.SetLength(imageSize);

    output.Position = 0;
    output.Write(label);
    output.Position = labelSize;
    output.Write(label);

    output.Position = imageSize - 2L * labelSize;
    output.Write(label);
    output.Position = imageSize - labelSize;
    output.Write(label);

    output.Position = imageSize;
    output.Flush();
  }

  // ---------- Helpers ----------

  /// <summary>Builds a file dnode and writes its data block(s).</summary>
  private static Dnode.Builder BuildFileDnode(
    byte[] data, SectorAllocator alloc, Stream output, ulong txg) {

    // Simple: one data block of size rounded up to a sector, single direct pointer.
    // For >8 KB we use a larger data block (still single level, up to 128 KB).
    var blockSize = Math.Max((int)ZfsConstants.SectorSize, NextPow2Ge(data.Length));
    if (blockSize > 128 * 1024) blockSize = 128 * 1024;

    // If data > blockSize, we would need L1 indirect blocks — keep it simple by
    // enlarging block size up to 1 MB if necessary.
    while (blockSize < data.Length && blockSize < 1024 * 1024)
      blockSize *= 2;
    if (blockSize < data.Length)
      throw new NotSupportedException("File > 1 MB not supported in this WORM writer.");

    var block = new byte[blockSize];
    data.CopyTo(block, 0);
    var bp = WriteBlock(block, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4,
      logicalSizeBytes: blockSize,
      type: ZfsConstants.DmuOtPlainFileContents);

    // Set the logical size to actual data length in the dnode — but ZFS dnode_phys doesn't
    // store file size directly in v28 (that goes in znode_phys bonus). For our reader we
    // encode file size in the bonus area as a simple u64.
    var bonus = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bonus, (ulong)data.Length);

    return new Dnode.Builder {
      Type = ZfsConstants.DmuOtPlainFileContents,
      Levels = 1,
      NumBlkPtr = 1,
      DataBlockSizeInSectors = (uint)(blockSize / ZfsConstants.SectorSize),
      UsedBytes = (ulong)blockSize,
      MaxBlockId = 0,
      BlkPtr0 = bp,
      Bonus = bonus,
      BonusLen = 8,
    };
  }

  private static Dnode.Builder BuildZapDnode(byte[] zapBlock, SectorAllocator alloc, Stream output, ulong txg) {
    var bp = WriteBlock(zapBlock, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4,
      type: ZfsConstants.DmuOtZap);
    return new Dnode.Builder {
      Type = ZfsConstants.DmuOtZap,
      Levels = 1,
      NumBlkPtr = 1,
      DataBlockSizeInSectors = (uint)(zapBlock.Length / ZfsConstants.SectorSize),
      UsedBytes = (ulong)zapBlock.Length,
      BlkPtr0 = bp,
    };
  }

  private static Dnode.Builder BuildDslDirDnode(int headDatasetObj) {
    var phys = new DslDirPhys.Builder {
      CreationTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      HeadDatasetObj = (ulong)headDatasetObj,
    };
    var bonus = DslDirPhys.Encode(phys);
    return new Dnode.Builder {
      Type = ZfsConstants.DmuOtDslDir,
      Levels = 0,
      NumBlkPtr = 0,
      BonusType = ZfsConstants.DmuOtDslDir,
      BonusLen = (ushort)bonus.Length,
      Bonus = bonus,
    };
  }

  private static Dnode.Builder BuildDslDatasetDnode(ulong dirObj, BlockPointer.Builder datasetBp, ulong txg) {
    var phys = new DslDatasetPhys.Builder {
      DirObj = dirObj,
      CreationTxg = txg,
      CreationTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      NumChildren = 0,
      UsedBytes = 0,
      Bp = datasetBp,
    };
    var bonus = DslDatasetPhys.Encode(phys);
    return new Dnode.Builder {
      Type = ZfsConstants.DmuOtDslDataset,
      Levels = 0,
      NumBlkPtr = 0,
      BonusType = ZfsConstants.DmuOtDslDataset,
      BonusLen = (ushort)bonus.Length,
      Bonus = bonus,
    };
  }

  private static byte[] PackDnodes(List<Dnode.Builder> dnodes) {
    // Round up to nearest 16-dnode (8 KB) multiple for a clean block.
    var nDnodes = dnodes.Count;
    // Need at least 1 sector worth, and a power-of-2 block size.
    var neededBytes = nDnodes * Dnode.Size;
    var blockSize = NextPow2Ge(neededBytes);
    if (blockSize < (int)ZfsConstants.SectorSize) blockSize = (int)ZfsConstants.SectorSize;
    if (blockSize < 16 * 1024) blockSize = 16 * 1024; // ZFS default dnode block = 16 KB

    var block = new byte[blockSize];
    for (var i = 0; i < nDnodes; i++)
      Dnode.Write(block.AsSpan(i * Dnode.Size, Dnode.Size), dnodes[i]);
    return block;
  }

  /// <summary>
  /// Writes <paramref name="block"/> into the data area, computes Fletcher-4, and returns a
  /// populated blkptr_t builder referencing it.
  /// </summary>
  private static BlockPointer.Builder WriteBlock(
    byte[] block, SectorAllocator alloc, Stream output, ulong txg,
    byte checksum, int? logicalSizeBytes = null, byte type = 0) {

    var lenSectors = block.Length / ZfsConstants.SectorSize;
    if (block.Length % ZfsConstants.SectorSize != 0)
      throw new ArgumentException("Block must be sector-aligned.", nameof(block));

    var offset = alloc.Allocate(block.Length);
    var offsetSectors = (ulong)(offset / ZfsConstants.SectorSize);

    output.Position = offset;
    output.Write(block);

    var cksum = Fletcher4.Compute(block);
    var lsize = (uint)((logicalSizeBytes ?? block.Length) / ZfsConstants.SectorSize) - 1;
    if ((logicalSizeBytes ?? block.Length) > (int)(lsize + 1) * ZfsConstants.SectorSize) lsize++;

    return new BlockPointer.Builder {
      Vdev = 0,
      Grid = 0,
      AsizeSectors = (uint)lenSectors - 1,
      OffsetSectors = offsetSectors,
      Lsize = (uint)(block.Length / ZfsConstants.SectorSize) - 1,
      Psize = (uint)(block.Length / ZfsConstants.SectorSize) - 1,
      Compression = ZfsConstants.ZioCompressOff,
      Checksum = checksum,
      Type = type,
      Level = 0,
      Birth = txg,
      Fill = 1,
      Cksum = cksum,
    };
  }

  private static XdrNvList.NvList BuildVdevLabelNvList(ulong poolGuid, ulong vdevGuid, ulong txg, ulong asize) {
    var vdevTree = new XdrNvList.NvList()
      .AddString("type", "disk")
      .AddUInt64("id", 0)
      .AddUInt64("guid", vdevGuid)
      .AddString("path", "/dev/compworkbench")
      .AddUInt64("whole_disk", 1)
      .AddUInt64("metaslab_array", 0)
      .AddUInt64("metaslab_shift", 24)    // 16 MB metaslabs
      .AddUInt64("ashift", ZfsConstants.Ashift)
      .AddUInt64("asize", asize)
      .AddUInt64("is_log", 0)
      .AddUInt64("DTL", 0);

    return new XdrNvList.NvList()
      .AddUInt64("version", ZfsConstants.PoolVersion)
      .AddString("name", "compworkbench")
      .AddUInt64("state", ZfsConstants.PoolStateActive)
      .AddUInt64("txg", txg)
      .AddUInt64("pool_guid", poolGuid)
      .AddUInt64("hostid", 0)
      .AddString("hostname", "")
      .AddUInt64("top_guid", vdevGuid)
      .AddUInt64("guid", vdevGuid)
      .AddNvList("vdev_tree", vdevTree);
  }

  private static byte[] BuildLabel(byte[] nvBytes, Uberblock.Builder ub) {
    var label = new byte[ZfsConstants.LabelSize];

    // 8 KB VTOC pad — zero.
    // 8 KB boot header — zero.
    // 112 KB nvlist — copy in.
    nvBytes.AsSpan().CopyTo(label.AsSpan(ZfsConstants.NvListOffset, nvBytes.Length));

    // 128 × 1 KB uberblock slots. Fill exactly one (slot 0); others zero.
    var ubSlot = label.AsSpan(ZfsConstants.UberblockArrayOffset, ZfsConstants.UberblockSize);
    Uberblock.Write(ubSlot, ub);

    return label;
  }

  private static int NextPow2Ge(int n) {
    if (n <= 1) return 1;
    var p = 1;
    while (p < n) p <<= 1;
    return p;
  }

  /// <summary>Deterministic 64-bit hash for reproducible GUIDs in WORM images.</summary>
  private static ulong HashToGuid(string s) {
    // Simple FNV-1a 64.
    const ulong fnvOffset = 0xCBF29CE484222325UL;
    const ulong fnvPrime = 0x00000100000001B3UL;
    ulong h = fnvOffset;
    foreach (var c in s) { h ^= (byte)c; h *= fnvPrime; }
    if (h == 0) h = 1; // avoid zero
    return h;
  }

  /// <summary>
  /// Allocates sector-aligned regions within the data area of the image.
  /// Offsets are byte offsets within the image file.
  /// </summary>
  private sealed class SectorAllocator {
    private readonly long _start;
    private readonly long _end;
    private readonly long _alignment;
    private long _next;

    public SectorAllocator(long start, long end, long alignment) {
      this._start = start;
      this._end = end;
      this._alignment = alignment;
      this._next = start;
    }

    public long Allocate(int bytes) {
      // Align to the block's own size for natural alignment.
      var align = Math.Max(this._alignment, NextPow2Long(bytes));
      var aligned = (this._next + align - 1) & ~(align - 1);
      if (aligned + bytes > this._end)
        throw new InvalidOperationException(
          $"ZFS data area exhausted: need {bytes} bytes at offset {aligned} but max = {this._end}.");
      this._next = aligned + bytes;
      return aligned;
    }

    private static long NextPow2Long(int n) {
      if (n <= 1) return 1;
      long p = 1;
      while (p < n) p <<= 1;
      return p;
    }
  }
}
