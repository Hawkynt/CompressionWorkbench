using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hammer;

/// <summary>
/// Writes a single-volume HAMMER (DragonFly BSD, HAMMER1) filesystem image that
/// DragonFly recognises and mounts. The output is a faithful port of
/// <c>newfs_hammer(8)</c> (<c>sbin/newfs_hammer/newfs_hammer.c</c>) together with
/// the on-disk helpers in <c>sbin/hammer/ondisk.c</c> and
/// <c>sbin/hammer/blockmap.c</c>: it lays down the volume header, the freemap
/// (zone-4 two-layer blockmap), the UNDO/REDO FIFO (zone-3), and a minimal root
/// B-Tree (zone-8) holding the root directory's inode and PFS#0 records.
///
/// <para>Geometry mirrors newfs exactly: the volume is split into a 256 KB header
/// junk area (<c>vol_bot_beg</c>), a boot area, a memory log, then the zone-2
/// buffer area. Every metadata block carries the version-gated CRC
/// (see <see cref="HammerCrc"/>).</para>
///
/// <para>HAMMER's UNDO FIFO has a hard minimum of
/// <c>HAMMER_MIN_UNDO_BIGBLOCKS (64) * HAMMER_BIGBLOCK_SIZE (8 MB) = 512 MB</c>,
/// so the smallest volume that <c>newfs_hammer</c>/this writer can format is on
/// the order of ~1 GB. The output stream is grown to that size (sparse on
/// filesystems that support holes).</para>
///
/// <para>The root directory is created empty (exactly as <c>newfs_hammer</c>
/// leaves it). File payloads passed to <see cref="AddFile"/> are recorded for
/// the label/inode-count surface but are not yet materialised as directory
/// entries — adding real directory entries requires additional B-Tree records
/// and is out of scope for v1.</para>
/// </summary>
public sealed class HammerWriter {
  // --- Constants from sys/vfs/hammer/hammer_disk.h ---
  private const ulong VolSignature = 0xC8414D4DC5523031UL;     // HAMMER_FSBUF_VOLUME
  private const int Bufsize = 16384;                            // HAMMER_BUFSIZE
  private const long BufMask64 = Bufsize - 1;
  private const long BigblockSize = 8192L * 1024;              // HAMMER_BIGBLOCK_SIZE (8 MB)
  private const long BigblockMask64 = BigblockSize - 1;
  private const int MaxZones = 16;                              // HAMMER_MAX_ZONES
  private const int MaxUndoBigblocks = 128;                     // HAMMER_MAX_UNDO_BIGBLOCKS
  private const int MinUndoBigblocks = 64;                      // HAMMER_MIN_UNDO_BIGBLOCKS
  private const int VolumeOndiskSize = 1928;                    // sizeof(struct hammer_volume_ondisk)

  private const int RootVolno = 0;                              // HAMMER_ROOT_VOLNO
  private const uint VolVersion = 7;                            // HAMMER_VOL_VERSION_DEFAULT

  private const long HeaderJunkSize = Bufsize * 16;             // HAMMER_VOL_JUNK_SIZE = 256 KB
  private const long BootMinBytes = 32 * 1024;                  // HAMMER_BOOT_MINBYTES
  private const long MemMinBytes = 256 * 1024;                  // HAMMER_MEM_MINBYTES

  // Zone indices.
  private const int ZoneRawBuffer = 2;                         // HAMMER_ZONE_RAW_BUFFER_INDEX
  private const int ZoneUndo = 3;                              // HAMMER_ZONE_UNDO_INDEX
  private const int ZoneFreemap = 4;                          // HAMMER_ZONE_FREEMAP_INDEX
  private const int ZoneBtree = 8;                            // HAMMER_ZONE_BTREE_INDEX
  private const int ZoneMeta = 9;                             // HAMMER_ZONE_META_INDEX
  private const int ZoneLargeData = 10;                       // HAMMER_ZONE_LARGE_DATA_INDEX
  private const int ZoneSmallData = 11;                       // HAMMER_ZONE_SMALL_DATA_INDEX
  private const int ZoneUnavail = 15;                         // HAMMER_ZONE_UNAVAIL_INDEX

  private const ulong OffZoneMask = 0xF000000000000000UL;
  private const ulong OffShortMask = 0x000FFFFFFFFFFFFFUL;
  private const ulong OffLongMask = 0x0FFFFFFFFFFFFFFFUL;
  private const ulong BlockmapUnavail = 0xFFFFFFFFFFFFFFFFUL; // (hammer_off_t)-1
  private const long BlockmapUnavailL = -1L;                  // same bit pattern, signed

