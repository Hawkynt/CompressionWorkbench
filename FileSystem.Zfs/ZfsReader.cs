#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Reads a ZFS pool image produced by <see cref="ZfsWriter"/> (and compatible minimal
/// spec-aligned images). Traverses: vdev label → highest-TXG uberblock → MOS objset →
/// object directory ZAP → DSL dataset → dataset objset → master node / ROOT dir ZAP → file
/// dnodes. Validates Fletcher-4 checksums on all traversed blocks.
/// </summary>
public sealed class ZfsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<ZfsEntry> _entries = new();
  private readonly Dictionary<ulong, Dnode.Builder> _datasetDnodesById = new();
  private string? _poolName;

  public IReadOnlyList<ZfsEntry> Entries => this._entries;
  public string? PoolName => this._poolName;

  public ZfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

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
    var rootDirZap = this.ReadZap(dsDnodes[(int)rootDirObj.Value]);

    foreach (var (name, objId) in rootDirZap) {
      if ((ulong)dsDnodes.Count <= objId) continue;
      var fileDnode = dsDnodes[(int)objId];
      long size = (long)fileDnode.UsedBytes;
      if (fileDnode.Bonus != null && fileDnode.Bonus.Length >= 8)
        size = (long)BinaryPrimitives.ReadUInt64LittleEndian(fileDnode.Bonus);
      this._entries.Add(new ZfsEntry {
        Name = name,
        Size = size,
        IsDirectory = false,
        LastModified = null,
        ObjectId = objId,
      });
    }
  }

  private (Uberblock.Builder? Ub, byte[] NvBytes) ReadLabel(long labelOffset) {
    if (labelOffset + ZfsConstants.LabelSize > this._data.Length) return (null, []);

    var labelSpan = this._data.AsSpan((int)labelOffset, ZfsConstants.LabelSize);
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
    var block = new byte[psize];
    Array.Copy(this._data, offset, block, 0, psize);

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

  /// <summary>Reads a ZAP (microzap) from a dnode.</summary>
  private List<(string Name, ulong Value)> ReadZap(Dnode.Builder dnode) {
    if (dnode.BlkPtr0 == null)
      throw new InvalidDataException("ZFS: ZAP dnode has no block pointer.");
    var block = this.ReadBlock(dnode.BlkPtr0);
    return MicroZap.Decode(block);
  }

  public byte[] Extract(ZfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!this._datasetDnodesById.TryGetValue(entry.ObjectId, out var dnode))
      throw new InvalidOperationException($"ZFS: dnode {entry.ObjectId} not found.");
    if (dnode.BlkPtr0 == null)
      return [];
    var block = this.ReadBlock(dnode.BlkPtr0);
    var size = entry.Size > 0 && entry.Size <= block.Length ? (int)entry.Size : block.Length;
    if (size == block.Length) return block;
    var result = new byte[size];
    Array.Copy(block, 0, result, 0, size);
    return result;
  }

  public void Dispose() { }
}
