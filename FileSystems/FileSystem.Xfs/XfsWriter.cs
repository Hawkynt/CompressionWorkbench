#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Xfs;

/// <summary>
/// Writes a minimal XFS v5 filesystem image that <c>xfs_repair -n -f</c> accepts.
///
/// <para>Each allocation group (AG) is laid out as:</para>
/// <code>
///   block 0:  SB (sector 0), AGF (sector 1), AGI (sector 2), AGFL (sector 3)
///   block 1:  bnobt root (1 leaf covering the free extent)
///   block 2:  cntbt root (same key ordering by length)
///   block 3:  inobt root (1 leaf covering the root-inode chunk for AG 0; empty for AG 1+)
///   block 4:  root-inode chunk start (64 inodes × 256 B = 16 KiB = 4 blocks) — AG 0 only
///   block 8+: free space (used for file data in AG 0)
/// </code>
/// <para>All v5 metadata blocks (SB, AGF, AGI, AGFL, btree blocks, dinodes) are
/// stamped with CRC-32C using the Castagnoli polynomial. Big-endian for most
/// on-disk fields; CRC fields are little-endian per XFS v5 convention.</para>
/// <para>Scope: short-form root directory; extent-based file data inline in one
/// BMBT record; no RMAP, no REFCOUNT, no quotas, no realtime volume, no
/// sparse-inode feature.</para>
/// </summary>
public sealed class XfsWriter {
  private const int BlockSize = 4096;
  private const int SectorSize = 512;
  private const int InodeSize = 256;     // v3 dinode.
  private const int InodesPerBlock = BlockSize / InodeSize; // 16
  private const int InodesPerChunk = 64; // XFS_INODES_PER_CHUNK
  private const int InodeChunkBlocks = InodesPerChunk / InodesPerBlock; // 4

  // XFS magic numbers.
  private const uint XfsMagic = 0x58465342;   // "XFSB"
  private const ushort InodeMagic = 0x494E;   // "IN"
  private const uint AgfMagic = 0x58414746;   // "XAGF"
  private const uint AgiMagic = 0x58414749;   // "XAGI"
  private const uint AgflMagic = 0x5841464C;  // "XAFL"
  private const uint BnobtV5Magic = 0x41423342;  // "AB3B" — v5 bnobt (CRC)
  private const uint CntbtV5Magic = 0x41423343;  // "AB3C" — v5 cntbt (CRC)
  private const uint InobtV5Magic = 0x49414233;  // "IAB3" — v5 inobt (CRC)

  // Geometry — 2 AGs × 4096 blocks × 4 KiB = 16 MiB per AG, 32 MiB total.
  // xfs kernel validate_sb_common requires agblocks × blocksize ≥ XFS_MIN_AG_BYTES
  // (= 16 MiB); smaller AGs trigger "SB sanity check failed".
  private const int AgBlocks = 4096;
  private const int AgCount = 2;
  private const byte BlockLog = 12;     // log2(4096)
  private const byte SectorLog = 9;     // log2(512)
  private const byte InodeLog = 8;      // log2(256)
  private const byte InoPbLog = 4;      // log2(16)
  private const byte AgBlkLog = 12;     // log2(4096)
  private const byte AgInoLog = AgBlkLog + InoPbLog; // 16

  // AG-internal block positions (agbno).
  private const int AgfSector = 1;
  private const int AgiSector = 2;
  private const int AgflSector = 3;
  private const int BnobtBlock = 1;
  private const int CntbtBlock = 2;
  private const int InobtBlock = 3;
  // xfs_repair (xfsprogs 6.6) calculates the expected first-inode agbno from
  // geometry as `XFS_PREALLOC_BLOCKS(mp) + AGFL_PREALLOCATION`. For a 2048-block
  // AG with 4 KiB blocks and 256-byte inodes, this comes out to agbno 72 (1152
  // relative to AG0). Placing the root inode chunk elsewhere triggers "sb root
  // inode value X inconsistent with calculated value 1152" and the subsequent
  // "root inode chunk not found" fatal error in phase 2.
  private const int InodeChunkBlock = 72;
  private const int InodeChunkEndBlock = InodeChunkBlock + InodeChunkBlocks; // 76

  // Inode numbering in AG 0: rootino = (agbno << InoPbLog) = 72 << 4 = 1152.
  private const ulong RootIno = (ulong)InodeChunkBlock * InodesPerBlock; // 1152
  private const ulong FirstChildIno = RootIno + 1; // 1153 — but sb_rbmino=1153 and
  // sb_rsumino=1154 collide with children; skip past them. Set sb_rbmino and
  // sb_rsumino to NULLFSINO (0) instead — they're only meaningful when a
  // realtime subvolume exists, which we don't have.

  // XFS v5 superblock version. Bits:
  //   0x0005 VERSION_5
  //   0x0080 NLINKBIT
  //   0x0200 ALIGNBIT
  //   0x0400 DALIGNBIT
  //   0x1000 LOGV2BIT
  //   0x2000 SECTORBIT
  //   0x4000 EXTFLGBIT
  //   0x8000 MOREBITSBIT
  // 0xB4A5 matches plain-v5 mkfs.xfs output.
  private const ushort XfsSbVersion5 = 0xB4A5;

  // sb_crc, di_crc, AGF crc, AGI crc, AGFL crc, btree crc: offsets within block.
  private const int SbCrcOffset = 224;
  private const int DiCrcOffset = 100;
  // AGF CRC lives at byte offset 216 per Linux kernel `struct xfs_agf`:
  //   magic(4)+ver(4)+seq(4)+len(4)+roots[3](12)+levels[3](12)+flfirst(4)
  //   +fllast(4)+flcount(4)+freeblks(4)+longest(4)+btreeblks(4)+uuid(16)
  //   +rmap_blocks(4)+refcount_blocks(4)+refcount_root(4)+refcount_level(4)
  //   +spare64[14](112)+lsn(8) = 216. Then agf_crc(4)+agf_spare2(4) = 224.
  private const int AgfCrcOffset = 216;
  private const int AgiCrcOffset = 312;
  private const int AgflCrcOffset = 32;
  // xfs_btree_block short-form v5: magic(4)+level(2)+numrecs(2)+leftsib(4)
  // +rightsib(4)+blkno(8)+lsn(8)+uuid(16)+owner(4) = 52. Then bb_crc(4).
  // Records start immediately after bb_crc at offset 56 (no bb_pad).
  private const int BtreeCrcOffset = 52;
  private const int BtreeRecOffset = 56;

