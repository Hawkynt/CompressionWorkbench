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
  private const int DataStartBlock = InodeChunkEndBlock;

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
    // Short-form dir with 4-byte inode entries — hard ceiling of 15 files (64-inode
    // chunk minus root minus rbm minus rsum minus a few reserve slots).
    if (this._files.Count >= InodesPerBlock - 1)
      throw new InvalidOperationException($"XfsWriter supports at most {InodesPerBlock - 1} files.");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 250) leaf = leaf[..250];
    this._files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    // Root chunk slot layout (AG 0, inode chunk starting at agbno=InodeChunkBlock):
    //   slot 0  = root directory (rootino)
    //   slot 1  = sb_rbmino (realtime bitmap inode) — empty regular file
    //   slot 2  = sb_rsumino (realtime summary inode) — empty regular file
    //   slot 3+ = user file inodes
    //   remaining = free
    const int FirstFileSlot = 3;

    // ── Allocate per-file data blocks in AG 0, starting at DataStartBlock ──
    var fileDataBlocks = new int[this._files.Count];
    var fileBlockCounts = new int[this._files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < this._files.Count; i++) {
      fileDataBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (this._files[i].data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }
    // Reserve 64 blocks (256 KiB) for an internal log at the tail of AG 0.
    const int LogBlocks = 64;
    var logStartAgBno = nextBlock;
    nextBlock += LogBlocks;
    var logStartFsBno = (ulong)logStartAgBno;

    // Total blocks: pad to AgCount × AgBlocks so every AG has the same size.
    var totalBlocks = Math.Max(nextBlock, AgBlocks * AgCount);
    var image = new byte[totalBlocks * BlockSize];

    // Free-extent bookkeeping (AG 0: single trailing extent after log).
    var freeStartAg0 = logStartAgBno + LogBlocks;
    var freeLenAg0 = AgBlocks - freeStartAg0;
    // Free-extent bookkeeping (AG 1+): no inode chunk, no files — entire post-
    // metadata region is free.
    var freeStartAgN = DataStartBlock - InodeChunkBlocks;  // no inode chunk in AG 1+
    var freeLenAgN = AgBlocks - freeStartAgN;

    var usedInodeSlots = FirstFileSlot + this._files.Count;
    var freeInodeSlots = InodesPerChunk - usedInodeSlots;

    // ── Per-AG metadata ──
    for (var ag = 0; ag < AgCount; ag++) {
      var agByteOffset = ag * AgBlocks * BlockSize;

      WriteSuperblock(image.AsSpan(agByteOffset),
        totalBlocks: (ulong)(AgBlocks * AgCount),
        logStart: logStartFsBno,
        logBlocks: LogBlocks,
        icount: (ulong)(ag == 0 ? InodesPerChunk : 0),
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
        inodeCount: ag == 0 ? (uint)InodesPerChunk : 0u,
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
        hasChunk: ag == 0,
        startAgino: (uint)RootIno,
        freeMask: ComputeFreeMask(usedInodeSlots));
    }

    // ── Root directory inode (slot 0 of chunk) ──
    var rootOff = InodeChunkBlock * BlockSize;
    WriteInodeCoreV3(image, rootOff, RootIno, mode: 0x41ED /* S_IFDIR|0755 */,
      format: 1, nlink: 2);
    // Short-form dir header (xfs_dir2_sf_hdr):
    //   __u8 count      — number of 4-byte-inode entries
    //   __u8 i8count    — number of 8-byte-inode entries
    //   __u8 parent[4]  — parent inode (4 B when i8count==0, else 8 B)
    var dirOff = rootOff + 176;
    image[dirOff] = (byte)this._files.Count;
    image[dirOff + 1] = 0; // i8count
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)RootIno);
    var entryPos = dirOff + 6;
    var firstChildIno = RootIno + (ulong)FirstFileSlot;
    // Start entry offsets at 0x60 — standard base past "." (0x30) and ".." (0x40).
    ushort nextOffset = 0x60;
    for (var i = 0; i < this._files.Count; i++) {
      var childIno = (uint)(firstChildIno + (ulong)i);
      var nameBytes = Encoding.UTF8.GetBytes(this._files[i].name);
      var nameLen = Math.Min(nameBytes.Length, 250);
      // Entry (xfs_dir2_sf_entry) with FTYPE feature:
      //   __u8 namelen
      //   __u8 offset[2]     (monotonic; mimics data-block offset)
      //   __u8 name[namelen]
      //   __u8 ftype         (only when sb_features_incompat has FTYPE)
      //   __u8 ino[4 or 8]
      image[entryPos] = (byte)nameLen;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryPos + 1), nextOffset);
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryPos + 3));
      image[entryPos + 3 + nameLen] = 1; // DT_REG (regular file)
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entryPos + 4 + nameLen), childIno);
      entryPos += 3 + nameLen + 1 + 4;
      // Next entry's sf_offset mimics the data-block layout, NOT the shortform
      // on-disk entry size. A dir2 data block entry with FTYPE is:
      //   ino(8) + namelen(1) + name[nameLen] + ftype(1) + tag(2) = nameLen + 12,
      // padded to 8-byte alignment. xfs_repair rejects "entry contains offset
      // out of order in shortform dir" when we advance by the shortform entry
      // size (nameLen + 8 padded) instead.
      nextOffset = (ushort)(nextOffset + ((nameLen + 12 + 7) & ~7));
    }
    var dirSize = entryPos - dirOff;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rootOff + 56), (ulong)dirSize);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rootOff + 64), 0);

    // ── Realtime bitmap inode (slot 1) — empty S_IFREG|0 (mkfs convention) ──
    var rbmOff = InodeChunkBlock * BlockSize + 1 * InodeSize;
    WriteInodeCoreV3(image, rbmOff, RootIno + 1, mode: 0x8000 /* S_IFREG, no perm bits */,
      format: 2 /* extents, 0 extents */, nlink: 1);

    // ── Realtime summary inode (slot 2) ──
    var rsumOff = InodeChunkBlock * BlockSize + 2 * InodeSize;
    WriteInodeCoreV3(image, rsumOff, RootIno + 2, mode: 0x8000, format: 2, nlink: 1);

    // ── User file inodes (slots 3+) ──
    for (var i = 0; i < this._files.Count; i++) {
      var ioff = InodeChunkBlock * BlockSize + (FirstFileSlot + i) * InodeSize;
      WriteInodeCoreV3(image, ioff, firstChildIno + (ulong)i,
        mode: 0x81A4 /* S_IFREG|0644 */, format: 2, nlink: 1);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)this._files[i].data.Length);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 64), (ulong)fileBlockCounts[i]);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), 1);

      var startBlock = (ulong)fileDataBlocks[i];
      var blockCount = (ulong)fileBlockCounts[i];
      var hi = (startBlock >> 43) & 0x1FF;
      var lo = (startBlock << 21) | (blockCount & 0x1FFFFF);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 176), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 184), lo);

      this._files[i].data.CopyTo(image, fileDataBlocks[i] * BlockSize);
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
    for (var slot = FirstFileSlot + this._files.Count; slot < InodesPerChunk; slot++) {
      var ioff = InodeChunkBlock * BlockSize + slot * InodeSize;
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
    // Every inode slot in the root chunk needs a valid CRC.
    for (var slot = 0; slot < InodesPerChunk; slot++) {
      var ioff = InodeChunkBlock * BlockSize + slot * InodeSize;
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
  /// Writes an inobt leaf with 0 or 1 records. When <paramref name="hasChunk"/>
  /// is true, emits a record describing a full 64-inode chunk starting at
  /// <paramref name="startAgino"/> with the given free-slot bitmask.
  /// </summary>
  private static void WriteInobt(Span<byte> block, uint agNumber, ulong selfSector,
      bool hasChunk, uint startAgino, ulong freeMask) {
    WriteBtreeSblockHeader(block, InobtV5Magic, agNumber, selfSector,
      level: 0, numrecs: hasChunk ? (ushort)1 : (ushort)0);
    if (hasChunk) {
      // Non-sparse inobt record (16 bytes):
      //   __be32 ir_startino;
      //   __be32 ir_freecount;  // count of free inodes in chunk
      //   __be64 ir_free;       // bitmask — bit=1 means slot is free
      var freeCount = (uint)System.Numerics.BitOperations.PopCount(freeMask);
      BinaryPrimitives.WriteUInt32BigEndian(block[BtreeRecOffset..], startAgino);
      BinaryPrimitives.WriteUInt32BigEndian(block[(BtreeRecOffset + 4)..], freeCount);
      BinaryPrimitives.WriteUInt64BigEndian(block[(BtreeRecOffset + 8)..], freeMask);
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
