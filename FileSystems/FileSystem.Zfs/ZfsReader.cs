#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Zfs;

/// <summary>
/// Reads a ZFS pool image produced by <see cref="ZfsWriter"/> (and compatible minimal
/// spec-aligned images). Traverses: vdev label → highest-TXG uberblock → MOS objset →
/// object directory ZAP → DSL dataset → dataset objset → master node / ROOT dir ZAP → file
/// dnodes. Validates Fletcher-4 checksums on all traversed blocks.
/// </summary>
public sealed class ZfsReader : IDisposable {
  private readonly ImageAccessor _data;
  private readonly List<ZfsEntry> _entries = new();
  private readonly Dictionary<ulong, Dnode.Builder> _datasetDnodesById = new();
  private string? _poolName;

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<ZfsEntry> Entries => this._entries;
  /// <summary>
  /// Gets the pool name.
  /// </summary>
public string? PoolName => this._poolName;

  /// <summary>
  /// Initializes a new instance of <see cref="ZfsReader"/>.
  /// </summary>
public ZfsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: a pool's metadata is a small fraction of the
    // vdev however many gigabytes of file records follow it.
    this._data = new ImageAccessor(stream, leaveOpen);
    this.Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._data.Length;

  private void Parse() {
    if (this._data.Length < ZfsConstants.LabelSize)
      throw new InvalidDataException("ZFS: image too small (minimum 256 KB for one label).");

    // Look at L0 first. Find highest-TXG uberblock.
    var (ub, nvBytes) = this.ReadLabel(0);
    if (ub == null)
      throw new InvalidDataException("ZFS: no valid uberblock found in L0.");

    // Parse nvlist for pool name.
    try {
      var nv = XdrNvList.Decode(nvBytes);
      foreach (var (name, type, value) in nv.Pairs) {
        if (name == "name" && type == XdrNvList.DataType.String)
          this._poolName = (string)value;
      }
    } catch {
      // NvList parse failure is non-fatal — continue with uberblock traversal.
    }

    // Follow ub.RootBp → MOS objset.
    var mosBlock = this.ReadBlock(ub.RootBp);
    var (mosMeta, osType) = ObjsetPhys.Read(mosBlock);
    if (osType != ZfsConstants.DmuOstMeta)
      throw new InvalidDataException($"ZFS: expected MOS objset (type {ZfsConstants.DmuOstMeta}), got {osType}.");

    var mosDnodes = this.ReadDnodeArray(mosMeta);

    // Object directory is at obj ID 1.
    if (mosDnodes.Count <= 1)
      throw new InvalidDataException("ZFS: MOS has no object directory.");
    var objDirDnode = mosDnodes[1];
    if (objDirDnode.Type != ZfsConstants.DmuOtObjectDirectory)
      throw new InvalidDataException($"ZFS: obj 1 not object directory (type = {objDirDnode.Type}).");

    var objDirZap = this.ReadZap(objDirDnode);
    ulong? rootDsDirId = null;
    foreach (var (k, v) in objDirZap)
      if (k == "root_dataset") rootDsDirId = v;
    if (rootDsDirId == null)
      throw new InvalidDataException("ZFS: object directory has no 'root_dataset' entry.");

    // DSL dir at rootDsDirId; it has head_dataset_obj pointing at the DSL dataset.
    if ((ulong)mosDnodes.Count <= rootDsDirId)
      throw new InvalidDataException("ZFS: root_dataset dnode out of range.");
    var dslDirDnode = mosDnodes[(int)rootDsDirId.Value];
    if (dslDirDnode.Bonus == null || dslDirDnode.Bonus.Length < DslDirPhys.Size)
      throw new InvalidDataException("ZFS: DSL dir bonus too small.");
    var dslDir = DslDirPhys.Decode(dslDirDnode.Bonus);
    if (dslDir.HeadDatasetObj == 0 || (ulong)mosDnodes.Count <= dslDir.HeadDatasetObj)
      throw new InvalidDataException("ZFS: DSL dir head_dataset_obj invalid.");

    var dslDsDnode = mosDnodes[(int)dslDir.HeadDatasetObj];
    if (dslDsDnode.Bonus == null || dslDsDnode.Bonus.Length < DslDatasetPhys.Size)
      throw new InvalidDataException("ZFS: DSL dataset bonus too small.");
    var dslDs = DslDatasetPhys.Decode(dslDsDnode.Bonus);

    // dslDs.Bp points at the dataset's objset_phys_t.
    var dsObjsetBlock = this.ReadBlock(dslDs.Bp);
    var (dsMeta, dsOsType) = ObjsetPhys.Read(dsObjsetBlock);
    if (dsOsType != ZfsConstants.DmuOstZfs)
      throw new InvalidDataException($"ZFS: expected dataset objset (type {ZfsConstants.DmuOstZfs}), got {dsOsType}.");

    var dsDnodes = this.ReadDnodeArray(dsMeta);
    for (var i = 0; i < dsDnodes.Count; i++)
      this._datasetDnodesById[(ulong)i] = dsDnodes[i];

    // Master node at obj 1 → entry ROOT = rootDirObj.
    if (dsDnodes.Count <= 1)
      throw new InvalidDataException("ZFS: dataset has no master node.");
    var masterZap = this.ReadZap(dsDnodes[1]);
    ulong? rootDirObj = null;
    foreach (var (k, v) in masterZap)
      if (k == "ROOT") rootDirObj = v;
    if (rootDirObj == null)
      throw new InvalidDataException("ZFS: dataset master node has no 'ROOT' entry.");

    if ((ulong)dsDnodes.Count <= rootDirObj)
      throw new InvalidDataException("ZFS: root dir obj out of range.");

    // Walk the directory tree starting at ROOT. Directory ZAP entry values encode the
    // child object id in the low bits and the file type in the high bits.
    this.CollectDirectory(dsDnodes, rootDirObj.Value, parentPath: "", depth: 0);
  }