  // Layer geometry: layer1 entry = 32 bytes, layer2 entry = 16 bytes.
  private const int Layer1EntrySize = 32;
  private const int Layer2EntrySize = 16;
  private const long BlockmapRadix2 = BigblockSize / Layer2EntrySize;   // 2^19
  private const long BlockmapLayer2 = BlockmapRadix2 * BigblockSize;    // 2^(19+23) = 4 TB
  private const long BlockmapLayer1Mask = (long)((1UL << (18 + 19 + 23)) - 1);
  private const long BlockmapLayer2Mask = BlockmapLayer2 - 1;

  // FIFO / B-Tree / inode constants.
  private const int UndoAlign = 512;                          // HAMMER_UNDO_ALIGN
  private const ushort HeadSignature = 0xC84E;
  private const ushort TailSignature = 0xC74F;
  private const ushort HeadTypeDummy = 0x0041;
  private const int BtreeLeafElms = 63;
  private const int NodeOndiskSize = 64 + BtreeLeafElms * 64; // 4096 (header + 63 x 64)
  private const byte BtreeTypeLeaf = (byte)'L';
  private const byte BtreeTypeRecord = (byte)'R';
  private const int InodeDataSize = 128;                      // sizeof(struct hammer_inode_data)
  private const int InodeCrcsize = 112;                       // offsetof(hammer_inode_data, mtime)
  private const int PfsdSize = 264;                           // sizeof(struct hammer_pseudofs_data)
  private const ushort InodeDataVersion = 1;
  private const long ObjidRoot = 1;                          // HAMMER_OBJID_ROOT
  private const ushort RectypeInode = 0x0001;
  private const ushort RectypePfs = 0x0015;
  private const byte ObjtypeDirectory = 1;
  private const uint LocalizeInode = 0x00000001;
  private const uint LocalizeMisc = 0x00000002;
  private const byte InodeCapDirLocalIno = 0x04;
  private const byte InodeCapDirhashAlg1 = 0x01;

  // The DragonFly HAMMER filesystem-type UUID "61dc63ac-6e38-11dc-8513-01301bb8a9f5".
  private static readonly byte[] FsTypeUuid =
    [0xac, 0x63, 0xdc, 0x61, 0x38, 0x6e, 0xdc, 0x11, 0x85, 0x13, 0x01, 0x30, 0x1b, 0xb8, 0xa9, 0xf5];

  private readonly List<(string Name, byte[] Content)> _files = [];
  private string _label = "hammer";
  private long _volumeSize = 2L * 1024 * 1024 * 1024 + 64 * 1024 * 1024; // ~2.06 GB default

  /// <summary>Filesystem label (max 63 chars, ASCII). Mirrors <c>newfs_hammer -L</c>.</summary>
  public string Label {
    get => this._label;
    set => this._label = value ?? "hammer";
  }

  /// <summary>
  /// Total volume size in bytes. Forced up to the HAMMER minimum (~1 GB) so the
  /// UNDO FIFO and freemap fit. Aligned down to <c>HAMMER_BUFSIZE</c> internally.
  /// </summary>
  public long VolumeSize {
    get => this._volumeSize;
    set => this._volumeSize = value;
  }

