#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs;

/// <summary>
/// Writes a minimal IBM Journaled File System (JFS1) aggregate image with a single
/// allocation group, one fileset, and an inline dtree root directory.
/// <para>
/// Byte layout matches the on-disk structures in <c>linux/fs/jfs</c>: all integer
/// fields are little-endian. <c>pxd_t</c> is packed as
/// <c>len_addr = (len &amp; 0xFFFFFF) | ((addr &gt;&gt; 32) &lt;&lt; 24)</c>,
/// <c>addr2 = addr &amp; 0xFFFFFFFF</c> (see <c>jfs_types.h</c>).
/// Dtree slot names are UCS-2 (UTF-16 LE) and overflow via the slot chain.
/// Round-trips through <see cref="JfsReader"/>.
/// </para>
/// </summary>
public sealed class JfsWriter {
  // ── spec constants ────────────────────────────────────────────────────────
  internal const int SuperblockOffset = 0x8000;   // 64 × 512 = 32768
  internal const int BlockSize = 4096;
  internal const int SectorSize = 512;
  internal const int L2BSize = 12;                // log2(4096)
  internal const int L2PBSize = 9;                // log2(512)
  internal const int L2BFactor = 3;               // 4096 / 512 = 8 = 2^3
  internal const uint JfsMagic = 0x3153464A;      // "JFS1" little-endian
  internal const uint JfsVersion = 2;
  internal const int InodeSize = 512;
  internal const int InodesPerBlock = BlockSize / InodeSize;    // 8
  internal const int FilesetIno = 16;             // FILESYSTEM_I in aggregate inode table
  internal const int RootIno = 2;                 // root directory in fileset inode table
  internal const int XtreeDataOffset = 224;       // di_data/dtroot offset inside 512-byte dinode
  internal const int DiDataSize = 288;            // size of _dtroot / _xtroot union (dinode 512 - 224)
  internal const int InostampFixed = unchecked((int)0x87878787);
  internal const int MinImageBlocks = 4096;       // 16 MB / 4096 minimum
  internal const int MaxFilesInRoot = 8;          // inline dtree has 9 slots (1 header + 8 entries)