  /// <summary>
  /// Recursively walks a directory dnode's ZAP, emitting a <see cref="ZfsEntry"/> for each
  /// file (with its full path) and for each subdirectory, then descending into the latter.
  /// </summary>
  private void CollectDirectory(List<Dnode.Builder> dsDnodes, ulong dirObjId, string parentPath, int depth) {
    if (depth > 64)
      throw new InvalidDataException("ZFS: directory tree too deep (possible cycle).");
    if ((ulong)dsDnodes.Count <= dirObjId)
      throw new InvalidDataException("ZFS: directory dnode out of range.");

    foreach (var (name, rawValue) in this.ReadZap(dsDnodes[(int)dirObjId])) {
      var objId = rawValue & ZfsConstants.ZfsDirentObjMask;
      var type = rawValue >> ZfsConstants.ZfsDirentTypeShift;
      if ((ulong)dsDnodes.Count <= objId) continue;
      var childDnode = dsDnodes[(int)objId];
      var fullPath = parentPath.Length == 0 ? name : parentPath + "/" + name;

      var isDir = type == ZfsConstants.ZfsDirentTypeDir
                  || (type == 0 && childDnode.Type == ZfsConstants.DmuOtDirectoryContents
                      && childDnode.Bonus != null
                      && childDnode.Bonus.Length >= 8
                      && BinaryPrimitives.ReadUInt64LittleEndian(childDnode.Bonus) == ZfsConstants.ModeIfDir);

      if (isDir) {
        this._entries.Add(new ZfsEntry {
          Name = fullPath,
          Size = 0,
          IsDirectory = true,
          LastModified = null,
          ObjectId = objId,
        });
        this.CollectDirectory(dsDnodes, objId, fullPath, depth + 1);
        continue;
      }

      long size = (long)childDnode.UsedBytes;
      if (childDnode.Bonus != null && childDnode.Bonus.Length >= 8)
        size = (long)BinaryPrimitives.ReadUInt64LittleEndian(childDnode.Bonus);
      this._entries.Add(new ZfsEntry {
        Name = fullPath,
        Size = size,
        IsDirectory = false,
        LastModified = null,
        ObjectId = objId,
      });
    }
  }

  private (Uberblock.Builder? Ub, byte[] NvBytes) ReadLabel(long labelOffset) {
    if (labelOffset + ZfsConstants.LabelSize > this._data.Length) return (null, []);

    var labelSpan = this._data.Read(labelOffset, ZfsConstants.LabelSize).AsSpan();
    var nvBytes = labelSpan.Slice(ZfsConstants.NvListOffset, ZfsConstants.NvListSize).ToArray();

    Uberblock.Builder? best = null;
    for (var i = 0; i < ZfsConstants.UberblockCount; i++) {
      var slotStart = ZfsConstants.UberblockArrayOffset + i * ZfsConstants.UberblockSize;
      var slot = labelSpan.Slice(slotStart, ZfsConstants.UberblockSize);
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(slot[..8]);
      if (magic != ZfsConstants.UberblockMagic) continue;
      Uberblock.Builder ub;
      try { ub = Uberblock.Read(slot); } catch { continue; }
      if (best == null || ub.Txg > best.Txg) best = ub;
    }
    return (best, nvBytes);
  }

  /// <summary>Reads the block referenced by a blkptr_t and verifies Fletcher-4.</summary>
  private byte[] ReadBlock(BlockPointer.Builder bp) {
    var psize = ((int)bp.Psize + 1) * (int)ZfsConstants.SectorSize;
    var offset = (long)bp.OffsetSectors * ZfsConstants.SectorSize;
    if (offset < 0 || offset + psize > this._data.Length)
      throw new InvalidDataException($"ZFS: blkptr offset {offset} + {psize} out of range.");
    var block = this._data.Read(offset, psize);

    if (bp.Checksum == ZfsConstants.ZioChecksumFletcher4) {
      var actual = Fletcher4.Compute(block);
      if (actual != bp.Cksum)
        throw new InvalidDataException(
          $"ZFS: Fletcher-4 mismatch at offset {offset} " +
          $"(expected {bp.Cksum}, got {actual}).");
    }
    return block;
  }

