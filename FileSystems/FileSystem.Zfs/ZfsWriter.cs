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

    // Build a directory tree from the (possibly path-separated) file names, then
    // materialise it into directory and file dnodes. The root directory occupies the
    // slot reserved above; every nested directory gets a fresh dnode whose data is a
    // ZAP mapping child name -> (type<<60 | childObjId).
    var root = DirectoryTree.Build(this._files);
    this.MaterialiseDirectory(root, (ulong)rootDirSlot, datasetDnodes, alloc, output, txg);

    // Master node ZAP → "ROOT" = rootDirSlot
    var masterZapBytes = MicroZap.Encode(new[] { ("ROOT", (ulong)rootDirSlot) }, (int)ZfsConstants.SectorSize);
    datasetDnodes[masterNodeSlot] = BuildZapDnode(masterZapBytes, alloc, output, txg,
      ZfsConstants.DmuOtMasterNode);

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
    var objDirZapBytes = MicroZap.Encode(objDirEntries, (int)ZfsConstants.SectorSize);
    mosDnodes[objDirSlot] = BuildZapDnode(objDirZapBytes, alloc, output, txg,
      ZfsConstants.DmuOtObjectDirectory);

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

  // ---------- Directory tree ----------

  /// <summary>
  /// An in-memory directory tree assembled from path-separated file names before any
  /// dnodes are allocated. Subdirectories are created on demand so that a file added as
  /// <c>a/b/c.txt</c> produces real directory objects for <c>a</c> and <c>a/b</c>.
  /// </summary>
  private sealed class DirectoryTree {
    public readonly SortedDictionary<string, DirectoryTree> SubDirs = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, byte[]> Files = new(StringComparer.Ordinal);

    public static DirectoryTree Build(IEnumerable<(string Name, byte[] Data)> files) {
      var root = new DirectoryTree();
      foreach (var (name, data) in files) {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
          continue;
        var dir = root;
        for (var i = 0; i < parts.Length - 1; i++) {
          var segment = parts[i];
          if (!dir.SubDirs.TryGetValue(segment, out var child)) {
            child = new DirectoryTree();
            dir.SubDirs[segment] = child;
          }
          dir = child;
        }
        dir.Files[parts[^1]] = data;
      }
      return root;
    }
  }

  /// <summary>
  /// Writes the contents of <paramref name="dir"/> into the dnode at
  /// <paramref name="dirObjId"/>: every child file and subdirectory is allocated a dnode,
  /// subdirectories are materialised recursively, and the directory's own ZAP (mapping each
  /// child name to <c>(type&lt;&lt;60 | childObjId)</c>) is written last.
  /// </summary>
  private void MaterialiseDirectory(
    DirectoryTree dir, ulong dirObjId, List<Dnode.Builder> dnodes,
    SectorAllocator alloc, Stream output, ulong txg) {

    var zapEntries = new List<(string, ulong)>();

    foreach (var (childName, childDir) in dir.SubDirs) {
      var childObjId = (ulong)dnodes.Count;
      // Reserve the slot up front so child objects allocated during recursion get later ids.
      dnodes.Add(new Dnode.Builder { Type = ZfsConstants.DmuOtDirectoryContents });
      this.MaterialiseDirectory(childDir, childObjId, dnodes, alloc, output, txg);
      zapEntries.Add((childName,
        (ZfsConstants.ZfsDirentTypeDir << ZfsConstants.ZfsDirentTypeShift) | childObjId));
    }

    foreach (var (fileName, data) in dir.Files) {
      var fileObjId = (ulong)dnodes.Count;
      dnodes.Add(BuildFileDnode(data, alloc, output, txg));
      zapEntries.Add((fileName,
        (ZfsConstants.ZfsDirentTypeReg << ZfsConstants.ZfsDirentTypeShift) | fileObjId));
    }

    dnodes[(int)dirObjId] = BuildDirectoryDnode(zapEntries, alloc, output, txg);
  }

  // ---------- Helpers ----------

  /// <summary>Largest entry count that still fits a single 512-byte micro-ZAP block.</summary>
  private static readonly int MicroZapCapacity =
    ((int)ZfsConstants.SectorSize - MicroZap.HeaderSize) / MicroZap.EntrySize;

  /// <summary>Block size used for the leaves and header of a fat ZAP.</summary>
  private const int FatZapBlockSize = 4096;

  /// <summary>
  /// Builds a directory dnode for the supplied child entries, with a znode bonus carrying the
  /// <c>S_IFDIR</c> mode so the directory is self-describing on disk. Small directories use a
  /// single micro-ZAP block; directories whose entries overflow a micro-ZAP spill into a fat
  /// ZAP whose blocks are referenced through a level-1 indirect block.
  /// </summary>
  private static Dnode.Builder BuildDirectoryDnode(
    List<(string Name, ulong Value)> zapEntries, SectorAllocator alloc, Stream output, ulong txg) {

    var bonus = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bonus, ZfsConstants.ModeIfDir);

    if (zapEntries.Count <= MicroZapCapacity && zapEntries.All(e => e.Name.Length < MicroZap.NameSize)) {
      var zapBlock = MicroZap.Encode(zapEntries, (int)ZfsConstants.SectorSize);
      var bp = WriteBlock(zapBlock, alloc, output, txg,
        ZfsConstants.ZioChecksumFletcher4,
        type: ZfsConstants.DmuOtDirectoryContents);
      return new Dnode.Builder {
        Type = ZfsConstants.DmuOtDirectoryContents,
        Levels = 1,
        NumBlkPtr = 1,
        DataBlockSizeInSectors = (uint)(zapBlock.Length / ZfsConstants.SectorSize),
        UsedBytes = (ulong)zapBlock.Length,
        BlkPtr0 = bp,
        Bonus = bonus,
        BonusLen = 8,
      };
    }

    return BuildFatZapDirectoryDnode(zapEntries, bonus, alloc, output, txg);
  }

  /// <summary>
  /// Materialises a fat-ZAP directory: each fat-ZAP block (header + leaves) is written as a
  /// separate data block, and a level-1 indirect block holding their block pointers is
  /// written and referenced by the dnode's single block pointer.
  /// </summary>
  private static Dnode.Builder BuildFatZapDirectoryDnode(
    List<(string Name, ulong Value)> zapEntries, byte[] bonus,
    SectorAllocator alloc, Stream output, ulong txg) {

    var fat = FatZap.Encode(zapEntries, FatZapBlockSize);
    var blockSize = fat.BlockSize;
    var blockCount = fat.BlockCount;

    // Indirect block: an array of blkptr_t, one per fat-ZAP block, sized to a power-of-two
    // block of its own (independent of the leaf block size). A single 128 KB indirect block
    // holds 1024 block pointers, which bounds this writer to ~1023 leaves — far beyond the
    // directory sizes targeted here.
    var indirectBytes = blockCount * BlockPointer.Size;
    var indirectBlockSize = NextPow2Ge(indirectBytes);
    if (indirectBlockSize < (int)ZfsConstants.SectorSize) indirectBlockSize = (int)ZfsConstants.SectorSize;
    if (indirectBlockSize > 128 * 1024)
      throw new NotSupportedException(
        "Fat-ZAP directory too large for a single-level indirect block in this writer.");

    var indirectBlock = new byte[indirectBlockSize];
    for (var i = 0; i < blockCount; i++) {
      var blockData = fat.Body.AsSpan(i * blockSize, blockSize).ToArray();
      var childBp = WriteBlock(blockData, alloc, output, txg,
        ZfsConstants.ZioChecksumFletcher4,
        type: ZfsConstants.DmuOtDirectoryContents);
      childBp.Level = 0;
      BlockPointer.Write(indirectBlock.AsSpan(i * BlockPointer.Size, BlockPointer.Size), childBp);
    }

    var indirectBp = WriteBlock(indirectBlock, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4,
      type: ZfsConstants.DmuOtDirectoryContents);
    indirectBp.Level = 1;

    return new Dnode.Builder {
      Type = ZfsConstants.DmuOtDirectoryContents,
      Levels = 2,
      NumBlkPtr = 1,
      DataBlockSizeInSectors = (uint)(blockSize / ZfsConstants.SectorSize),
      MaxBlockId = (ulong)(blockCount - 1),
      UsedBytes = (ulong)(blockCount * blockSize),
      BlkPtr0 = indirectBp,
      Bonus = bonus,
      BonusLen = 8,
    };
  }

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

  private static Dnode.Builder BuildZapDnode(byte[] zapBlock, SectorAllocator alloc, Stream output, ulong txg,
    byte dnodeType = ZfsConstants.DmuOtZap) {
    var bp = WriteBlock(zapBlock, alloc, output, txg,
      ZfsConstants.ZioChecksumFletcher4,
      type: dnodeType);
    return new Dnode.Builder {
      Type = dnodeType,
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
