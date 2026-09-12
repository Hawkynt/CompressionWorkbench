#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Genuine copy-on-write in-place add for ZFS pool images produced by
/// <see cref="ZfsWriter"/> — the spec-faithful alternative to the whole-image
/// rebuild in <see cref="ZfsModifier"/>. A file is added (or replaced) in the
/// root dataset directory by writing NEW (CoW) blocks only for the changed path
/// and leaving every untouched data block byte-identical at its original offset.
/// <para>
/// The CoW path mirrors how ZFS itself commits a transaction group: it walks the
/// active uberblock down to the file's would-be location, then rewrites only the
/// tree blocks on that path into freshly-allocated free space, bottom-up:
/// <list type="number">
///   <item>the new file's data block + its block pointer;</item>
///   <item>a new file dnode, appended to the dataset's dnode array — which means
///   the whole dataset dnode-array block is CoW'd (it also carries the repointed
///   ROOT-directory dnode whose ZAP block is rewritten with the extra entry);</item>
///   <item>the rewritten ROOT directory micro-ZAP block (name → new object id);</item>
///   <item>the dataset <c>objset_phys_t</c> block (its meta-dnode now points at the
///   new dnode-array block);</item>
///   <item>the DSL dataset dnode in the MOS dnode array (its bonus <c>ds_bp</c> now
///   points at the new dataset objset) — so the MOS dnode-array block is CoW'd;</item>
///   <item>the MOS <c>objset_phys_t</c> block (meta-dnode → new MOS dnode array);</item>
///   <item>a new uberblock (txg+1, root_bp → new MOS objset) written into the next
///   slot of every label's uberblock array, with Fletcher-4 recomputed for every
///   new block.</item>
/// </list>
/// </para>
/// <para>
/// Every existing block (the seed file's data, the seed file's dnode bytes that are
/// copied verbatim into the new dnode-array block, the labels' NVList, the unused
/// uberblock slots) stays byte-identical at its original offset; the new blocks are
/// appended into the free tail of the data area, so the image size is unchanged and
/// no existing block is moved or overwritten. Verified by round-tripping the result
/// through <see cref="ZfsReader"/> (added + existing files list and extract
/// byte-identically) and by the CoW-offset proof (every pre-existing data block is
/// unchanged at its offset).
/// </para>
/// <para>
/// Cases the in-place adder does not handle — a multi-block (indirect) dnode array,
/// a fat-ZAP root directory, a file larger than a single 1&#160;MB data block, a
/// micro-ZAP that is already full, or a data area with no room to append the new
/// blocks — throw <see cref="NotSupportedException"/> so the caller can rebuild.
/// </para>
/// </summary>
public static class ZfsInPlaceAdder {