  /// <summary>Reads an array of dnodes described by a meta-dnode.</summary>
  private List<Dnode.Builder> ReadDnodeArray(Dnode.Builder metaDnode) {
    if (metaDnode.BlkPtr0 == null)
      throw new InvalidDataException("ZFS: meta-dnode has no block pointer.");
    var block = this.ReadBlock(metaDnode.BlkPtr0);
    var dnodes = new List<Dnode.Builder>();
    for (var i = 0; i + Dnode.Size <= block.Length; i += Dnode.Size)
      dnodes.Add(Dnode.Read(block.AsSpan(i, Dnode.Size)));
    return dnodes;
  }

  /// <summary>
  /// Reads a ZAP from a dnode, detecting micro-ZAP vs fat ZAP by the leading block type. A
  /// micro-ZAP fits in a single block; a fat ZAP spans a header block plus leaf blocks that
  /// are reached through the dnode's indirect block(s).
  /// </summary>
  private List<(string Name, ulong Value)> ReadZap(Dnode.Builder dnode) {
    if (dnode.BlkPtr0 == null)
      throw new InvalidDataException("ZFS: ZAP dnode has no block pointer.");

    var firstBlock = this.ReadDnodeDataBlock(dnode, 0);
    if (firstBlock.Length >= 8) {
      var blockType = BinaryPrimitives.ReadUInt64LittleEndian(firstBlock.AsSpan(0, 8));
      if (blockType == ZfsConstants.ZbtMicro)
        return MicroZap.Decode(firstBlock);
      if (blockType == ZfsConstants.ZbtHeader)
        return FatZap.Decode(this.ReadDnodeBody(dnode));
    }
    return MicroZap.Decode(firstBlock);
  }

  /// <summary>
  /// Reads logical block <paramref name="blockId"/> of a dnode, walking the indirect tree
  /// when <c>Levels &gt; 1</c>.
  /// </summary>
  private byte[] ReadDnodeDataBlock(Dnode.Builder dnode, ulong blockId) {
    if (dnode.BlkPtr0 == null)
      throw new InvalidDataException("ZFS: dnode has no block pointer.");
    if (dnode.Levels <= 1)
      return this.ReadBlock(dnode.BlkPtr0);

    // Walk down dn_nlevels - 1 levels of indirect blocks. Each level's block
    // holds indirect_block_size / sizeof(blkptr_t) pointers, so the entry to
    // follow at a given level is the block id divided by how many data blocks
    // each of that level's children covers.
    var indirectSize = 1 << dnode.IndirectBlockShift;
    var pointersPerIndirect = (ulong)(indirectSize / BlockPointer.Size);

    var bp = dnode.BlkPtr0;
    for (var level = dnode.Levels; level > 1; --level) {
      var indirect = this.ReadBlock(bp);
      var span = 1UL;
      for (var i = 0; i < level - 2; ++i) span *= pointersPerIndirect;
      var index = blockId / span % pointersPerIndirect;
      var ptrOffset = (int)(index * (ulong)BlockPointer.Size);
      if (ptrOffset + BlockPointer.Size > indirect.Length)
        throw new InvalidDataException("ZFS: dnode block id out of range of indirect block.");
      bp = BlockPointer.Read(indirect.AsSpan(ptrOffset, BlockPointer.Size));
    }
    return this.ReadBlock(bp);
  }

  /// <summary>Concatenates all logical data blocks of a dnode into one contiguous buffer.</summary>
  private byte[] ReadDnodeBody(Dnode.Builder dnode) {
    var blockCount = dnode.Levels <= 1 ? 1 : (int)(dnode.MaxBlockId + 1);
    if (blockCount == 1)
      return this.ReadDnodeDataBlock(dnode, 0);
    var blockSize = (int)dnode.DataBlockSizeInSectors * (int)ZfsConstants.SectorSize;
    var body = new byte[blockCount * blockSize];
    for (var i = 0; i < blockCount; i++) {
      var block = this.ReadDnodeDataBlock(dnode, (ulong)i);
      block.AsSpan(0, Math.Min(block.Length, blockSize)).CopyTo(body.AsSpan(i * blockSize));
    }
    return body;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(ZfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"ZFS: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var ms = new MemoryStream();
    this.ExtractTo(entry, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />,
  /// one record at a time through the dnode's indirect tree. Returns the byte count.
  /// </summary>
  public long ExtractTo(ZfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (!this._datasetDnodesById.TryGetValue(entry.ObjectId, out var dnode))
      throw new InvalidOperationException($"ZFS: dnode {entry.ObjectId} not found.");
    if (dnode.BlkPtr0 == null) return 0;

    var size = entry.Size;
    long written = 0;
    for (ulong blockId = 0; blockId <= dnode.MaxBlockId; ++blockId) {
      if (size > 0 && written >= size) break;
      var block = this.ReadDnodeDataBlock(dnode, blockId);
      var take = size > 0 ? (int)Math.Min(block.Length, size - written) : block.Length;
      if (take <= 0) break;
      destination.Write(block, 0, take);
      written += take;
    }
    return written;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._data.Dispose();
}