  // Layout blocks. AIT needs 3 blocks so inode 16 (FILESYSTEM_I) fits: inodes 0-7 / 8-15 / 16-23.
  private const int AitBlock = 9;                 // aggregate inode table (block 9..11)
  private const int AitBlockCount = 3;
  private const int FsitBlock = 12;               // fileset inode table (contains root)
  private const int DataStartBlock = 13;

  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly byte[] _volumeUuid = Guid.NewGuid().ToByteArray();
  private readonly byte[] _logUuid = Guid.NewGuid().ToByteArray();

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= MaxFilesInRoot)
      throw new InvalidOperationException($"JfsWriter supports at most {MaxFilesInRoot} files in the inline root dtree.");
    var leaf = Path.GetFileName(name);
    this._files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // ── block layout ───────────────────────────────────────────────────────
    var fileDataBlocks = new int[this._files.Count];
    var fileBlockCounts = new int[this._files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < this._files.Count; i++) {
      fileDataBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (this._files[i].Data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }

    var totalBlocks = Math.Max(MinImageBlocks, nextBlock + 4);
    var image = new byte[(long)totalBlocks * BlockSize];

    WriteSuperblock(image, totalBlocks);
    WriteAggregateInodeTable(image);
    WriteFilesetInodeTable(image, fileDataBlocks, fileBlockCounts);

    for (var i = 0; i < this._files.Count; i++) {
      var data = this._files[i].Data;
      if (data.Length > 0)
        data.CopyTo(image, (long)fileDataBlocks[i] * BlockSize);
    }

    output.Write(image);
  }

  // ── superblock (jfs_superblock, le) ──────────────────────────────────────
  private void WriteSuperblock(byte[] image, int totalBlocks) {
    var sb = image.AsSpan(SuperblockOffset);
    // s_magic[4] = "JFS1"
    "JFS1"u8.CopyTo(sb);
    // s_version (le32)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], JfsVersion);
    // s_size (le64) aggregate size in blocks
    BinaryPrimitives.WriteUInt64LittleEndian(sb[8..], (ulong)totalBlocks);
    // s_bsize (le32)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], BlockSize);
    // s_l2bsize (le16) at offset 20
    BinaryPrimitives.WriteUInt16LittleEndian(sb[20..], L2BSize);
    // s_l2bfactor (le16) at offset 22
    BinaryPrimitives.WriteUInt16LittleEndian(sb[22..], L2BFactor);
    // s_pbsize (le32) at offset 24
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], SectorSize);
    // s_l2pbsize (le16) at offset 28
    BinaryPrimitives.WriteUInt16LittleEndian(sb[28..], L2PBSize);
    // pad (le16) at offset 30
    BinaryPrimitives.WriteUInt16LittleEndian(sb[30..], 0);
    // s_agsize (le32) at offset 32 — single AG covers whole aggregate
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)totalBlocks);
    // s_flag (le32) at 36 — 0 (no group commit)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], 0);
    // s_state (le32) at 40 — 0 = FM_CLEAN
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 0);
    // s_compress (le32) at 44
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 0);
    // s_ait2 pxd_t (8 bytes) at 48 — secondary aggregate inode table (we use it as primary pointer)
    WritePxd(sb[48..], length: (uint)AitBlockCount, address: (ulong)AitBlock);
    // s_aim2 pxd_t at 56 — secondary aggregate inode map
    WritePxd(sb[56..], length: 0, address: 0);
    // s_logdev (le32) at 64 — 0 means inline log
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], 0);
    // s_logserial (le32) at 68
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 1);
    // s_logpxd pxd_t at 72 (inline log: reserve final blocks)
    WritePxd(sb[72..], length: 1, address: (ulong)(totalBlocks - 2));
    // s_fsckpxd pxd_t at 80
    WritePxd(sb[80..], length: 1, address: (ulong)(totalBlocks - 1));
    // s_time timestruc_t at 88 (8 bytes: sec + nsec)
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(sb[88..], (uint)now);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[92..], 0);
    // s_fsckloglen (le32) at 96
    BinaryPrimitives.WriteUInt32LittleEndian(sb[96..], 1);
    // s_fscklog s8 at 100
    sb[100] = 0;
    // s_fpack[11] pack/volume name at 101
    var pack = Encoding.ASCII.GetBytes("JFS-WORKBENCH\0");
    var packCopy = Math.Min(pack.Length, 11);
    pack.AsSpan(0, packCopy).CopyTo(sb[101..]);
    // s_xsize (le64) at 112 — extendfs size
    BinaryPrimitives.WriteUInt64LittleEndian(sb[112..], (ulong)totalBlocks);
    // s_xfsckpxd pxd_t at 120
    WritePxd(sb[120..], length: 1, address: (ulong)(totalBlocks - 1));
    // s_xlogpxd pxd_t at 128
    WritePxd(sb[128..], length: 1, address: (ulong)(totalBlocks - 2));
    // s_uuid[16] at 136 — MUST be non-zero
    this._volumeUuid.CopyTo(sb[136..]);
    // s_label[16] at 152
    var label = Encoding.ASCII.GetBytes("JFS Workbench\0\0\0");
    label.AsSpan(0, Math.Min(label.Length, 16)).CopyTo(sb[152..]);
    // s_loguuid[16] at 168
    this._logUuid.CopyTo(sb[168..]);
  }

  // ── aggregate inode table: inode 16 = FILESYSTEM_I with xtree → fileset inode table.
  // AIT is a contiguous run of blocks starting at AitBlock. Inode N is at byte
  // offset AitBlock*BlockSize + N*InodeSize.
  private static void WriteAggregateInodeTable(byte[] image) {
    var aitOffset = (long)AitBlock * BlockSize;
    var fsinoOff = (int)(aitOffset + FilesetIno * InodeSize);

    WriteInodeCore(image, fsinoOff, inoNumber: FilesetIno, mode: 0x41ED, nlink: 1, size: BlockSize);
    // FILESYSTEM_I's xtree root points at the fileset inode table (1 block).
    WriteXtreeRoot(image.AsSpan(fsinoOff + XtreeDataOffset, DiDataSize),
      entries: [(offset: 0, length: 1, address: (ulong)FsitBlock)]);
  }

  // ── fileset inode table: inode 2 = root dir (inline dtree) + file inodes
  private void WriteFilesetInodeTable(byte[] image, int[] fileDataBlocks, int[] fileBlockCounts) {
    var fsitOffset = (long)FsitBlock * BlockSize;

    // Root directory inode 2
    var rootOff = (int)(fsitOffset + RootIno * InodeSize);
    // Directory size = whole inline dtree area (32 bytes header + 8 * 32 slots = 288)
    WriteInodeCore(image, rootOff, inoNumber: RootIno, mode: 0x41ED, nlink: 2, size: 288);
    WriteRootDtree(image.AsSpan(rootOff + XtreeDataOffset, DiDataSize));

    // File inodes: 3, 4, ...
    for (var i = 0; i < this._files.Count; i++) {
      var ino = 3 + i;
      var inoOff = (int)(fsitOffset + (long)ino * InodeSize);
      var (_, data) = this._files[i];
      WriteInodeCore(image, inoOff, inoNumber: (uint)ino, mode: 0x81A4, nlink: 1, size: (ulong)data.Length);
      WriteXtreeRoot(image.AsSpan(inoOff + XtreeDataOffset, DiDataSize),
        entries: [(offset: 0, length: (uint)fileBlockCounts[i], address: (ulong)fileDataBlocks[i])]);
    }
  }

  // ── dinode common header (first 256 bytes). di_data starts at 256. ────
  private static void WriteInodeCore(byte[] image, int ioff, uint inoNumber, uint mode, uint nlink, ulong size) {
    var di = image.AsSpan(ioff, InodeSize);
    di.Clear();
    // di_inostamp (le32) at 0 — current JFS uses 0x87878787
    BinaryPrimitives.WriteInt32LittleEndian(di[0..], InostampFixed);
    // di_fileset (le32) at 4 — 16 for all aggregate inodes AND for fileset inodes pointing at FILESYSTEM_I's tree
    BinaryPrimitives.WriteUInt32LittleEndian(di[4..], FilesetIno);
    // di_number (le32) at 8
    BinaryPrimitives.WriteUInt32LittleEndian(di[8..], inoNumber);
    // di_gen (le32) at 12
    BinaryPrimitives.WriteUInt32LittleEndian(di[12..], 1);
    // di_ixpxd pxd_t at 16 — self-descriptor for this inode table
    WritePxd(di[16..], length: 1, address: 0);
    // di_size (le64) at 24
    BinaryPrimitives.WriteUInt64LittleEndian(di[24..], size);
    // di_nblocks (le64) at 32
    BinaryPrimitives.WriteUInt64LittleEndian(di[32..], (size + BlockSize - 1) / BlockSize);
    // di_nlink (le32) at 40
    BinaryPrimitives.WriteUInt32LittleEndian(di[40..], nlink);
    // di_uid (le32) at 44
    BinaryPrimitives.WriteUInt32LittleEndian(di[44..], 0);
    // di_gid (le32) at 48
    BinaryPrimitives.WriteUInt32LittleEndian(di[48..], 0);
    // di_mode (le32) at 52
    BinaryPrimitives.WriteUInt32LittleEndian(di[52..], mode);
    // 4 × timestruc_t (each 8 bytes): di_atime @56, di_ctime @64, di_mtime @72, di_otime @80
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    for (var t = 0; t < 4; t++) {
      BinaryPrimitives.WriteUInt32LittleEndian(di[(56 + t * 8)..], now);      // tv_sec
      BinaryPrimitives.WriteUInt32LittleEndian(di[(60 + t * 8)..], 0);        // tv_nsec
    }
    // di_acl dxd_t at 88 (16 bytes) — zero
    // di_ea dxd_t at 104 (16 bytes) — zero
    // di_next_index le32 at 120
    BinaryPrimitives.WriteUInt32LittleEndian(di[120..], 0);
    // di_acltype le32 at 124
    BinaryPrimitives.WriteUInt32LittleEndian(di[124..], 0);
    // Union starts at 128. For dir: _table[12] at 128..223, _dtroot at 224..511 (288 bytes).
    // For file: _u1 at 128..223, _xtroot at 224..511 (288 bytes).
  }

  // ── xtree root in di_data (256 bytes) ─────────────────────────────────
  // Header 16 bytes:
  //   flag(1) + rsrvd1(1) + nextindex(le16) + maxentry(le16) + rsrvd2(le16) + pxd_self(8) ...
  // Wait: xtheader is 32 bytes (next(8) + prev(8) + flag(1) + rsrvd1(1) + nextindex(le16) + maxentry(le16) + rsrvd2(le16) + self pxd(8))
  // Entries start at header[2] (XTENTRYSTART = 2 → index 2 in the xad array).
  // Since entries are 16 bytes each and the header is 32 bytes = 2 xad slots, header occupies slots [0] and [1];
  // entries fill slots [2..maxentry-1]. In 256 bytes we fit 16 xad slots total, 14 usable.
  private static void WriteXtreeRoot(Span<byte> data, (ulong offset, uint length, ulong address)[] entries) {
    data.Clear();
    const int XtentryStart = 2;
    var maxEntry = data.Length / 16;   // 16 for 256-byte area
    // next(le64) @ 0 = 0
    // prev(le64) @ 8 = 0
    // flag u8 @ 16: BT_LEAF | BT_ROOT = 0x01 | 0x40 = 0x41 (per jfs_btree.h)
    data[16] = 0x41;
    data[17] = 0; // rsrvd1
    BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)(XtentryStart + entries.Length));  // nextindex
    BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)maxEntry);                         // maxentry
    BinaryPrimitives.WriteUInt16LittleEndian(data[22..], 0);                                        // rsrvd2
    WritePxd(data[24..], length: 0, address: 0);                                                    // self

    for (var i = 0; i < entries.Length; i++) {
      var entryOff = (XtentryStart + i) * 16;
      WriteXad(data.Slice(entryOff, 16), entries[i].offset, entries[i].length, entries[i].address);
    }
  }

  // ── xad_t (16 bytes) ──────────────────────────────────────────────────
  // struct xad {
  //   u8 flag;              // byte 0
  //   u8 rsrvd[2];          // bytes 1,2
  //   u8 off1;              // byte 3 — upper 8 bits of 40-bit offset
  //   le32 off2;            // bytes 4..7 — lower 32 bits of offset
  //   pxd_t loc;            // bytes 8..15 — length + address
  // };
  private static void WriteXad(Span<byte> dst, ulong offset, uint length, ulong address) {
    dst.Clear();
    dst[0] = 0;              // flag = no special (not XAD_NEW/XAD_COMPRESSED)
    dst[1] = 0; dst[2] = 0;  // reserved
    dst[3] = (byte)((offset >> 32) & 0xFF);                          // off1
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(offset & 0xFFFFFFFF)); // off2
    WritePxd(dst[8..], length, address);
  }

  // ── pxd_t (8 bytes) ───────────────────────────────────────────────────
  // len_addr (le32): low 24 bits = length, high 8 bits = upper 8 bits of 40-bit address
  // addr2    (le32): low 32 bits of address
  // Verified against linux/fs/jfs/jfs_types.h (PXDlength/PXDaddress inline fns).
  internal static void WritePxd(Span<byte> dst, uint length, ulong address) {
    var lenMasked = length & 0xFFFFFFu;
    var addrHi = (uint)((address >> 32) & 0xFF) << 24;
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0..], lenMasked | addrHi);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(address & 0xFFFFFFFF));
  }

  // ── dtroot inline directory (288 bytes at di_data + 0..287, total 288) ─
  // Layout (per jfs_dtree.h):
  //   slot[0] = header: DASD(16) + flag(1) + nextindex(1) + freecnt(1) + freelist(1) + idotdot(le32) + stbl[8]  → 32
  //   slot[1..8] = dtslot[32] each
  //     dtslot { s8 next; s8 cnt; le16 name[15]; }  — leaf slot for an entry is actually ldtentry (same 32B size)
  //     ldtentry { le32 inumber; s8 next; u8 namlen; le16 name[11]; le32 index; }
  //
  // We place entry i in slot (i+1), use stbl[i] = (i+1). Names up to 11 UCS-2 chars fit in one slot.
  private void WriteRootDtree(Span<byte> data) {
    data.Clear();
    // DASD (16 bytes) at +0 — zero
    data[16] = 0;                                           // flag (DT_INLINE not needed for root since it is always inline)
    data[17] = (byte)this._files.Count;                     // nextindex
    // freecnt + freelist: start freelist at slot index = 1 + count, chain remaining
    var freeStart = 1 + this._files.Count;
    sbyte freecnt = (sbyte)Math.Max(0, 8 - this._files.Count);
    data[18] = (byte)freecnt;                               // freecnt
    data[19] = (byte)(freecnt == 0 ? -1 : freeStart);       // freelist
    BinaryPrimitives.WriteUInt32LittleEndian(data[20..], RootIno);   // idotdot (self for root)
    // stbl[8] at offset 24: populate indices [0..count-1], rest unused (we set to -1 for safety)
    for (var i = 0; i < 8; i++)
      data[24 + i] = i < this._files.Count ? (byte)(i + 1) : unchecked((byte)-1);

    // ldtentry slots for each file: slot (i+1) starting at offset (i+1)*32
    for (var i = 0; i < this._files.Count; i++) {
      var slotOff = (i + 1) * 32;
      var childIno = (uint)(3 + i);
      BinaryPrimitives.WriteUInt32LittleEndian(data[slotOff..], childIno);     // inumber
      var name = this._files[i].Name;
      var nameLen = Math.Min(name.Length, 11);
      data[slotOff + 4] = unchecked((byte)-1);                                 // next = -1 (no continuation)
      data[slotOff + 5] = (byte)nameLen;                                       // namlen
      for (var c = 0; c < nameLen; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(data[(slotOff + 6 + c * 2)..], name[c]);
      BinaryPrimitives.WriteUInt32LittleEndian(data[(slotOff + 28)..], (uint)i); // index (dir-table slot)
    }

    // Free-list chain over remaining slots (i+1 .. 8)
    for (var s = freeStart; s <= 8; s++) {
      var slotOff = s * 32;
      // dtslot: next points to next free slot, or -1 for last
      data[slotOff] = (byte)(s < 8 ? s + 1 : unchecked((byte)-1));
      data[slotOff + 1] = 0;
    }
  }
}