  /// <summary>
  /// Records a file for inclusion. v1 stores the entry for label/inode-count
  /// purposes; directory-entry materialisation is not yet implemented.
  /// </summary>
  public void AddFile(string name, byte[] content) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    this._files.Add((name, content ?? []));
  }

  // ===== Volume layout state (poor-man's allocator, mirrors newfs) =====
  private long _volBotBeg, _volMemBeg, _volBufBeg, _volBufEnd;
  private long _volFreeOff, _volFreeEnd;       // zone-2 raw-buffer offsets
  private long _vol0StatFreebigblocks;
  private long _vol0StatBigblocks;
  private long _vol0StatInodes;
  private long _vol0BtreeRoot;
  private ulong _vol0NextTid = 0x0000000100000000UL;
  private long _undoLimit;
  private byte[] _volFsid = new byte[16];

  // Per-zone blockmap header (vol0_blockmap[16]).
  private readonly Blockmap[] _blockmap = new Blockmap[MaxZones];
  private readonly long[] _undoArray = new long[MaxUndoBigblocks];

  // Sparse physical buffer cache: rawDeviceOffset (16 KB aligned) -> 16 KB buffer.
  private readonly SortedDictionary<long, byte[]> _buffers = [];

  private sealed class Blockmap {
    public long PhysOffset;
    public long FirstOffset;
    public long NextOffset;
    public long AllocOffset;
  }

  /// <summary>Formats the HAMMER volume and writes it to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var size = AlignBufDown(Math.Max(this._volumeSize, BigblockSize * (MinUndoBigblocks + 16)));

    this._volFsid = Guid.NewGuid().ToByteArray();
    this.FormatVolumeGeometry(size);
    this.FormatFreemap();
    this._vol0StatFreebigblocks = this.InitializeFreemap();
    // Format zones mapped to zone-2 (btree, meta, large-data, small-data).
    foreach (var z in new[] { ZoneBtree, ZoneMeta, ZoneLargeData, ZoneSmallData })
      this.FormatBlockmap(z);
    this.FormatUndomap();
    this._vol0StatBigblocks = this._vol0StatFreebigblocks;
    this._vol0BtreeRoot = this.FormatRootDirectory();
    ++this._vol0StatInodes;

    this.WriteImage(output, size);
  }

  // ---- Geometry (newfs_hammer format_volume) ----
  private void FormatVolumeGeometry(long size) {
    var bootArea = Math.Max(BootMinBytes, AlignBuf(InitBootAreaSize(size)));
    var memLog = Math.Max(MemMinBytes, AlignBuf(InitMemoryLogSize(size)));

    var volAlloc = HeaderJunkSize;
    this._volBotBeg = volAlloc;
    volAlloc += bootArea;
    this._volMemBeg = volAlloc;
    volAlloc += memLog;
    this._volBufBeg = volAlloc;
    this._volBufEnd = AlignBufDown(size);

    var volBufSize = this._volBufEnd - this._volBufBeg;
    this._volFreeOff = EncodeRawBuffer(RootVolno, 0);
    this._volFreeEnd = EncodeRawBuffer(RootVolno, volBufSize & ~BigblockMask64);
  }

  // newfs init_boot_area_size / init_memory_log_size scale with volume size but
  // clamp to NOM values; for our minimal volumes the minimum applies.
  private static long InitBootAreaSize(long _) => BootMinBytes;
  private static long InitMemoryLogSize(long _) => MemMinBytes;

  // ---- Freemap (ondisk.c format_freemap) ----
  private void FormatFreemap() {
    var layer1Offset = this.BootstrapBigblock();
    for (long i = 0; i < BigblockSize; i += Layer1EntrySize) {
      // layer1: blocks_free(8)=0, phys_offset(8)=UNAVAIL, reserved01(8)=0,
      //         layer2_crc(4)=0, layer1_crc(4).
      var entry = new byte[Layer1EntrySize];
      BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0, 8), 0);                 // blocks_free
      BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8, 8), BlockmapUnavail);   // phys_offset
      this.SetLayer1Crc(entry);
      this.WriteAtZone2(layer1Offset + i, entry);
    }

    var bm = this._blockmap[ZoneFreemap] = new Blockmap {
      PhysOffset = layer1Offset,
      FirstOffset = 0,
      NextOffset = EncodeRawBuffer(0, 0),
      AllocOffset = EncodeRawBuffer(255, -1),
    };
    _ = bm;
  }

  // ---- initialize_freemap (ondisk.c) ----
  private long InitializeFreemap() {
    long count = 0;
    var freemapPhys = this._blockmap[ZoneFreemap].PhysOffset;
    var alignedFreeEnd = BlockmapLayer2Doalign(this._volFreeEnd);

    // Pass 1: bootstrap layer2 big-blocks per layer1 entry.
    for (var phys = EncodeRawBuffer(RootVolno, 0); phys < alignedFreeEnd; phys += BlockmapLayer2) {
      var layer1Offset = freemapPhys + Layer1Offset(phys);
      var layer1 = this.ReadZone2(layer1Offset, Layer1EntrySize);
      if (BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(8, 8)) == BlockmapUnavail) {
        BinaryPrimitives.WriteUInt64LittleEndian(layer1.AsSpan(8, 8), (ulong)this.BootstrapBigblock());
        BinaryPrimitives.WriteUInt64LittleEndian(layer1.AsSpan(0, 8), 0); // blocks_free
        this.SetLayer1Crc(layer1);
        this.WriteAtZone2(layer1Offset, layer1);
      }
    }

    // Pass 2: fill every layer2 entry.
    for (var phys = EncodeRawBuffer(RootVolno, 0); phys < alignedFreeEnd; phys += BlockmapLayer2) {
      long layer1Count = 0;
      var layer1Offset = freemapPhys + Layer1Offset(phys);
      var layer1 = this.ReadZone2(layer1Offset, Layer1EntrySize);
      var layer1Phys = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(8, 8));

      for (long block = 0; block < BlockmapLayer2; block += BigblockSize) {
        var layer2Offset = layer1Phys + Layer2Offset(block);
        var layer2 = new byte[Layer2EntrySize];
        var pb = phys + block;
        if (pb < this._volFreeOff) {
          layer2[0] = ZoneFreemap;                                              // zone
          BinaryPrimitives.WriteUInt32LittleEndian(layer2.AsSpan(4, 4), (uint)BigblockSize); // append_off
          BinaryPrimitives.WriteInt32LittleEndian(layer2.AsSpan(8, 4), 0);      // bytes_free
        } else if (pb < this._volFreeEnd) {
          layer2[0] = 0;
          BinaryPrimitives.WriteUInt32LittleEndian(layer2.AsSpan(4, 4), 0);
          BinaryPrimitives.WriteInt32LittleEndian(layer2.AsSpan(8, 4), (int)BigblockSize);
          ++count;
          ++layer1Count;
        } else {
          layer2[0] = ZoneUnavail;
          BinaryPrimitives.WriteUInt32LittleEndian(layer2.AsSpan(4, 4), (uint)BigblockSize);
          BinaryPrimitives.WriteInt32LittleEndian(layer2.AsSpan(8, 4), 0);
        }
        this.SetLayer2Crc(layer2);
        this.WriteAtZone2(layer2Offset, layer2);
      }

      var blocksFree = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(0, 8)) + layer1Count;
      BinaryPrimitives.WriteUInt64LittleEndian(layer1.AsSpan(0, 8), (ulong)blocksFree);
      this.SetLayer1Crc(layer1);
      this.WriteAtZone2(layer1Offset, layer1);
    }

    return count;
  }

  // ---- format_blockmap (degenerate; zones mapped to zone-2) ----
  private void FormatBlockmap(int zone) {
    var zoneBase = ZoneEncode(zone, 0);
    this._blockmap[zone] = new Blockmap {
      PhysOffset = 0,
      FirstOffset = zoneBase,
      NextOffset = zoneBase,
      AllocOffset = Encode(zone, 255, -1),
    };
  }

  // ---- format_undomap (ondisk.c) ----
  private void FormatUndomap() {
    var volBufSize = this._volBufEnd - this._volBufBeg;
    var undoLimit = volBufSize / 1000;
    if (undoLimit < BigblockSize * MinUndoBigblocks)
      undoLimit = BigblockSize * MinUndoBigblocks;
    undoLimit = BigblockDoalign(undoLimit);
    if (undoLimit < BigblockSize)
      undoLimit = BigblockSize;
    if (undoLimit > BigblockSize * MaxUndoBigblocks)
      undoLimit = BigblockSize * MaxUndoBigblocks;
    this._undoLimit = undoLimit;

    this._blockmap[ZoneUndo] = new Blockmap {
      PhysOffset = BlockmapUnavailL,
      FirstOffset = EncodeUndo(0),
      NextOffset = EncodeUndo(0),
      AllocOffset = EncodeUndo(undoLimit),
    };

    var limitIndex = (int)(undoLimit / BigblockSize);
    var n = 0;
    for (; n < limitIndex; ++n)
      this._undoArray[n] = this.AllocUndoBigblock();
    for (; n < MaxUndoBigblocks; ++n)
      this._undoArray[n] = BlockmapUnavailL;

    // Pre-initialise UNDO blocks with DUMMY fifo records (HAMMER v4+).
    var scan = this._blockmap[ZoneUndo].FirstOffset;
    uint seqno = 0;
    while (scan < this._blockmap[ZoneUndo].AllocOffset) {
      const int bytes = UndoAlign;
      var rec = new byte[bytes];
      // hammer_fifo_head: hdr_signature(2), hdr_type(2), hdr_size(4), hdr_seq(4), hdr_crc(4).
      BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0, 2), HeadSignature);
      BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2, 2), HeadTypeDummy);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4, 4), bytes);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8, 4), seqno++);
      // hammer_fifo_tail (out-of-band at end): tail_signature(2), tail_type(2), tail_size(4).
      BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(bytes - 8, 2), TailSignature);
      BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(bytes - 6, 2), HeadTypeDummy);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(bytes - 4, 4), bytes);
      this.SetFifoHeadCrc(rec, bytes);

      var undoPhys = this.UndoToPhys(scan);
      this.WriteAtPhys(undoPhys, rec);
      scan += bytes;
    }
  }

  // ---- format_root_directory (newfs_hammer.c) ----
  private long FormatRootDirectory() {
    var bnode = this.AllocBtreeNode(out var btreeOff);
    var idata = this.AllocMetaElement(InodeDataSize, out var idataOff);
    var pfsd = this.AllocMetaElement(PfsdSize, out var pfsdOff);
    var createTid = this.CreateTid();
    var xtime = NowTimeMicros();

    // ---- inode data (struct hammer_inode_data) ----
    // version(2)@0, mode(2)@2, uflags(4)@4, rmajor(4)@8, rminor(4)@12, ctime(8)@16,
    // parent_obj_id(8)@24, uid(16)@32, gid(16)@48, obj_type(1)@64, cap_flags(1)@65,
    // reserved01(2)@66, reserved02(4)@68, nlinks(8)@72, size(8)@80, ext.symlink[24]@88,
    // mtime(8)@112, atime(8)@120.
    BinaryPrimitives.WriteUInt16LittleEndian(idata.AsSpan(0, 2), InodeDataVersion);   // version
    BinaryPrimitives.WriteUInt16LittleEndian(idata.AsSpan(2, 2), 0x01ED);             // mode 0755
    BinaryPrimitives.WriteUInt64LittleEndian(idata.AsSpan(16, 8), xtime);            // ctime
    idata[64] = ObjtypeDirectory;                                                    // obj_type
    idata[65] = InodeCapDirLocalIno | InodeCapDirhashAlg1;                            // cap_flags (v7 >= 6 & >= 2)
    BinaryPrimitives.WriteUInt64LittleEndian(idata.AsSpan(72, 8), 1);                // nlinks
    BinaryPrimitives.WriteUInt64LittleEndian(idata.AsSpan(80, 8), 0);                // size
    BinaryPrimitives.WriteUInt64LittleEndian(idata.AsSpan(112, 8), xtime);          // mtime
    BinaryPrimitives.WriteUInt64LittleEndian(idata.AsSpan(120, 8), xtime);          // atime
    this.WriteAtZone2(this.ZoneXToZone2(idataOff), idata, InodeDataSize);

    // ---- PFS#0 data (struct hammer_pseudofs_data) ----
    // sync_low_tid(8)@0, sync_beg_tid(8)@8, sync_end_tid(8)@16, sync_beg_ts(8)@24,
    // sync_end_ts(8)@32, shared_uuid(16)@40, unique_uuid(16)@56, reserved01(4)@72,
    // mirror_flags(4)@76, label[64]@80, snapshots[64]@144, ...
    BinaryPrimitives.WriteUInt64LittleEndian(pfsd.AsSpan(0, 8), 1);                  // sync_low_tid
    this._volFsid.CopyTo(pfsd.AsSpan(40, 16));                                       // shared_uuid = vol_fsid
    this._volFsid.CopyTo(pfsd.AsSpan(56, 16));                                       // unique_uuid = vol_fsid
    Encoding.ASCII.GetBytes(Truncate(this._label, 63)).CopyTo(pfsd.AsSpan(80));      // label[64] @ 80
    this.WriteAtZone2(this.ZoneXToZone2(pfsdOff), pfsd, PfsdSize);

    // ---- root B-Tree node (leaf, 2 elements) ----
    BinaryPrimitives.WriteUInt64LittleEndian(bnode.AsSpan(8, 8), 0);                 // parent = 0
    BinaryPrimitives.WriteInt32LittleEndian(bnode.AsSpan(16, 4), 2);                 // count
    bnode[20] = BtreeTypeLeaf;                                                        // type 'L'
    BinaryPrimitives.WriteUInt64LittleEndian(bnode.AsSpan(56, 8), 0);                // mirror_tid

    var nowSecs = (uint)(NowTimeMicros() / 1_000_000UL);
    WriteLeafElm(bnode, 0, createTid, RectypeInode, LocalizeInode, idataOff, InodeDataSize,
                 this.LeafDataCrc(RectypeInode, idata, InodeDataSize), nowSecs);
    WriteLeafElm(bnode, 1, createTid, RectypePfs, LocalizeMisc, pfsdOff, PfsdSize,
                 this.LeafDataCrc(RectypePfs, pfsd, PfsdSize), nowSecs);

    this.SetBtreeCrc(bnode);
    this.WriteAtZone2(this.ZoneXToZone2(btreeOff), bnode, NodeOndiskSize);

    return btreeOff;
  }

  private static void WriteLeafElm(byte[] node, int idx, ulong createTid, ushort recType,
                                   uint localize, long dataOff, int dataLen, uint dataCrc,
                                   uint createTs) {
    // elms[] start at byte 64; each element is 64 bytes.
    var b = 64 + idx * 64;
    // base: obj_id(8), key(8), create_tid(8), delete_tid(8), rec_type(2), obj_type(1), btype(1), localization(4).
    BinaryPrimitives.WriteInt64LittleEndian(node.AsSpan(b + 0, 8), ObjidRoot);
    BinaryPrimitives.WriteInt64LittleEndian(node.AsSpan(b + 8, 8), 0);               // key
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(b + 16, 8), createTid);
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(b + 24, 8), 0);             // delete_tid
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(b + 32, 2), recType);
    node[b + 34] = ObjtypeDirectory;                                                  // obj_type
    node[b + 35] = BtreeTypeRecord;                                                   // btype 'R'
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(b + 36, 4), localize);
    // leaf: create_ts(4)@40, delete_ts(4)@44, data_offset(8)@48, data_len(4)@56, data_crc(4)@60.
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(b + 40, 4), createTs);
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(b + 48, 8), (ulong)dataOff);
    BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(b + 56, 4), dataLen);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(b + 60, 4), dataCrc);
  }

  // ===== allocators (blockmap.c) =====
  private long BootstrapBigblock() {
    var result = this._volFreeOff;
    this._volFreeOff += BigblockSize;
    return result;
  }

  private long AllocUndoBigblock() {
    var result = this.BootstrapBigblock();
    var freemapPhys = this._blockmap[ZoneFreemap].PhysOffset;

    var layer1Offset = freemapPhys + Layer1Offset(result);
    var layer1 = this.ReadZone2(layer1Offset, Layer1EntrySize);
    var bf = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(0, 8)) - 1;
    BinaryPrimitives.WriteUInt64LittleEndian(layer1.AsSpan(0, 8), (ulong)bf);
    this.SetLayer1Crc(layer1);
    this.WriteAtZone2(layer1Offset, layer1);

    var layer1Phys = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(8, 8));
    var layer2Offset = layer1Phys + Layer2Offset(result);
    var layer2 = this.ReadZone2(layer2Offset, Layer2EntrySize);
    layer2[0] = ZoneUndo;
    BinaryPrimitives.WriteUInt32LittleEndian(layer2.AsSpan(4, 4), (uint)BigblockSize);
    BinaryPrimitives.WriteInt32LittleEndian(layer2.AsSpan(8, 4), 0);
    this.SetLayer2Crc(layer2);
    this.WriteAtZone2(layer2Offset, layer2);

    --this._vol0StatFreebigblocks;
    return result;
  }

  private byte[] AllocBtreeNode(out long offp) {
    var node = this.AllocBlockmap(ZoneBtree, NodeOndiskSize, out offp);
    return node;
  }

  private byte[] AllocMetaElement(int dataLen, out long offp) {
    var data = this.AllocBlockmap(ZoneMeta, dataLen, out offp);
    return data;
  }

  // alloc_blockmap: simplified iterator using next_offset (blockmap.c).
  private byte[] AllocBlockmap(int zone, int bytes, out long resultOff) {
    var freemapPhys = this._blockmap[ZoneFreemap].PhysOffset;
    var blockmap = this._blockmap[zone];
    bytes = (int)DataDoalign(bytes);

    while (true) {
      var tmp = blockmap.NextOffset + bytes - 1;
      if (((blockmap.NextOffset ^ tmp) & ~BufMask64) != 0)
        blockmap.NextOffset = tmp & ~BufMask64;

      var layer1Offset = freemapPhys + Layer1Offset(blockmap.NextOffset);
      var layer1 = this.ReadZone2(layer1Offset, Layer1EntrySize);
      var layer1Phys = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(8, 8));

      var layer2Offset = layer1Phys + Layer2Offset(blockmap.NextOffset);
      var layer2 = this.ReadZone2(layer2Offset, Layer2EntrySize);
      var l2zone = layer2[0];

      if (l2zone == ZoneUnavail)
        throw new InvalidOperationException("HAMMER alloc_blockmap: layer2 ran out of space");

      if (l2zone == 0) {
        var bf = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(0, 8)) - 1;
        BinaryPrimitives.WriteUInt64LittleEndian(layer1.AsSpan(0, 8), (ulong)bf);
        this.SetLayer1Crc(layer1);
        this.WriteAtZone2(layer1Offset, layer1);
        layer2[0] = (byte)zone;
        l2zone = (byte)zone;
        --this._vol0StatFreebigblocks;
      }
      if (l2zone != zone) {
        blockmap.NextOffset = ZoneLayer2NextOffset(blockmap.NextOffset);
        continue;
      }

      var bytesFree = BinaryPrimitives.ReadInt32LittleEndian(layer2.AsSpan(8, 4)) - bytes;
      BinaryPrimitives.WriteInt32LittleEndian(layer2.AsSpan(8, 4), bytesFree);
      resultOff = blockmap.NextOffset;
      blockmap.NextOffset += bytes;
      BinaryPrimitives.WriteUInt32LittleEndian(layer2.AsSpan(4, 4),
        (uint)(blockmap.NextOffset & BigblockMask64));
      this.SetLayer2Crc(layer2);
      this.WriteAtZone2(layer2Offset, layer2);

      return new byte[bytes]; // zero-filled element; caller fills and writes it back.
    }
  }

  // ===== CRC helpers =====
  private void SetLayer1Crc(byte[] e) =>
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28, 4), HammerCrc.DataCrc(VolVersion, e.AsSpan(0, 28)));

  private void SetLayer2Crc(byte[] e) =>
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12, 4), HammerCrc.DataCrc(VolVersion, e.AsSpan(0, 12)));

  private void SetBlockmapCrc(byte[] e) =>
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(36, 4), HammerCrc.DataCrc(VolVersion, e.AsSpan(0, 36)));

  private void SetFifoHeadCrc(byte[] rec, int bytes) {
    // hammer_fifo_head layout: hdr_signature(2)@0, hdr_type(2)@2, hdr_size(4)@4,
    // hdr_seq(4)@8, hdr_crc(4)@12. HAMMER_FIFO_HEAD_CRCOFF = offsetof(hdr_crc) = 12.
    // hammer_crc_get_fifo_head = datacrc(head, 12) ^ datacrc(head+1, bytes - sizeof(*head)).
    // sizeof(struct hammer_fifo_head) == 16, so the second chunk is [16, bytes).
    var crc = HammerCrc.DataCrc(VolVersion, rec.AsSpan(0, 12))
            ^ HammerCrc.DataCrc(VolVersion, rec.AsSpan(16, bytes - 16));
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12, 4), crc);
  }

  private void SetBtreeCrc(byte[] node) {
    // crc covers node+4 .. end (HAMMER_BTREE_CRCSIZE = sizeof(node) - sizeof(crc)).
    var crc = HammerCrc.DataCrc(VolVersion, node.AsSpan(4, NodeOndiskSize - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(0, 4), crc);
  }

  private uint LeafDataCrc(ushort recType, byte[] data, int dataLen) {
    if (dataLen == 0) return 0;
    return recType == RectypeInode
      ? HammerCrc.DataCrc(VolVersion, data.AsSpan(0, InodeCrcsize))
      : HammerCrc.DataCrc(VolVersion, data.AsSpan(0, dataLen));
  }

  // ===== offset/zone encode helpers =====
  private static long ZoneEncode(int zone, long off) => (long)(((ulong)zone << 60) | ((ulong)off & ~OffZoneMask));
  private static long Encode(int zone, int volNo, long off) =>
    (long)(((ulong)zone << 60) | (((ulong)(volNo & 255)) << 52) | ((ulong)off & OffShortMask));
  private static long EncodeRawBuffer(int volNo, long off) => Encode(ZoneRawBuffer, volNo, off);
  private static long EncodeUndo(long off) => Encode(ZoneUndo, RootVolno, off);
  private static int ZoneDecode(long off) => (int)((ulong)off >> 60);

  private long ZoneXToZone2(long off) =>
    (long)(((ulong)ZoneRawBuffer << 60) | ((ulong)off & ~OffZoneMask));

  private static long Layer1Offset(long zone2) {
    var idx = (int)(((ulong)(zone2 & BlockmapLayer1Mask)) / BlockmapLayer2);
    return idx * (long)Layer1EntrySize;
  }

  private static long Layer2Offset(long zone2) {
    var idx = (int)(((ulong)(zone2 & BlockmapLayer2Mask)) / (ulong)BigblockSize);
    return idx * (long)Layer2EntrySize;
  }

  private static long BlockmapLayer2Doalign(long off) => (off + BlockmapLayer2Mask) & ~BlockmapLayer2Mask;
  private static long BigblockDoalign(long off) => (off + BigblockMask64) & ~BigblockMask64;
  private static long ZoneLayer2NextOffset(long off) => (off + BigblockSize) & ~BigblockMask64;
  private static long DataDoalign(long off) => (off + 15) & ~15;
  private static long AlignBuf(long off) => (off + BufMask64) & ~BufMask64;
  private static long AlignBufDown(long off) => off & ~BufMask64;

  // zone-2 -> physical device offset: vol_buf_beg + (zone2 & OFF_SHORT_MASK).
  private long Zone2ToPhys(long zone2) => this._volBufBeg + (long)((ulong)zone2 & OffShortMask);

  // zone-3 (undo) -> physical via the undo array.
  private long UndoToPhys(long zone3) {
    var index = (int)((long)((ulong)zone3 & OffShortMask) / BigblockSize);
    return this._undoArray[index] is var z2 && z2 != BlockmapUnavailL
      ? this.Zone2ToPhys(z2) + (zone3 & BigblockMask64)
      : throw new InvalidOperationException("HAMMER undo index out of range");
  }

  private ulong CreateTid() => this._vol0NextTid++;
  private static ulong NowTimeMicros() =>
    (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);

  // ===== sparse buffer-cache I/O =====
  private byte[] BufferAt(long physOffset) {
    var bufStart = physOffset & ~BufMask64;
    if (!this._buffers.TryGetValue(bufStart, out var buf)) {
      buf = new byte[Bufsize];
      this._buffers[bufStart] = buf;
    }
    return buf;
  }

  private void WriteAtPhys(long physOffset, ReadOnlySpan<byte> data) {
    var bufStart = physOffset & ~BufMask64;
    var inBuf = (int)(physOffset - bufStart);
    if (inBuf + data.Length > Bufsize)
      throw new InvalidOperationException("HAMMER write crosses a 16 KB buffer boundary");
    data.CopyTo(this.BufferAt(physOffset).AsSpan(inBuf, data.Length));
  }

  private void WriteAtZone2(long zone2, byte[] data) => this.WriteAtZone2(zone2, data, data.Length);
  private void WriteAtZone2(long zone2, byte[] data, int len) =>
    this.WriteAtPhys(this.Zone2ToPhys(zone2), data.AsSpan(0, len));

  private byte[] ReadZone2(long zone2, int len) {
    var phys = this.Zone2ToPhys(zone2);
    var bufStart = phys & ~BufMask64;
    var inBuf = (int)(phys - bufStart);
    var result = new byte[len];
    this.BufferAt(phys).AsSpan(inBuf, len).CopyTo(result);
    return result;
  }

  // ===== final image emission =====
  private void WriteImage(Stream output, long size) {
    // Build the 16 KB volume header buffer at device offset 0.
    var hdr = new byte[Bufsize];
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0, 8), VolSignature);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(8, 8), this._volBotBeg);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(16, 8), this._volMemBeg);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(24, 8), this._volBufBeg);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(32, 8), this._volBufEnd);
    // vol_fsid: unique per-filesystem UUID; the PFS#0 shared/unique uuids match it.
    this._volFsid.CopyTo(hdr.AsSpan(48, 16));
    FsTypeUuid.CopyTo(hdr.AsSpan(64, 16));
    Encoding.ASCII.GetBytes(Truncate(this._label, 63)).CopyTo(hdr.AsSpan(80));
    BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(144, 4), RootVolno);    // vol_no
    BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(148, 4), 1);            // vol_count
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(152, 4), VolVersion);  // vol_version
    // vol_crc @156 left 0 — newfs_hammer leaves it 0 and the kernel does not check it.
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(164, 4), RootVolno);   // vol_rootvol
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(200, 8), this._vol0StatBigblocks);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(208, 8), this._vol0StatFreebigblocks);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(224, 8), this._vol0StatInodes);
    BinaryPrimitives.WriteInt64LittleEndian(hdr.AsSpan(240, 8), this._vol0BtreeRoot);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(248, 8), this._vol0NextTid);

    // vol0_blockmap[16] @264, each entry 40 bytes.
    for (var z = 0; z < MaxZones; ++z) {
      var bm = this._blockmap[z];
      var off = 264 + z * 40;
      if (bm != null) {
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(off + 0, 8), (ulong)bm.PhysOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(off + 8, 8), (ulong)bm.FirstOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(off + 16, 8), (ulong)bm.NextOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(off + 24, 8), (ulong)bm.AllocOffset);
      }
      // entry_crc @ +36 over the first 36 bytes.
      var entry = hdr.AsSpan(off, 40).ToArray();
      this.SetBlockmapCrc(entry);
      entry.CopyTo(hdr.AsSpan(off, 40));
    }

    // vol0_undo_array[128] @904, each 8 bytes.
    for (var n = 0; n < MaxUndoBigblocks; ++n)
      BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(904 + n * 8, 8), (ulong)this._undoArray[n]);

    // The volume header occupies the first 16 KB buffer; merge any allocations
    // the formatter placed inside the header-junk region (none below 1928 bytes).
    this._buffers[0] = hdr;

    // Emit: write every cached buffer at its device offset; grow file to `size`.
    output.SetLength(size);
    foreach (var (offset, buf) in this._buffers) {
      output.Seek(offset, SeekOrigin.Begin);
      output.Write(buf, 0, buf.Length);
    }
    output.Seek(size, SeekOrigin.Begin);
    output.Flush();
  }

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