  /// <summary>
  /// Adds (or replaces) a small file in the root dataset directory of
  /// <paramref name="archive"/> via copy-on-write. Throws
  /// <see cref="NotSupportedException"/> for any shape the in-place path does not
  /// handle so the caller can rebuild instead.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    AddFile(image, name, data);

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    archive.Flush();
  }

  /// <summary>In-memory variant operating directly on the image bytes.</summary>
  public static void AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // Flat root only: no path separators (nested directories would need to CoW the
    // intermediate directory dnodes/ZAPs too, which this minimal adder does not do).
    var fileName = name.Replace('\\', '/');
    if (fileName.Contains('/'))
      throw new NotSupportedException("ZFS in-place add: nested directories not supported — use rebuild.");
    if (fileName.Length == 0)
      throw new InvalidDataException("ZFS in-place add: empty file name.");
    if (fileName.Length >= MicroZap.NameSize)
      throw new NotSupportedException("ZFS in-place add: name exceeds micro-ZAP limit — use rebuild.");

    const int labelSize = ZfsConstants.LabelSize;
    if (image.Length < 4L * labelSize)
      throw new InvalidDataException("ZFS in-place add: image too small.");

    var dataAreaStart = 2L * labelSize;
    var dataAreaEnd = image.Length - 2L * labelSize;

    // ── Read the active uberblock from L0 ─────────────────────────────────────
    var (ubSlotIndex, ub) = ReadActiveUberblock(image, 0);
    if (ub == null)
      throw new InvalidDataException("ZFS in-place add: no valid uberblock in L0.");

    // ── Walk the active tree down to the dataset dnode array ───────────────────
    var mosObjsetBlock = ReadBlock(image, ub.RootBp);
    var (mosMeta, mosOsType) = ObjsetPhys.Read(mosObjsetBlock);
    if (mosOsType != ZfsConstants.DmuOstMeta)
      throw new InvalidDataException("ZFS in-place add: root bp is not the MOS objset.");
    RequireSingleLevel(mosMeta, "MOS meta-dnode");

    var mosDnodeBlock = ReadBlock(image, mosMeta.BlkPtr0!);
    var mosDnodes = ReadDnodes(mosDnodeBlock);

    // Object directory (obj 1) → root_dataset → DSL dir → head_dataset_obj → DSL dataset.
    var objDir = RequireDnode(mosDnodes, 1, "MOS object directory");
    if (objDir.Type != ZfsConstants.DmuOtObjectDirectory)
      throw new InvalidDataException("ZFS in-place add: MOS obj 1 is not the object directory.");
    var objDirZap = ReadMicroZap(image, objDir);
    var rootDsDirId = FindZap(objDirZap, "root_dataset")
      ?? throw new InvalidDataException("ZFS in-place add: object directory has no 'root_dataset'.");

    var dslDir = RequireDnode(mosDnodes, (int)rootDsDirId, "DSL dir");
    if (dslDir.Bonus == null || dslDir.Bonus.Length < DslDirPhys.Size)
      throw new InvalidDataException("ZFS in-place add: DSL dir bonus too small.");
    var dslDirPhys = DslDirPhys.Decode(dslDir.Bonus);
    var headDsObj = dslDirPhys.HeadDatasetObj;
    if (headDsObj == 0 || (ulong)mosDnodes.Count <= headDsObj)
      throw new InvalidDataException("ZFS in-place add: DSL dir head_dataset_obj invalid.");

    var dslDsDnodeIndex = (int)headDsObj;
    var dslDsDnode = mosDnodes[dslDsDnodeIndex];
    if (dslDsDnode.Bonus == null || dslDsDnode.Bonus.Length < DslDatasetPhys.Size)
      throw new InvalidDataException("ZFS in-place add: DSL dataset bonus too small.");
    var dslDsPhys = DslDatasetPhys.Decode(dslDsDnode.Bonus);

    var dsObjsetBlock = ReadBlock(image, dslDsPhys.Bp);
    var (dsMeta, dsOsType) = ObjsetPhys.Read(dsObjsetBlock);
    if (dsOsType != ZfsConstants.DmuOstZfs)
      throw new InvalidDataException("ZFS in-place add: dataset bp is not the ZFS objset.");
    RequireSingleLevel(dsMeta, "dataset meta-dnode");

    var dsDnodeBlock = ReadBlock(image, dsMeta.BlkPtr0!);
    var dsDnodes = ReadDnodes(dsDnodeBlock);

    // Master node (obj 1) → ROOT → root directory dnode.
    var masterNode = RequireDnode(dsDnodes, 1, "dataset master node");
    var masterZap = ReadMicroZap(image, masterNode);
    var rootDirObj = FindZap(masterZap, "ROOT")
      ?? throw new InvalidDataException("ZFS in-place add: master node has no 'ROOT'.");
    if ((ulong)dsDnodes.Count <= rootDirObj)
      throw new InvalidDataException("ZFS in-place add: ROOT object id out of range.");

    var rootDirIndex = (int)rootDirObj;
    var rootDir = dsDnodes[rootDirIndex];
    if (rootDir.Type != ZfsConstants.DmuOtDirectoryContents)
      throw new InvalidDataException("ZFS in-place add: ROOT dnode is not a directory.");
    // Only a single-block micro-ZAP root directory is handled in place.
    var rootDirZap = ReadMicroZap(image, rootDir);

    // ── Replace-by-name: drop any existing entry for this file ────────────────
    // The replaced file's dnode slot is reused (its bytes are overwritten in the
    // CoW'd dnode-array block), so no array growth and no stale ZAP entry remains.
    int fileSlot;
    var existing = FindZapEntry(rootDirZap, fileName);
    if (existing != null) {
      fileSlot = (int)(existing.Value & ZfsConstants.ZfsDirentObjMask);
      if (fileSlot <= 0 || fileSlot >= dsDnodes.Count)
        throw new InvalidDataException("ZFS in-place add: existing entry points outside the dnode array.");
      rootDirZap.RemoveAll(e => string.Equals(e.Name, fileName, StringComparison.Ordinal));
    } else {
      // Reuse the first FREE dnode slot (Type == DMU_OT_NONE). The writer's PackDnodes
      // rounds the dnode-array block up to a 16 KB minimum, so the array typically holds
      // many trailing zero (free) slots beyond the live objects — reusing one keeps the
      // block size unchanged, which is what the CoW invariant needs. Slot 0 is reserved
      // (the null object), so the search starts at 1. If no free slot exists the array is
      // genuinely full and the rebuild fallback grows the block.
      fileSlot = -1;
      for (var i = 1; i < dsDnodes.Count; i++) {
        if (dsDnodes[i].Type == ZfsConstants.DmuOtNone) { fileSlot = i; break; }
      }
      if (fileSlot < 0)
        throw new NotSupportedException("ZFS in-place add: dataset dnode array block full — use rebuild.");
    }

    // The new ZAP must still fit one micro-ZAP block at its current size.
    if ((rootDirZap.Count + 1) > MicroZapCapacity(rootDirZapBlockSize: RootZapBlockSize(image, rootDir)))
      throw new NotSupportedException("ZFS in-place add: root directory micro-ZAP full — use rebuild.");

    // ── Allocate CoW destinations from the free tail of the data area ─────────
    var alloc = new TailAllocator(image, dataAreaStart, dataAreaEnd);

    var newTxg = ub.Txg + 1;

    // (1) file data block + its dnode.
    var fileDnode = BuildFileDnode(data, alloc, image, newTxg);
    dsDnodes[fileSlot] = fileDnode;

    // (2) ROOT directory micro-ZAP block (with the new entry), repoint ROOT dnode.
    rootDirZap.Add((fileName,
      (ZfsConstants.ZfsDirentTypeReg << ZfsConstants.ZfsDirentTypeShift) | (uint)fileSlot));
    var rootZapBlockSize = (int)rootDir.DataBlockSizeInSectors * (int)ZfsConstants.SectorSize;
    var rootZapBlock = MicroZap.Encode(rootDirZap, rootZapBlockSize);
    var rootZapBp = WriteBlock(rootZapBlock, alloc, image, newTxg,
      ZfsConstants.DmuOtDirectoryContents);
    dsDnodes[rootDirIndex] = RepointDirectoryDnode(rootDir, rootZapBp, rootZapBlock.Length);

    // (3) dataset dnode-array block (carries the new file dnode + repointed ROOT dnode).
    var newDsDnodeBlock = PackDnodes(dsDnodes, dsDnodeBlock.Length);
    var newDsDnodeBp = WriteBlock(newDsDnodeBlock, alloc, image, newTxg, ZfsConstants.DmuOtNone);

    // (4) dataset objset block (meta-dnode → new dnode array).
    var newDsMeta = RepointMetaDnode(dsMeta, newDsDnodeBp, newDsDnodeBlock.Length);
    var newDsObjsetBlock = new byte[ObjsetPhys.Size];
    ObjsetPhys.Write(newDsObjsetBlock, newDsMeta, ZfsConstants.DmuOstZfs);
    var newDsObjsetBp = WriteBlock(newDsObjsetBlock, alloc, image, newTxg, ZfsConstants.DmuOtNone);

    // (5) DSL dataset dnode (bonus ds_bp → new dataset objset), in MOS dnode array.
    dslDsPhys.Bp = newDsObjsetBp;
    var newDslDsBonus = DslDatasetPhys.Encode(dslDsPhys);
    var newDslDsDnode = CloneDnodeWithBonus(dslDsDnode, newDslDsBonus);
    mosDnodes[dslDsDnodeIndex] = newDslDsDnode;

    // (6) MOS dnode-array block.
    var newMosDnodeBlock = PackDnodes(mosDnodes, mosDnodeBlock.Length);
    var newMosDnodeBp = WriteBlock(newMosDnodeBlock, alloc, image, newTxg, ZfsConstants.DmuOtNone);

    // (7) MOS objset block (meta-dnode → new MOS dnode array).
    var newMosMeta = RepointMetaDnode(mosMeta, newMosDnodeBp, newMosDnodeBlock.Length);
    var newMosObjsetBlock = new byte[ObjsetPhys.Size];
    ObjsetPhys.Write(newMosObjsetBlock, newMosMeta, ZfsConstants.DmuOstMeta);
    var newMosObjsetBp = WriteBlock(newMosObjsetBlock, alloc, image, newTxg, ZfsConstants.DmuOtNone);

    // (8) New uberblock → next slot of every label's uberblock array.
    var newUb = new Uberblock.Builder {
      Version = ub.Version,
      Txg = newTxg,
      GuidSum = ub.GuidSum,
      Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      RootBp = newMosObjsetBp,
      SoftwareVersion = ub.SoftwareVersion,
    };
    var newSlotIndex = (ubSlotIndex + 1) % ZfsConstants.UberblockCount;
    WriteUberblockToAllLabels(image, newSlotIndex, newUb, newTxg);
  }

  // ── Uberblock handling ──────────────────────────────────────────────────────

  /// <summary>
  /// Finds the highest-txg uberblock in the label at <paramref name="labelOffset"/>,
  /// returning its slot index and parsed contents.
  /// </summary>
  private static (int SlotIndex, Uberblock.Builder? Ub) ReadActiveUberblock(byte[] image, long labelOffset) {
    Uberblock.Builder? best = null;
    var bestSlot = -1;
    for (var i = 0; i < ZfsConstants.UberblockCount; i++) {
      var slotStart = (int)labelOffset + ZfsConstants.UberblockArrayOffset + i * ZfsConstants.UberblockSize;
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slotStart, 8));
      if (magic != ZfsConstants.UberblockMagic) continue;
      Uberblock.Builder ub;
      try { ub = Uberblock.Read(image.AsSpan(slotStart, ZfsConstants.UberblockSize)); } catch { continue; }
      if (best == null || ub.Txg > best.Txg) { best = ub; bestSlot = i; }
    }
    return (bestSlot, best);
  }

  /// <summary>
  /// Writes <paramref name="ub"/> into slot <paramref name="slotIndex"/> of all four
  /// vdev labels (L0/L1 at the front, L2/L3 at the back), matching the writer's layout.
  /// The previously-active uberblock in the other slots is left intact, so the image is
  /// still readable if the new uberblock were ever rejected — the reader picks the
  /// highest txg, which is the one just written.
  /// </summary>
  private static void WriteUberblockToAllLabels(byte[] image, int slotIndex, Uberblock.Builder ub, ulong newTxg) {
    const int labelSize = ZfsConstants.LabelSize;
    var labelOffsets = new long[] {
      0,
      labelSize,
      image.Length - 2L * labelSize,
      image.Length - labelSize,
    };
    // Guard: the target slot must not already hold a higher-or-equal txg (it should be
    // empty/older for a fresh add; reusing a wrapped slot is fine because newTxg wins).
    foreach (var labelOffset in labelOffsets) {
      var slotStart = (int)labelOffset + ZfsConstants.UberblockArrayOffset + slotIndex * ZfsConstants.UberblockSize;
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slotStart, 8));
      if (magic == ZfsConstants.UberblockMagic) {
        var existingTxg = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slotStart + Uberblock.TxgOffset, 8));
        if (existingTxg >= newTxg)
          throw new NotSupportedException("ZFS in-place add: uberblock slot wrap collision — use rebuild.");
      }
      Uberblock.Write(image.AsSpan(slotStart, ZfsConstants.UberblockSize), ub);
    }
  }

  // ── Block read/write ────────────────────────────────────────────────────────

  /// <summary>Reads the block referenced by a blkptr_t and verifies Fletcher-4.</summary>
  private static byte[] ReadBlock(byte[] image, BlockPointer.Builder bp) {
    var psize = ((int)bp.Psize + 1) * (int)ZfsConstants.SectorSize;
    var offset = (long)bp.OffsetSectors * ZfsConstants.SectorSize;
    if (offset < 0 || offset + psize > image.Length)
      throw new InvalidDataException($"ZFS in-place add: blkptr offset {offset}+{psize} out of range.");
    var block = image.AsSpan((int)offset, psize).ToArray();
    if (bp.Checksum == ZfsConstants.ZioChecksumFletcher4) {
      var actual = Fletcher4.Compute(block);
      if (actual != bp.Cksum)
        throw new InvalidDataException($"ZFS in-place add: Fletcher-4 mismatch at {offset}.");
    }
    return block;
  }

  /// <summary>
  /// Writes <paramref name="block"/> into a freshly-allocated free region of the data
  /// area, computes Fletcher-4, and returns a populated blkptr_t referencing it.
  /// </summary>
  private static BlockPointer.Builder WriteBlock(
    byte[] block, TailAllocator alloc, byte[] image, ulong txg, byte type) {
    if (block.Length % ZfsConstants.SectorSize != 0)
      throw new ArgumentException("Block must be sector-aligned.", nameof(block));

    var offset = alloc.Allocate(block.Length);
    block.CopyTo(image.AsSpan((int)offset));

    var cksum = Fletcher4.Compute(block);
    var sectorsMinusOne = (uint)(block.Length / ZfsConstants.SectorSize) - 1;
    return new BlockPointer.Builder {
      Vdev = 0,
      Grid = 0,
      AsizeSectors = sectorsMinusOne,
      OffsetSectors = (ulong)(offset / ZfsConstants.SectorSize),
      Lsize = sectorsMinusOne,
      Psize = sectorsMinusOne,
      Compression = ZfsConstants.ZioCompressOff,
      Checksum = ZfsConstants.ZioChecksumFletcher4,
      Type = type,
      Level = 0,
      Birth = txg,
      Fill = 1,
      Cksum = cksum,
    };
  }

  // ── Dnode helpers ─────────────────────────────────────────────────────────

  private static List<Dnode.Builder> ReadDnodes(byte[] block) {
    var dnodes = new List<Dnode.Builder>();
    for (var i = 0; i + Dnode.Size <= block.Length; i += Dnode.Size)
      dnodes.Add(Dnode.Read(block.AsSpan(i, Dnode.Size)));
    return dnodes;
  }

  /// <summary>
  /// Packs dnodes back into a block of the SAME size as the original so the CoW'd
  /// block reuses the original geometry (and the meta-dnode's block-size fields stay
  /// valid). Trailing slots beyond the dnode list stay zero.
  /// </summary>
  private static byte[] PackDnodes(List<Dnode.Builder> dnodes, int blockSize) {
    if (dnodes.Count * Dnode.Size > blockSize)
      throw new NotSupportedException("ZFS in-place add: dnode array no longer fits its block — use rebuild.");
    var block = new byte[blockSize];
    for (var i = 0; i < dnodes.Count; i++)
      Dnode.Write(block.AsSpan(i * Dnode.Size, Dnode.Size), dnodes[i]);
    return block;
  }

  private static Dnode.Builder RequireDnode(List<Dnode.Builder> dnodes, int index, string what) {
    if (index < 0 || index >= dnodes.Count)
      throw new InvalidDataException($"ZFS in-place add: {what} dnode out of range.");
    return dnodes[index];
  }

  private static void RequireSingleLevel(Dnode.Builder meta, string what) {
    if (meta.BlkPtr0 == null)
      throw new InvalidDataException($"ZFS in-place add: {what} has no block pointer.");
    if (meta.Levels > 1)
      throw new NotSupportedException($"ZFS in-place add: {what} is multi-level (indirect) — use rebuild.");
  }

  /// <summary>Builds a single-block file dnode and writes its data block.</summary>
  private static Dnode.Builder BuildFileDnode(byte[] data, TailAllocator alloc, byte[] image, ulong txg) {
    var blockSize = Math.Max((int)ZfsConstants.SectorSize, NextPow2Ge(data.Length));
    while (blockSize < data.Length && blockSize < 1024 * 1024) blockSize *= 2;
    if (blockSize < data.Length)
      throw new NotSupportedException("ZFS in-place add: file > 1 MB not supported — use rebuild.");

    var block = new byte[blockSize];
    data.CopyTo(block, 0);
    var bp = WriteBlock(block, alloc, image, txg, ZfsConstants.DmuOtPlainFileContents);

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

  /// <summary>
  /// Returns a copy of a directory dnode repointed at a new (single-block) ZAP block,
  /// preserving its bonus (the S_IFDIR mode) so it still reads back as a directory.
  /// </summary>
  private static Dnode.Builder RepointDirectoryDnode(Dnode.Builder dir, BlockPointer.Builder zapBp, int zapBlockLen) {
    return new Dnode.Builder {
      Type = dir.Type,
      IndirectBlockShift = dir.IndirectBlockShift,
      Levels = 1,
      NumBlkPtr = 1,
      BonusType = dir.BonusType,
      Checksum = dir.Checksum,
      Compress = dir.Compress,
      Flags = dir.Flags,
      DataBlockSizeInSectors = (uint)(zapBlockLen / ZfsConstants.SectorSize),
      MaxBlockId = 0,
      UsedBytes = (ulong)zapBlockLen,
      BlkPtr0 = zapBp,
      Bonus = dir.Bonus,
      BonusLen = dir.BonusLen,
    };
  }

  /// <summary>Returns a copy of a meta-dnode repointed at a new dnode-array block.</summary>
  private static Dnode.Builder RepointMetaDnode(Dnode.Builder meta, BlockPointer.Builder bp, int blockLen) {
    return new Dnode.Builder {
      Type = meta.Type,
      IndirectBlockShift = meta.IndirectBlockShift,
      Levels = 1,
      NumBlkPtr = 1,
      BonusType = meta.BonusType,
      Checksum = meta.Checksum,
      Compress = meta.Compress,
      Flags = meta.Flags,
      DataBlockSizeInSectors = (uint)(blockLen / ZfsConstants.SectorSize),
      MaxBlockId = 0,
      UsedBytes = (ulong)blockLen,
      BlkPtr0 = bp,
      Bonus = meta.Bonus,
      BonusLen = meta.BonusLen,
    };
  }

  /// <summary>Returns a copy of a (bonus-only) dnode with a replaced bonus payload.</summary>
  private static Dnode.Builder CloneDnodeWithBonus(Dnode.Builder src, byte[] bonus) {
    return new Dnode.Builder {
      Type = src.Type,
      IndirectBlockShift = src.IndirectBlockShift,
      Levels = src.Levels,
      NumBlkPtr = src.NumBlkPtr,
      BonusType = src.BonusType,
      Checksum = src.Checksum,
      Compress = src.Compress,
      Flags = src.Flags,
      DataBlockSizeInSectors = src.DataBlockSizeInSectors,
      MaxBlockId = src.MaxBlockId,
      UsedBytes = src.UsedBytes,
      BlkPtr0 = src.BlkPtr0,
      Bonus = bonus,
      BonusLen = (ushort)bonus.Length,
    };
  }

  // ── Micro-ZAP helpers ──────────────────────────────────────────────────────

  /// <summary>Reads a single-block micro-ZAP from a dnode; rejects fat ZAPs.</summary>
  private static List<(string Name, ulong Value)> ReadMicroZap(byte[] image, Dnode.Builder dnode) {
    if (dnode.BlkPtr0 == null)
      throw new InvalidDataException("ZFS in-place add: ZAP dnode has no block pointer.");
    if (dnode.Levels > 1)
      throw new NotSupportedException("ZFS in-place add: indirect (fat) ZAP not supported — use rebuild.");
    var block = ReadBlock(image, dnode.BlkPtr0);
    if (block.Length >= 8) {
      var blockType = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(0, 8));
      if (blockType == ZfsConstants.ZbtHeader)
        throw new NotSupportedException("ZFS in-place add: fat ZAP directory not supported — use rebuild.");
    }
    return MicroZap.Decode(block);
  }

  private static int RootZapBlockSize(byte[] image, Dnode.Builder rootDir)
    => (int)rootDir.DataBlockSizeInSectors * (int)ZfsConstants.SectorSize;

  private static int MicroZapCapacity(int rootDirZapBlockSize)
    => (rootDirZapBlockSize - MicroZap.HeaderSize) / MicroZap.EntrySize;

  private static ulong? FindZap(List<(string Name, ulong Value)> zap, string key) {
    foreach (var (k, v) in zap)
      if (string.Equals(k, key, StringComparison.Ordinal)) return v;
    return null;
  }

  private static ulong? FindZapEntry(List<(string Name, ulong Value)> zap, string key) {
    foreach (var (k, v) in zap)
      if (string.Equals(k, key, StringComparison.Ordinal)) return v;
    return null;
  }

  // ── Free-tail allocator ─────────────────────────────────────────────────────

  /// <summary>
  /// Allocates sector-aligned regions from the FREE TAIL of the data area — the bytes
  /// after the highest-offset block any existing block pointer references. The writer
  /// lays out every block strictly forward from the data-area start, so all free space
  /// is a single contiguous run at the end; appending there guarantees no existing block
  /// is moved or overwritten (the CoW invariant), and the image size never changes.
  /// </summary>
  private sealed class TailAllocator {
    private readonly long _end;
    private long _next;

    public TailAllocator(byte[] image, long dataAreaStart, long dataAreaEnd) {
      this._end = dataAreaEnd;
      // High-water mark = end of the last referenced data block. We scan the active
      // tree's blocks; but a simpler, conservative bound that also stays correct under
      // replace (which frees blocks but we do not reclaim) is the max end offset over
      // every block pointer reachable from the active uberblock. To avoid a second full
      // walk we instead start from a scan of the whole image for the highest non-zero
      // sector below dataAreaEnd, which is an over-approximation that never overlaps a
      // live block. That is too coarse; instead the caller-independent safe choice is
      // the data-area start advanced past all currently-allocated blocks, which we
      // compute by scanning block pointers — done in ComputeHighWater.
      this._next = Math.Max(dataAreaStart, ComputeHighWater(image, dataAreaStart, dataAreaEnd));
    }

    public long Allocate(int bytes) {
      var align = Math.Max((long)ZfsConstants.SectorSize, NextPow2Long(bytes));
      var aligned = (this._next + align - 1) & ~(align - 1);
      if (aligned + bytes > this._end)
        throw new NotSupportedException(
          $"ZFS in-place add: no free space to append {bytes} bytes (need {aligned + bytes}, max {this._end}) — use rebuild.");
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

  /// <summary>
  /// Computes the high-water mark of the data area: the end offset of the
  /// highest-addressed block referenced by any block pointer reachable from the active
  /// uberblock. New CoW blocks are appended strictly above this, so they never collide
  /// with a live block, and pre-existing blocks stay byte-identical.
  /// </summary>
  private static long ComputeHighWater(byte[] image, long dataAreaStart, long dataAreaEnd) {
    var high = dataAreaStart;
    void Consider(BlockPointer.Builder? bp) {
      if (bp == null || bp.OffsetSectors == 0) return;
      var offset = (long)bp.OffsetSectors * ZfsConstants.SectorSize;
      var psize = ((long)bp.Psize + 1) * ZfsConstants.SectorSize;
      var end = offset + psize;
      if (offset >= dataAreaStart && end <= dataAreaEnd && end > high) high = end;
    }

    try {
      var (_, ub) = ReadActiveUberblock(image, 0);
      if (ub == null) return high;
      Consider(ub.RootBp);

      var mosObjset = ReadBlock(image, ub.RootBp);
      var (mosMeta, _) = ObjsetPhys.Read(mosObjset);
      Consider(mosMeta.BlkPtr0);
      if (mosMeta.BlkPtr0 == null || mosMeta.Levels > 1) return high;

      var mosDnodeBlock = ReadBlock(image, mosMeta.BlkPtr0);
      var mosDnodes = ReadDnodes(mosDnodeBlock);
      foreach (var dn in mosDnodes) ConsiderDnodeBlocks(image, dn, Consider);

      // Descend into the head dataset to cover its dnode array + every file/dir block.
      foreach (var dn in mosDnodes) {
        if (dn.Type != ZfsConstants.DmuOtDslDataset || dn.Bonus == null || dn.Bonus.Length < DslDatasetPhys.Size)
          continue;
        var ds = DslDatasetPhys.Decode(dn.Bonus);
        Consider(ds.Bp);
        if (ds.Bp.OffsetSectors == 0) continue;
        byte[] dsObjset;
        try { dsObjset = ReadBlock(image, ds.Bp); } catch { continue; }
        var (dsMeta, _) = ObjsetPhys.Read(dsObjset);
        Consider(dsMeta.BlkPtr0);
        if (dsMeta.BlkPtr0 == null || dsMeta.Levels > 1) continue;
        var dsDnodeBlock = ReadBlock(image, dsMeta.BlkPtr0);
        foreach (var fileDn in ReadDnodes(dsDnodeBlock)) ConsiderDnodeBlocks(image, fileDn, Consider);
      }
    } catch {
      // On any parse hiccup, fall back to scanning the data area for the last non-zero
      // sector — a safe over-approximation (never below a live block).
      return Math.Max(high, LastNonZeroSectorEnd(image, dataAreaStart, dataAreaEnd));
    }
    return high;
  }

  private static void ConsiderDnodeBlocks(byte[] image, Dnode.Builder dn, Action<BlockPointer.Builder?> consider) {
    if (dn.BlkPtr0 == null) return;
    consider(dn.BlkPtr0);
    if (dn.Levels <= 1) return;
    // Indirect: the L1 block holds child block pointers; consider each child too.
    try {
      var indirect = ReadBlock(image, dn.BlkPtr0);
      for (var off = 0; off + BlockPointer.Size <= indirect.Length; off += BlockPointer.Size) {
        var child = BlockPointer.Read(indirect.AsSpan(off, BlockPointer.Size));
        if (child.OffsetSectors != 0) consider(child);
      }
    } catch { /* best effort */ }
  }

  private static long LastNonZeroSectorEnd(byte[] image, long dataAreaStart, long dataAreaEnd) {
    var sector = (int)ZfsConstants.SectorSize;
    for (var off = dataAreaEnd - sector; off >= dataAreaStart; off -= sector) {
      var span = image.AsSpan((int)off, sector);
      var allZero = true;
      foreach (var b in span) if (b != 0) { allZero = false; break; }
      if (!allZero) return off + sector;
    }
    return dataAreaStart;
  }

  private static int NextPow2Ge(int n) {
    if (n <= 1) return 1;
    var p = 1;
    while (p < n) p <<= 1;
    return p;
  }
}