  private readonly List<(string name, byte[] data)> _files = [];
  private static readonly Guid VolumeUuid = new("7fb1c7a0-b71b-4f34-9d8a-5c7f6a2e11d3");
  private static readonly byte[] UuidBytes = VolumeUuid.ToByteArray();

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    // Preserve the full path; subdirectory inodes are materialised in WriteTo.
    // Normalise separators and trim leading/trailing slashes.
    var normalized = name.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0)
      throw new ArgumentException("XfsWriter: file name must not be empty.", nameof(name));
    this._files.Add((normalized, data));
  }

  /// <summary>
  /// A node in the in-memory directory tree built from path-separated file
  /// names. Each node maps to exactly one XFS inode; directories carry a
  /// short-form child list, regular files carry their payload.
  /// </summary>
  private sealed class TreeNode {
    public required string Name;            // single path component ("" for root)
    public bool IsDirectory;
    public byte[] Data = [];                // payload for regular files
    public readonly List<TreeNode> Children = [];
    public ulong Ino;                       // assigned inode number
    public int Slot;                        // slot index in the root inode chunk
    public int DataBlock;                   // first data block (files / block-form dirs)
    public int BlockCount;                  // data blocks (files / block-form dirs)
    public bool BlockFormDirectory;         // directory uses out-of-line dir2 data blocks
    public long DirByteSize;                // di_size for a block-form directory
  }

  /// <summary>
  /// Builds the directory tree from the path-separated file names. Returns the
  /// root node plus a flat list of every node in inode-slot order: the root
  /// occupies slot 0, slots 1/2 are reserved for rbmino/rsumino, and every
  /// directory/file node is assigned slots 3+. Directories sort before the
  /// files at each level so a parent's child inodes are contiguous, but the
  /// exact ordering is irrelevant — only the slot/inode assignment matters.
  /// </summary>
  private (TreeNode Root, List<TreeNode> Nodes) BuildTree(int firstNodeSlot) {
    var root = new TreeNode { Name = "", IsDirectory = true };

    foreach (var (path, data) in this._files) {
      var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      var cursor = root;
      for (var p = 0; p < parts.Length; p++) {
        var leaf = parts[p];
        if (leaf.Length > 250) leaf = leaf[..250];
        var isLast = p == parts.Length - 1;
        if (isLast) {
          // A regular file leaf.
          cursor.Children.Add(new TreeNode { Name = leaf, IsDirectory = false, Data = data });
        } else {
          // An intermediate directory: reuse if it already exists.
          var existing = cursor.Children.FirstOrDefault(c => c.IsDirectory && c.Name == leaf);
          if (existing is null) {
            existing = new TreeNode { Name = leaf, IsDirectory = true };
            cursor.Children.Add(existing);
          }
          cursor = existing;
        }
      }
    }

    // Flatten in slot order: root first, then breadth-first over the tree.
    var nodes = new List<TreeNode> { root };
    var queue = new Queue<TreeNode>();
    queue.Enqueue(root);
    while (queue.Count > 0) {
      var dir = queue.Dequeue();
      foreach (var child in dir.Children) {
        nodes.Add(child);
        if (child.IsDirectory) queue.Enqueue(child);
      }
    }

    // Assign inode slots/numbers. Root is slot 0; non-root nodes start at
    // firstNodeSlot (3, past rbmino/rsumino).
    root.Slot = 0;
    root.Ino = RootIno;
    var slot = firstNodeSlot;
    for (var i = 1; i < nodes.Count; i++) {
      nodes[i].Slot = slot;
      nodes[i].Ino = RootIno + (ulong)slot;
      slot++;
    }
    return (root, nodes);
  }

  public void WriteTo(Stream output) {
    // Root chunk slot layout (AG 0, inode chunk starting at agbno=InodeChunkBlock):
    //   slot 0  = root directory (rootino)
    //   slot 1  = sb_rbmino (realtime bitmap inode) — empty regular file
    //   slot 2  = sb_rsumino (realtime summary inode) — empty regular file
    //   slot 3+ = directory and file inodes (subdirectory tree)
    //   remaining = free
    const int FirstNodeSlot = 3;

    var (_, nodes) = BuildTree(FirstNodeSlot);

    // Used slots = root + rbm + rsum + every tree node beyond the root.
    // The inode space grows in whole 64-inode chunks (4 blocks each) so the
    // tree can hold far more than a single chunk's worth of inodes.
    var usedInodeSlots = 2 + nodes.Count;  // (root counted in nodes) + rbm + rsum
    var chunkCount = (usedInodeSlots + InodesPerChunk - 1) / InodesPerChunk;
    if (chunkCount < 1) chunkCount = 1;
    var totalInodeSlots = chunkCount * InodesPerChunk;
    var inodeBlocks = chunkCount * InodeChunkBlocks;
    var dataStartBlock = InodeChunkBlock + inodeBlocks;

    // ── Allocate data blocks for regular-file nodes, in slot order ──
    var nextBlock = dataStartBlock;
    foreach (var node in nodes) {
      if (node.IsDirectory) continue;
      node.DataBlock = nextBlock;
      node.BlockCount = Math.Max(1, (node.Data.Length + BlockSize - 1) / BlockSize);
      nextBlock += node.BlockCount;
    }

    // ── Allocate directory data blocks for any directory whose short-form
    //    encoding overflows the inode literal area (block-form dir2) ──
    foreach (var node in nodes) {
      if (!node.IsDirectory) continue;
      if (FitsShortForm(node)) continue;
      var (blocks, byteSize) = MeasureBlockFormDirectory(node);
      node.BlockFormDirectory = true;
      node.DataBlock = nextBlock;
      node.BlockCount = blocks;
      node.DirByteSize = byteSize;
      nextBlock += blocks;
    }
    // Reserve 64 blocks (256 KiB) for an internal log at the tail of AG 0.
    const int LogBlocks = 64;
    var logStartAgBno = nextBlock;
    nextBlock += LogBlocks;
    var logStartFsBno = (ulong)logStartAgBno;

    // All inodes, file/dir data and the log live in AG 0. If they would spill
    // past the AG boundary they'd overwrite AG 1's metadata, so refuse instead.
    if (nextBlock > AgBlocks)
      throw new InvalidOperationException(
        $"XfsWriter: total content exceeds one allocation group ({nextBlock} of {AgBlocks} blocks).");

    // Total blocks: pad to AgCount × AgBlocks so every AG has the same size.
    var totalBlocks = Math.Max(nextBlock, AgBlocks * AgCount);
    var image = new byte[totalBlocks * BlockSize];

    // Free-extent bookkeeping (AG 0: single trailing extent after log).
    var freeStartAg0 = logStartAgBno + LogBlocks;
    var freeLenAg0 = AgBlocks - freeStartAg0;
    // Free-extent bookkeeping (AG 1+): no inode chunk, no files — the region
    // past the per-AG metadata header is entirely free.
    var freeStartAgN = InodeChunkBlock;  // no inode chunk in AG 1+
    var freeLenAgN = AgBlocks - freeStartAgN;

    var freeInodeSlots = totalInodeSlots - usedInodeSlots;

    // ── Per-AG metadata ──
    for (var ag = 0; ag < AgCount; ag++) {
      var agByteOffset = ag * AgBlocks * BlockSize;

      WriteSuperblock(image.AsSpan(agByteOffset),
        totalBlocks: (ulong)(AgBlocks * AgCount),
        logStart: logStartFsBno,
        logBlocks: LogBlocks,
        icount: (ulong)(ag == 0 ? totalInodeSlots : 0),
        ifree: (ulong)(ag == 0 ? freeInodeSlots : 0),
        fdblocks: (ulong)(freeLenAg0 + (AgCount - 1) * freeLenAgN));

      WriteAgf(image.AsSpan(agByteOffset + AgfSector * SectorSize),
        agNumber: (uint)ag,
        agBlocks: AgBlocks,
        bnobtRoot: BnobtBlock,
        cntbtRoot: CntbtBlock,
        freeBlocks: (uint)(ag == 0 ? freeLenAg0 : freeLenAgN),
        longest: (uint)(ag == 0 ? freeLenAg0 : freeLenAgN));

      WriteAgi(image.AsSpan(agByteOffset + AgiSector * SectorSize),
        agNumber: (uint)ag,
        agBlocks: AgBlocks,
        inodeCount: ag == 0 ? (uint)totalInodeSlots : 0u,
        freeInodes: ag == 0 ? (uint)freeInodeSlots : 0u,
        inobtRoot: InobtBlock,
        inobtLevel: 1u,  // always 1 — an empty btree still has a root leaf
        newIno: ag == 0 ? (uint)RootIno : 0xFFFFFFFFu);

      WriteAgfl(image.AsSpan(agByteOffset + AgflSector * SectorSize), agNumber: (uint)ag);

      // bb_blkno is the **disk sector number**, not the filesystem block number.
      // For 4 KiB block × 512 B sector, sector = fsblock × 8.
      const int SectorsPerBlock = BlockSize / SectorSize;

      WriteBnobt(image.AsSpan(agByteOffset + BnobtBlock * BlockSize),
        agNumber: (uint)ag,
        selfSector: (ulong)(ag * AgBlocks + BnobtBlock) * SectorsPerBlock,
        freeStart: (uint)(ag == 0 ? freeStartAg0 : freeStartAgN),
        freeLen: (uint)(ag == 0 ? freeLenAg0 : freeLenAgN));

      WriteCntbt(image.AsSpan(agByteOffset + CntbtBlock * BlockSize),
        agNumber: (uint)ag,
        selfSector: (ulong)(ag * AgBlocks + CntbtBlock) * SectorsPerBlock,
        freeStart: (uint)(ag == 0 ? freeStartAg0 : freeStartAgN),
        freeLen: (uint)(ag == 0 ? freeLenAg0 : freeLenAgN));

      WriteInobt(image.AsSpan(agByteOffset + InobtBlock * BlockSize),
        agNumber: (uint)ag,
        selfSector: (ulong)(ag * AgBlocks + InobtBlock) * SectorsPerBlock,
        chunkCount: ag == 0 ? chunkCount : 0,
        startAgino: (uint)RootIno,
        usedSlots: usedInodeSlots);
    }

    // ── Directory inodes (root + every subdirectory) ──
    // Each directory inode carries a short-form directory whose parent is the
    // enclosing directory (the root's parent is itself). Build a parent lookup
    // so each child can name its parent inode and so we can fix up nlinks.
    var parentOf = new Dictionary<TreeNode, TreeNode>();
    foreach (var node in nodes)
      foreach (var child in node.Children)
        parentOf[child] = node;

    foreach (var node in nodes) {
      if (!node.IsDirectory) continue;
      var ioff = InodeOffsetForSlot(node.Slot);
      // A directory's link count is 2 (self "." plus parent's reference) plus
      // one for each child subdirectory (whose ".." points back here).
      var childDirCount = node.Children.Count(c => c.IsDirectory);
      if (node.BlockFormDirectory) {
        // Out-of-line dir2: the entry list lives in directory data blocks
        // (di_format = extents), not the inode literal area.
        WriteInodeCoreV3(image, ioff, node.Ino, mode: 0x41ED /* S_IFDIR|0755 */,
          format: 2 /* extents */, nlink: (uint)(2 + childDirCount));
        WriteBlockFormDirectory(image, ioff, node, parentOf);
      } else {
        WriteInodeCoreV3(image, ioff, node.Ino, mode: 0x41ED /* S_IFDIR|0755 */,
          format: 1 /* local short-form */, nlink: (uint)(2 + childDirCount));
        WriteShortFormDirectory(image, ioff, node, parentOf);
      }
    }

    // ── Realtime bitmap inode (slot 1) — empty S_IFREG|0 (mkfs convention) ──
    var rbmOff = InodeOffsetForSlot(1);
    WriteInodeCoreV3(image, rbmOff, RootIno + 1, mode: 0x8000 /* S_IFREG, no perm bits */,
      format: 2 /* extents, 0 extents */, nlink: 1);

    // ── Realtime summary inode (slot 2) ──
    var rsumOff = InodeOffsetForSlot(2);
    WriteInodeCoreV3(image, rsumOff, RootIno + 2, mode: 0x8000, format: 2, nlink: 1);

    // ── Regular-file inodes ──
    foreach (var node in nodes) {
      if (node.IsDirectory) continue;
      var ioff = InodeOffsetForSlot(node.Slot);
      WriteInodeCoreV3(image, ioff, node.Ino,
        mode: 0x81A4 /* S_IFREG|0644 */, format: 2, nlink: 1);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)node.Data.Length);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 64), (ulong)node.BlockCount);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), 1);

      var startBlock = (ulong)node.DataBlock;
      var blockCount = (ulong)node.BlockCount;
      var hi = (startBlock >> 43) & 0x1FF;
      var lo = (startBlock << 21) | (blockCount & 0x1FFFFF);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 176), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 184), lo);

      node.Data.CopyTo(image, node.DataBlock * BlockSize);
    }

    // ── Format the log at (logStartAgBno, LogBlocks) ──
    // xfs_repair checks whether the log is "ahead" of metadata. Without a
    // properly formatted log it emits "Maximum metadata LSN (0:0) is ahead of
    // log (0:0). Would format log to cycle 3." and exits 1 under -n.
    // We stamp sector 0 with a clean xlog_rec_header (cycle=1, XLOG_INIT_CYCLE)
    // and stamp the first 4 bytes of every subsequent 512-byte sector with the
    // cycle number. The kernel log-recovery code then reports l_curr_cycle=1.
    FormatLog(image, logStartAgBno * BlockSize, LogBlocks * BlockSize, cycle: 1);

    // ── Initialize all "free" slots with valid v3 inode headers ──
    // mkfs.xfs writes valid IN-magic inodes (mode=0, nlink=0) for every slot
    // in the chunk, not just allocated ones. xfs_repair walks the chunk and
    // validates each slot; zero-filled slots are flagged as corrupt.
    for (var slot = usedInodeSlots; slot < totalInodeSlots; slot++) {
      var ioff = InodeOffsetForSlot(slot);
      WriteInodeCoreV3(image, ioff, RootIno + (ulong)slot, mode: 0,
        format: 0 /* XFS_DINODE_FMT_DEV */, nlink: 0, aformat: 0);
    }

    // ── CRC backfill (last — after all data is written) ──
    for (var ag = 0; ag < AgCount; ag++) {
      var agOff = ag * AgBlocks * BlockSize;
      BackfillCrc(image.AsSpan(agOff, SectorSize), SbCrcOffset);
      BackfillCrc(image.AsSpan(agOff + AgfSector * SectorSize, SectorSize), AgfCrcOffset);
      BackfillCrc(image.AsSpan(agOff + AgiSector * SectorSize, SectorSize), AgiCrcOffset);
      BackfillCrc(image.AsSpan(agOff + AgflSector * SectorSize, SectorSize), AgflCrcOffset);
      BackfillCrc(image.AsSpan(agOff + BnobtBlock * BlockSize, BlockSize), BtreeCrcOffset);
      BackfillCrc(image.AsSpan(agOff + CntbtBlock * BlockSize, BlockSize), BtreeCrcOffset);
      BackfillCrc(image.AsSpan(agOff + InobtBlock * BlockSize, BlockSize), BtreeCrcOffset);
    }
    // Every inode slot across all allocated chunks needs a valid CRC.
    for (var slot = 0; slot < totalInodeSlots; slot++) {
      var ioff = InodeOffsetForSlot(slot);
      BackfillCrc(image.AsSpan(ioff, InodeSize), DiCrcOffset);
    }

    output.Write(image);
  }

  /// <summary>
  /// Bitmask of free inodes in a 64-slot chunk. Bit i=1 means slot i is free.
  /// Slots 0..<paramref name="usedSlots"/>-1 are in use, remainder is free.
  /// </summary>
  private static ulong ComputeFreeMask(int usedSlots) {
    if (usedSlots >= InodesPerChunk) return 0UL;
    if (usedSlots <= 0) return ulong.MaxValue;
    return ulong.MaxValue << usedSlots;
  }

  /// <summary>
  /// Byte offset of an inode <paramref name="slot"/> within the contiguous run
  /// of inode chunks that begins at <see cref="InodeChunkBlock"/>. Chunks are
  /// laid out back-to-back, so the slot index maps linearly onto block and
  /// in-block position — matching the agino encoding the reader uses.
  /// </summary>
  private static int InodeOffsetForSlot(int slot)
    => (InodeChunkBlock + slot / InodesPerBlock) * BlockSize + (slot % InodesPerBlock) * InodeSize;

  /// <summary>
  /// The number of bytes available for an inline short-form directory in the
  /// inode literal area (everything past the v3 dinode core at offset 176).
  /// </summary>
  private const int ShortFormForkCapacity = InodeSize - 176; // 80 bytes

  /// <summary>
  /// True when <paramref name="dir"/>'s children fit in the inode literal area
  /// as a short-form directory. Each entry needs namelen(1)+offset(2)+name+
  /// ftype(1)+ino(4) bytes on top of the 6-byte header.
  /// </summary>
  private static bool FitsShortForm(TreeNode dir) {
    var total = 6; // xfs_dir2_sf_hdr (count, i8count, 4-byte parent)
    foreach (var child in dir.Children) {
      var nameLen = Math.Min(Encoding.UTF8.GetByteCount(child.Name), 250);
      total += 3 + nameLen + 1 + 4;
      if (total > ShortFormForkCapacity) return false;
    }
    return total <= ShortFormForkCapacity;
  }

  // dir2 data-block header size we emit (xfs_dir3_blk_hdr, v5). The reader skips
  // exactly this many bytes before the first packed data entry.
  private const int Dir2DataHeaderSize = 48;
  private const uint Dir2BlockMagicV5 = 0x58444233; // "XDB3"

  /// <summary>The on-disk size of one dir2 data entry (with FTYPE), 8-aligned.</summary>
  private static int Dir2EntrySize(int nameLen) => (8 + 1 + nameLen + 1 + 2 + 7) & ~7;

  /// <summary>
  /// Measures how many 4 KiB directory data blocks a block-form directory needs
  /// and the resulting di_size. Each block carries its own header plus "." and
  /// ".." in the first block; an entry never straddles a block boundary.
  /// </summary>
  private static (int Blocks, long ByteSize) MeasureBlockFormDirectory(TreeNode dir) {
    var blocks = 1;
    var used = Dir2DataHeaderSize + Dir2EntrySize(1) + Dir2EntrySize(2); // "." + ".."
    foreach (var child in dir.Children) {
      var nameLen = Math.Min(Encoding.UTF8.GetByteCount(child.Name), 250);
      var entry = Dir2EntrySize(nameLen);
      if (used + entry > BlockSize) {
        blocks++;
        used = Dir2DataHeaderSize;
      }
      used += entry;
    }
    return (blocks, (long)blocks * BlockSize);
  }

  /// <summary>
  /// Writes the inline short-form directory (xfs_dir2_sf) for a directory inode
  /// at <paramref name="inodeOff"/>. The fork lives at offset 176 in a v3
  /// dinode. "." and ".." are implied (self / parent) — only real children are
  /// listed. Each entry carries the FTYPE byte (DT_DIR for subdirectories,
  /// DT_REG for files) since the superblock advertises the FTYPE feature.
  /// </summary>
  private static void WriteShortFormDirectory(byte[] image, int inodeOff, TreeNode dir,
      IReadOnlyDictionary<TreeNode, TreeNode> parentOf) {
    var dirOff = inodeOff + 176;
    var parentIno = parentOf.TryGetValue(dir, out var parent) ? parent.Ino : dir.Ino;

    // xfs_dir2_sf_hdr: count(1), i8count(1), parent[4] (4 B when i8count==0).
    image[dirOff] = (byte)dir.Children.Count;
    image[dirOff + 1] = 0; // i8count — all inode numbers fit in 32 bits
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)parentIno);

    var entryPos = dirOff + 6;
    // Start entry offsets at 0x60 — standard base past "." (0x30) and ".." (0x40).
    ushort nextOffset = 0x60;
    foreach (var child in dir.Children) {
      var nameBytes = Encoding.UTF8.GetBytes(child.Name);
      var nameLen = Math.Min(nameBytes.Length, 250);
      // xfs_dir2_sf_entry with FTYPE: namelen(1), offset[2], name[namelen],
      // ftype(1), ino[4].
      image[entryPos] = (byte)nameLen;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryPos + 1), nextOffset);
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryPos + 3));
      image[entryPos + 3 + nameLen] = child.IsDirectory ? (byte)2 : (byte)1; // DT_DIR / DT_REG
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entryPos + 4 + nameLen), (uint)child.Ino);
      entryPos += 3 + nameLen + 1 + 4;
      // Next sf_offset mimics the dir2 data-block layout (ino(8)+namelen(1)+
      // name+ftype(1)+tag(2) = nameLen + 12, padded to 8), NOT the shortform
      // on-disk entry size — xfs_repair rejects out-of-order offsets otherwise.
      nextOffset = (ushort)(nextOffset + ((nameLen + 12 + 7) & ~7));
    }

    var dirSize = entryPos - dirOff;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 56), (ulong)dirSize); // di_size
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 64), 0);               // di_nblocks
  }

  /// <summary>
  /// Writes a block-form directory: the entry list lives in one or more
  /// contiguous 4 KiB directory data blocks (xfs_dir2 "XDB3" data blocks) rather
  /// than the inode literal area. The inode is in extents format with a single
  /// extent covering every directory data block. Each data block starts with a
  /// 48-byte v5 data-block header followed by packed
  /// <c>xfs_dir2_data_entry</c> records: inumber(8), namelen(1), name, ftype(1),
  /// tag(2), padded to an 8-byte boundary. "." and ".." occupy the first block;
  /// an entry never straddles a block boundary, so trailing slack in a block is
  /// left zero (the reader treats a zero inumber as a free slot).
  /// </summary>
  private static void WriteBlockFormDirectory(byte[] image, int inodeOff, TreeNode dir,
      IReadOnlyDictionary<TreeNode, TreeNode> parentOf) {
    var parentIno = parentOf.TryGetValue(dir, out var parent) ? parent.Ino : dir.Ino;
    var firstBlock = dir.DataBlock;
    var blockCount = dir.BlockCount;

    // Initialise every data block's header up front.
    for (var b = 0; b < blockCount; b++) {
      var blockOff = (firstBlock + b) * BlockSize;
      WriteDir2DataBlockHeader(image, blockOff, firstBlock + b);
    }

    var curBlock = 0;
    var pos = firstBlock * BlockSize + Dir2DataHeaderSize;

    // "." → self, ".." → parent. Both carry DT_DIR.
    pos = WriteDir2DataEntry(image, ref curBlock, firstBlock, pos, dir.Ino, ".", isDir: true);
    pos = WriteDir2DataEntry(image, ref curBlock, firstBlock, pos, parentIno, "..", isDir: true);

    foreach (var child in dir.Children)
      pos = WriteDir2DataEntry(image, ref curBlock, firstBlock, pos, child.Ino, child.Name, child.IsDirectory);

    _ = pos;

    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 56), (ulong)dir.DirByteSize); // di_size
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 64), (ulong)blockCount);       // di_nblocks
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(inodeOff + 76), 1);                       // di_nextents

    // Single extent: logical dir block 0, physical firstBlock, blockCount blocks.
    var startBlock = (ulong)firstBlock;
    var hi = (startBlock >> 43) & 0x1FF;
    var lo = (startBlock << 21) | ((ulong)blockCount & 0x1FFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 176), hi);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOff + 184), lo);
  }

  /// <summary>
  /// Writes one dir2 data entry, advancing to the next data block first if it
  /// would not fit in the current one. Returns the position past the entry.
  /// </summary>
  private static int WriteDir2DataEntry(byte[] image, ref int curBlock, int firstBlock,
      int pos, ulong ino, string name, bool isDir) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 250);
    var entLen = Dir2EntrySize(nameLen);

    var blockStart = (firstBlock + curBlock) * BlockSize;
    if (pos - blockStart + entLen > BlockSize) {
      curBlock++;
      blockStart = (firstBlock + curBlock) * BlockSize;
      pos = blockStart + Dir2DataHeaderSize;
    }

    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(pos), ino);
    image[pos + 8] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(pos + 9));
    image[pos + 9 + nameLen] = isDir ? (byte)2 : (byte)1; // DT_DIR / DT_REG
    // tag = byte offset of this entry within its data block.
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 10 + nameLen), (ushort)(pos - blockStart));
    return pos + entLen;
  }

  /// <summary>Writes the 48-byte v5 dir2 data-block header (XDB3 magic).</summary>
  private static void WriteDir2DataBlockHeader(byte[] image, int blockOff, int fsBlock) {
    var h = image.AsSpan(blockOff);
    BinaryPrimitives.WriteUInt32BigEndian(h[0..], Dir2BlockMagicV5); // magic
    BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 0);            // crc (left zero — v4-style dir, see class doc)
    BinaryPrimitives.WriteUInt64BigEndian(h[8..], (ulong)fsBlock * (BlockSize / SectorSize)); // blkno (sector)
    BinaryPrimitives.WriteUInt64BigEndian(h[16..], 0);             // lsn
    UuidBytes.CopyTo(h[24..]);                                     // uuid
    BinaryPrimitives.WriteUInt64BigEndian(h[40..], 0);            // owner (set to 0; reader ignores)
  }

  private static void WriteSuperblock(Span<byte> sb, ulong totalBlocks, ulong logStart,
      int logBlocks, ulong icount, ulong ifree, ulong fdblocks) {
    BinaryPrimitives.WriteUInt32BigEndian(sb[0..], XfsMagic);
    BinaryPrimitives.WriteUInt32BigEndian(sb[4..], BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(sb[8..], totalBlocks);        // sb_dblocks
    BinaryPrimitives.WriteUInt64BigEndian(sb[16..], 0);                 // sb_rblocks
    BinaryPrimitives.WriteUInt64BigEndian(sb[24..], 0);                 // sb_rextents

    UuidBytes.CopyTo(sb[32..]);

    BinaryPrimitives.WriteUInt64BigEndian(sb[48..], logStart);
    BinaryPrimitives.WriteUInt64BigEndian(sb[56..], RootIno);
    // xfs_repair expects rbmino/rsumino to be rootino+1 and rootino+2 even when
    // no realtime subvolume exists. The inodes themselves must exist on disk
    // (inode slots 1 and 2 of the root chunk). They are marked S_IFREG|0 so
    // xfs_repair sees them as unused-but-present.
    BinaryPrimitives.WriteUInt64BigEndian(sb[64..], RootIno + 1);       // sb_rbmino
    BinaryPrimitives.WriteUInt64BigEndian(sb[72..], RootIno + 2);       // sb_rsumino
    BinaryPrimitives.WriteUInt32BigEndian(sb[80..], 1);                 // sb_rextsize
    BinaryPrimitives.WriteUInt32BigEndian(sb[84..], AgBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(sb[88..], AgCount);
    BinaryPrimitives.WriteUInt32BigEndian(sb[92..], 0);                 // sb_rbmblocks
    BinaryPrimitives.WriteUInt32BigEndian(sb[96..], (uint)logBlocks);
    BinaryPrimitives.WriteUInt16BigEndian(sb[100..], XfsSbVersion5);
    BinaryPrimitives.WriteUInt16BigEndian(sb[102..], SectorSize);
    BinaryPrimitives.WriteUInt16BigEndian(sb[104..], InodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(sb[106..], InodesPerBlock);
    // sb_fname[12] at 108 = zero

    sb[120] = BlockLog;
    sb[121] = SectorLog;
    sb[122] = InodeLog;
    sb[123] = InoPbLog;
    sb[124] = AgBlkLog;
    sb[125] = 0;          // sb_rextslog
    sb[126] = 0;          // sb_inprogress
    sb[127] = 25;         // sb_imax_pct

    BinaryPrimitives.WriteUInt64BigEndian(sb[128..], icount);
    BinaryPrimitives.WriteUInt64BigEndian(sb[136..], ifree);
    BinaryPrimitives.WriteUInt64BigEndian(sb[144..], fdblocks);
    BinaryPrimitives.WriteUInt64BigEndian(sb[152..], 0);                // sb_frextents
    BinaryPrimitives.WriteUInt64BigEndian(sb[160..], 0);                // sb_uquotino
    BinaryPrimitives.WriteUInt64BigEndian(sb[168..], 0);                // sb_gquotino
    BinaryPrimitives.WriteUInt16BigEndian(sb[176..], 0);                // sb_qflags
    sb[178] = 0;                                                        // sb_flags
    sb[179] = 0;                                                        // sb_shared_vn
    // sb_inoalignmt = XFS_INODE_BIG_CLUSTER_SIZE / blocksize.
    // For v5 with 4 KiB blocks: 8192 / 4096 = 2 (kernel uses fixed 8192-byte
    // inode cluster on v5 regardless of inode size). A 64-inode chunk spans
    // (64 × inodesize / cluster_size) clusters but inoalignmt is still 2.
    BinaryPrimitives.WriteUInt32BigEndian(sb[180..], 2);                // sb_inoalignmt
    BinaryPrimitives.WriteUInt32BigEndian(sb[184..], 0);                // sb_unit
    BinaryPrimitives.WriteUInt32BigEndian(sb[188..], 0);                // sb_width
    sb[192] = 0;                                                        // sb_dirblklog
    sb[193] = 0;                                                        // sb_logsectlog
    BinaryPrimitives.WriteUInt16BigEndian(sb[194..], 0);                // sb_logsectsize
    BinaryPrimitives.WriteUInt32BigEndian(sb[196..], 1);                // sb_logsunit
    // sb_features2/bad_features2: LAZYSBCOUNTBIT + ATTR2BIT + PROJID32BIT + CRCBIT.
    BinaryPrimitives.WriteUInt32BigEndian(sb[200..], 0x18A);
    BinaryPrimitives.WriteUInt32BigEndian(sb[204..], 0x18A);
    BinaryPrimitives.WriteUInt32BigEndian(sb[208..], 0);                // sb_features_compat
    BinaryPrimitives.WriteUInt32BigEndian(sb[212..], 0);                // sb_features_ro_compat (no finobt/rmap/reflink/inobtcnt)
    // sb_features_incompat: FTYPE(0x1) is required for v5 in modern kernels
    // (dirents carry file-type byte). Leave SPINODES/META_UUID off.
    BinaryPrimitives.WriteUInt32BigEndian(sb[216..], 0x1);              // sb_features_incompat = FTYPE
    BinaryPrimitives.WriteUInt32BigEndian(sb[220..], 0);                // sb_features_log_incompat
    // sb_crc at 224 — computed later.
    BinaryPrimitives.WriteUInt32BigEndian(sb[228..], 0);                // sb_spino_align
    BinaryPrimitives.WriteUInt64BigEndian(sb[232..], 0);                // sb_pquotino
    BinaryPrimitives.WriteUInt64BigEndian(sb[240..], 0);                // sb_lsn
    // sb_meta_uuid: only carries a value when INCOMPAT_META_UUID is set.
    // Without the feature bit, xfs_repair expects this region zeroed.
    sb.Slice(248, 16).Clear();                                          // sb_meta_uuid
    // Remaining fields (sb_rrmapino @ 264) are left zero — the SB struct
    // continues to 264 total bytes but xfs_repair zero-pads the rest to 512.
  }

  private static void WriteAgf(Span<byte> agf, uint agNumber, uint agBlocks,
      uint bnobtRoot, uint cntbtRoot, uint freeBlocks, uint longest) {
    BinaryPrimitives.WriteUInt32BigEndian(agf[0..], AgfMagic);
    BinaryPrimitives.WriteUInt32BigEndian(agf[4..], 1);                 // agf_versionnum
    BinaryPrimitives.WriteUInt32BigEndian(agf[8..], agNumber);          // agf_seqno
    BinaryPrimitives.WriteUInt32BigEndian(agf[12..], agBlocks);         // agf_length
    // agf_roots[3] at 16
    BinaryPrimitives.WriteUInt32BigEndian(agf[16..], bnobtRoot);        // BNO
    BinaryPrimitives.WriteUInt32BigEndian(agf[20..], cntbtRoot);        // CNT
    BinaryPrimitives.WriteUInt32BigEndian(agf[24..], 0);                // RMAP (unused)
    // agf_levels[3] at 28
    BinaryPrimitives.WriteUInt32BigEndian(agf[28..], 1);                // BNO level = 1 (single leaf)
    BinaryPrimitives.WriteUInt32BigEndian(agf[32..], 1);                // CNT level = 1
    BinaryPrimitives.WriteUInt32BigEndian(agf[36..], 0);                // RMAP level = 0
    BinaryPrimitives.WriteUInt32BigEndian(agf[40..], 0);                // agf_flfirst
    // agf_fllast = AGFL_SIZE-1, with agf_flcount=0 meaning empty.
    // For a 512-byte AGFL with 36-byte header, slots = (512-36)/4 = 119.
    BinaryPrimitives.WriteUInt32BigEndian(agf[44..], 118);              // agf_fllast (last valid index)
    BinaryPrimitives.WriteUInt32BigEndian(agf[48..], 0);                // agf_flcount
    BinaryPrimitives.WriteUInt32BigEndian(agf[52..], freeBlocks);       // agf_freeblks
    BinaryPrimitives.WriteUInt32BigEndian(agf[56..], longest);          // agf_longest
    BinaryPrimitives.WriteUInt32BigEndian(agf[60..], 0);                // agf_btreeblks
    UuidBytes.CopyTo(agf[64..]);                                        // agf_uuid
    BinaryPrimitives.WriteUInt32BigEndian(agf[80..], 0);                // agf_rmap_blocks
    BinaryPrimitives.WriteUInt32BigEndian(agf[84..], 0);                // agf_refcount_blocks
    BinaryPrimitives.WriteUInt32BigEndian(agf[88..], 0);                // agf_refcount_root
    BinaryPrimitives.WriteUInt32BigEndian(agf[92..], 0);                // agf_refcount_level
    // agf_spare64[14] at 96..207 = zero
    BinaryPrimitives.WriteUInt64BigEndian(agf[208..], 0);               // agf_lsn
    // agf_crc at 216 — computed later.
    BinaryPrimitives.WriteUInt32BigEndian(agf[220..], 0);               // agf_spare2
  }

  private static void WriteAgi(Span<byte> agi, uint agNumber, uint agBlocks,
      uint inodeCount, uint freeInodes, uint inobtRoot, uint inobtLevel, uint newIno) {
    BinaryPrimitives.WriteUInt32BigEndian(agi[0..], AgiMagic);
    BinaryPrimitives.WriteUInt32BigEndian(agi[4..], 1);                 // agi_versionnum
    BinaryPrimitives.WriteUInt32BigEndian(agi[8..], agNumber);          // agi_seqno
    BinaryPrimitives.WriteUInt32BigEndian(agi[12..], agBlocks);         // agi_length
    BinaryPrimitives.WriteUInt32BigEndian(agi[16..], inodeCount);       // agi_count
    BinaryPrimitives.WriteUInt32BigEndian(agi[20..], inobtRoot);        // agi_root
    BinaryPrimitives.WriteUInt32BigEndian(agi[24..], inobtLevel);       // agi_level
    BinaryPrimitives.WriteUInt32BigEndian(agi[28..], freeInodes);       // agi_freecount
    BinaryPrimitives.WriteUInt32BigEndian(agi[32..], newIno);           // agi_newino
    BinaryPrimitives.WriteUInt32BigEndian(agi[36..], 0xFFFFFFFFu);      // agi_dirino (unused)
    // agi_unlinked[64] at 40..295 — fill with 0xFFFFFFFF.
    for (var i = 0; i < 64; i++)
      BinaryPrimitives.WriteUInt32BigEndian(agi[(40 + i * 4)..], 0xFFFFFFFFu);
    UuidBytes.CopyTo(agi[296..]);                                       // agi_uuid
    // agi_crc at 312 — computed later.
    BinaryPrimitives.WriteUInt32BigEndian(agi[316..], 0);               // agi_pad32
    BinaryPrimitives.WriteUInt64BigEndian(agi[320..], 0);               // agi_lsn
    BinaryPrimitives.WriteUInt32BigEndian(agi[328..], 0);               // agi_free_root (finobt)
    BinaryPrimitives.WriteUInt32BigEndian(agi[332..], 0);               // agi_free_level
  }

  private static void WriteAgfl(Span<byte> agfl, uint agNumber) {
    BinaryPrimitives.WriteUInt32BigEndian(agfl[0..], AgflMagic);
    BinaryPrimitives.WriteUInt32BigEndian(agfl[4..], agNumber);         // agfl_seqno
    UuidBytes.CopyTo(agfl[8..]);                                        // agfl_uuid
    BinaryPrimitives.WriteUInt64BigEndian(agfl[24..], 0);               // agfl_lsn
    // agfl_crc at 32 — computed later.
    // agfl_bno[] starts at 36. For empty free-list, fill with 0xFFFFFFFF.
    for (var off = 36; off + 4 <= SectorSize; off += 4)
      BinaryPrimitives.WriteUInt32BigEndian(agfl[off..], 0xFFFFFFFFu);
  }

  /// <summary>Writes a free-space-by-block B+tree leaf with 0 or 1 records.</summary>
  private static void WriteBnobt(Span<byte> block, uint agNumber, ulong selfSector,
      uint freeStart, uint freeLen) {
    var hasRecord = freeLen > 0;
    WriteBtreeSblockHeader(block, BnobtV5Magic, agNumber, selfSector,
      level: 0, numrecs: hasRecord ? (ushort)1 : (ushort)0);
    if (hasRecord) {
      // bnobt leaf record: 8 bytes = __be32 startblock, __be32 blockcount.
      BinaryPrimitives.WriteUInt32BigEndian(block[BtreeRecOffset..], freeStart);
      BinaryPrimitives.WriteUInt32BigEndian(block[(BtreeRecOffset + 4)..], freeLen);
    }
  }

  private static void WriteCntbt(Span<byte> block, uint agNumber, ulong selfSector,
      uint freeStart, uint freeLen) {
    var hasRecord = freeLen > 0;
    WriteBtreeSblockHeader(block, CntbtV5Magic, agNumber, selfSector,
      level: 0, numrecs: hasRecord ? (ushort)1 : (ushort)0);
    if (hasRecord) {
      // cntbt leaf record: same layout as bnobt; keying is by (count, then start).
      BinaryPrimitives.WriteUInt32BigEndian(block[BtreeRecOffset..], freeStart);
      BinaryPrimitives.WriteUInt32BigEndian(block[(BtreeRecOffset + 4)..], freeLen);
    }
  }

  /// <summary>
  /// Writes an inobt leaf with one record per allocated 64-inode chunk
  /// (<paramref name="chunkCount"/> records). The first chunk starts at
  /// <paramref name="startAgino"/>; each record's free-slot bitmask is derived
  /// from how many of the <paramref name="usedSlots"/> inodes fall in it.
  /// </summary>
  private static void WriteInobt(Span<byte> block, uint agNumber, ulong selfSector,
      int chunkCount, uint startAgino, int usedSlots) {
    WriteBtreeSblockHeader(block, InobtV5Magic, agNumber, selfSector,
      level: 0, numrecs: (ushort)chunkCount);
    // One non-sparse inobt record per 64-inode chunk (16 bytes each):
    //   __be32 ir_startino;
    //   __be32 ir_freecount;  // count of free inodes in chunk
    //   __be64 ir_free;       // bitmask — bit=1 means slot is free
    for (var c = 0; c < chunkCount; c++) {
      var chunkUsed = Math.Clamp(usedSlots - c * InodesPerChunk, 0, InodesPerChunk);
      var freeMask = ComputeFreeMask(chunkUsed);
      var freeCount = (uint)System.Numerics.BitOperations.PopCount(freeMask);
      var rec = BtreeRecOffset + c * 16;
      BinaryPrimitives.WriteUInt32BigEndian(block[rec..], startAgino + (uint)(c * InodesPerChunk));
      BinaryPrimitives.WriteUInt32BigEndian(block[(rec + 4)..], freeCount);
      BinaryPrimitives.WriteUInt64BigEndian(block[(rec + 8)..], freeMask);
    }
  }

  /// <summary>
  /// Writes a 56-byte xfs_btree_sblock v5 (CRC-enabled) header. Records start at offset 56.
  /// <paramref name="selfSector"/> is the 512-byte-sector number of this block
  /// (= fsblock × 8 for 4 KiB blocks).
  /// </summary>
  private static void WriteBtreeSblockHeader(Span<byte> block, uint magic, uint agNumber,
      ulong selfSector, ushort level, ushort numrecs) {
    BinaryPrimitives.WriteUInt32BigEndian(block[0..], magic);
    BinaryPrimitives.WriteUInt16BigEndian(block[4..], level);
    BinaryPrimitives.WriteUInt16BigEndian(block[6..], numrecs);
    BinaryPrimitives.WriteUInt32BigEndian(block[8..], 0xFFFFFFFFu);     // bb_leftsib = NULLAGBLOCK
    BinaryPrimitives.WriteUInt32BigEndian(block[12..], 0xFFFFFFFFu);    // bb_rightsib
    BinaryPrimitives.WriteUInt64BigEndian(block[16..], selfSector);     // bb_blkno (disk sector)
    BinaryPrimitives.WriteUInt64BigEndian(block[24..], 0);              // bb_lsn
    UuidBytes.CopyTo(block[32..]);                                      // bb_uuid
    BinaryPrimitives.WriteUInt32BigEndian(block[48..], agNumber);       // bb_owner
    // bb_crc at 52 — computed later. Records start at offset 56.
  }

  private static void WriteInodeCoreV3(byte[] image, int ioff, ulong inodeNumber,
      ushort mode, byte format, uint nlink, byte aformat = 2) {
    var di = image.AsSpan(ioff);
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], mode);
    di[4] = 3;                                                          // di_version (v3 = CRC)
    di[5] = format;                                                     // di_format
    BinaryPrimitives.WriteUInt16BigEndian(di[6..], 0);                  // di_onlink (unused in v3)
    BinaryPrimitives.WriteUInt32BigEndian(di[8..], 0);                  // di_uid
    BinaryPrimitives.WriteUInt32BigEndian(di[12..], 0);                 // di_gid
    BinaryPrimitives.WriteUInt32BigEndian(di[16..], nlink);
    BinaryPrimitives.WriteUInt16BigEndian(di[20..], 0);                 // di_projid_lo
    BinaryPrimitives.WriteUInt16BigEndian(di[22..], 0);                 // di_projid_hi
    // di_pad[6] at 24..29 zero
    BinaryPrimitives.WriteUInt16BigEndian(di[30..], 0);                 // di_flushiter
    // di_atime/mtime/ctime at 32/40/48 left zero
    BinaryPrimitives.WriteUInt64BigEndian(di[56..], 0);                 // di_size (caller overwrites)
    BinaryPrimitives.WriteUInt64BigEndian(di[64..], 0);                 // di_nblocks (caller overwrites)
    BinaryPrimitives.WriteUInt32BigEndian(di[72..], 0);                 // di_extsize
    BinaryPrimitives.WriteUInt32BigEndian(di[76..], 0);                 // di_nextents (caller may overwrite)
    BinaryPrimitives.WriteUInt16BigEndian(di[80..], 0);                 // di_anextents
    di[82] = 0;                                                         // di_forkoff
    di[83] = aformat;                                                   // di_aformat
    BinaryPrimitives.WriteUInt32BigEndian(di[84..], 0);                 // di_dmevmask
    BinaryPrimitives.WriteUInt16BigEndian(di[88..], 0);                 // di_dmstate
    BinaryPrimitives.WriteUInt16BigEndian(di[90..], 0);                 // di_flags
    BinaryPrimitives.WriteUInt32BigEndian(di[92..], 0);                 // di_gen

    // v3 tail (96..175).
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);       // di_next_unlinked = NULLAGINO
    // di_crc at 100 (little-endian) — backfilled later.
    BinaryPrimitives.WriteUInt64BigEndian(di[104..], 0);                // di_changecount
    BinaryPrimitives.WriteUInt64BigEndian(di[112..], 0);                // di_lsn
    BinaryPrimitives.WriteUInt64BigEndian(di[120..], 0);                // di_flags2
    BinaryPrimitives.WriteUInt32BigEndian(di[128..], 0);                // di_cowextsize
    // di_pad2[12] at 132..143 zero
    // di_crtime at 144..151 zero
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], inodeNumber);      // di_ino
    UuidBytes.CopyTo(di[160..]);                                        // di_uuid (first 16 B)
  }

  /// <summary>
  /// Formats the log region as if <c>libxfs_log_clear</c> had been invoked with
  /// <c>XLOG_INIT_CYCLE=1</c>. Layout:
  /// <list type="bullet">
  ///   <item>sector 0: <c>xlog_rec_header</c> (h_magic=0xFEEDBABE, h_cycle=1,
  ///         h_num_logops=1, h_len=512, h_lsn=0x1_0000_0000)</item>
  ///   <item>sector 1: one packed op-header + <c>XLOG_UNMOUNT_TYPE</c> (0x556e)
  ///         magic; first 4 bytes overwritten with cycle=1 (stored in
  ///         h_cycle_data[0] so the kernel can recover them)</item>
  ///   <item>sectors 2..end: all zero (cycle 0) — kernel treats this as
  ///         "not yet written this cycle" and sets l_curr_cycle=1</item>
  /// </list>
  /// This makes <c>xlog_find_tail</c> succeed with
  /// <c>log-&gt;l_curr_cycle = 1</c> and a clean unmount record at block 1,
  /// which satisfies the <c>format_log_max_lsn</c> early-return check in
  /// xfs_repair (max_cycle=0 &lt; l_curr_cycle=1 ⇒ silent return).
  /// </summary>
  private static void FormatLog(byte[] image, int logOffsetBytes, int logSizeBytes, uint cycle) {
    const uint XlogMagic = 0xFEEDBABE;
    const uint XlogUnmountType = 0x556E;          // "Un" — XLOG_UNMOUNT_TYPE
    const uint XlogUnmountTransFlag = 0x10;       // XLOG_UNMOUNT_TRANS
    const byte XfsLog = 0xAA;                     // XFS_LOG client ID
    const int XlogBigRecordBsize = 32 * 1024;
    var log = image.AsSpan(logOffsetBytes, logSizeBytes);
    log.Clear();

    var lsn = (ulong)cycle << 32;   // block=0

    // ── Sector 0: xlog_rec_header ──
    BinaryPrimitives.WriteUInt32BigEndian(log[0..], XlogMagic);
    BinaryPrimitives.WriteUInt32BigEndian(log[4..], cycle);             // h_cycle
    BinaryPrimitives.WriteUInt32BigEndian(log[8..], 2);                 // h_version = 2 (LOGV2)
    BinaryPrimitives.WriteUInt32BigEndian(log[12..], 512);              // h_len = 1 BBSIZE
    // h_tail_lsn points at the block AFTER the unmount record (= the next
    // block that will be written), indicating "tail has caught up with head"
    // which xfs_repair interprets as a cleanly unmounted log.
    var tailLsn = ((ulong)cycle << 32) | 2;
    BinaryPrimitives.WriteUInt64BigEndian(log[16..], lsn);              // h_lsn = (cycle, 0)
    BinaryPrimitives.WriteUInt64BigEndian(log[24..], tailLsn);          // h_tail_lsn = (cycle, 2)
    // h_crc at 32 (LE) left zero — kernel/xfs_repair tolerate zero CRC on
    // a freshly-initialized clean log.
    BinaryPrimitives.WriteUInt32BigEndian(log[36..], 0xFFFFFFFFu);      // h_prev_block = -1
    BinaryPrimitives.WriteUInt32BigEndian(log[40..], 1);                // h_num_logops = 1
    // h_cycle_data[64] at 44..299: save first 4 bytes of sector 1's
    // unmount record so the kernel can recover them after the cycle stamp.
    // After the cycle-stamp below we set h_cycle_data[0] = original value.
    BinaryPrimitives.WriteUInt32BigEndian(log[300..], 1);               // h_fmt = XLOG_FMT_LINUX_LE
    UuidBytes.CopyTo(log[304..]);                                       // h_fs_uuid
    BinaryPrimitives.WriteUInt32BigEndian(log[320..], XlogBigRecordBsize); // h_size

    // ── Sector 1: xlog_op_header + XLOG_UNMOUNT_TYPE magic ──
    var unmountSector = log[SectorSize..];
    // xlog_op_header layout:
    //   __be32 oh_tid
    //   __be32 oh_len
    //   __u8   oh_clientid
    //   __u8   oh_flags
    //   __u16  oh_res2
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[0..], 0xB0C0D0D0u); // oh_tid (libxfs sentinel)
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[4..], 8);           // oh_len
    unmountSector[8] = XfsLog;                                              // oh_clientid
    unmountSector[9] = (byte)XlogUnmountTransFlag;                          // oh_flags
    BinaryPrimitives.WriteUInt16BigEndian(unmountSector[10..], 0);          // oh_res2
    // magic payload at offset 12: { uint16 magic=0x556e, uint16 pad1=0, uint32 pad2=0 }
    BinaryPrimitives.WriteUInt16LittleEndian(unmountSector[12..], (ushort)XlogUnmountType);

    // Save sector 1's first 4 bytes (oh_tid MSB) into h_cycle_data[0] then
    // stamp the cycle in its place. The kernel's xlog_unpack_data restores
    // these bytes after verifying the cycle.
    var savedFirst4 = BinaryPrimitives.ReadUInt32BigEndian(unmountSector);
    BinaryPrimitives.WriteUInt32BigEndian(log[44..], savedFirst4);      // h_cycle_data[0]
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[0..], cycle);   // cycle stamp

    // Sectors 2..end remain zero (cycle 0) — kernel interprets as unwritten.
  }

  /// <summary>
  /// Backfills the CRC-32C of <paramref name="block"/> into the 4-byte field at
  /// <paramref name="crcFieldOffset"/>. The field is zeroed during hashing and
  /// written little-endian afterwards (matches XFS v5 for SB/AGF/AGI/AGFL/btree/inode).
  /// </summary>
  internal static void BackfillCrc(Span<byte> block, int crcFieldOffset) {
    block[crcFieldOffset] = 0;
    block[crcFieldOffset + 1] = 0;
    block[crcFieldOffset + 2] = 0;
    block[crcFieldOffset + 3] = 0;
    var crc = Crc32.Compute(block, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(block[crcFieldOffset..], crc);
  }
}
