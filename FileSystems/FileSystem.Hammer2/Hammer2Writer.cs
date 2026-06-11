using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hammer2;

/// <summary>
/// Writes a single-volume HAMMER2 (DragonFly BSD) filesystem image that
/// DragonFly recognises and mounts. The output is a faithful port of what
/// <c>newfs_hammer2(8)</c> (<c>sbin/newfs_hammer2</c> together with the
/// on-disk helpers in <c>sbin/hammer2/cmd_setcomp.c</c> and the kernel's
/// <c>sys/vfs/hammer2/hammer2_disk.h</c>) lays down for a fresh, empty
/// filesystem:
///
/// <list type="bullet">
///   <item><description>the volume header (<c>struct hammer2_volume_data</c>) at
///   byte offset 0 — magic, version, the boot/aux geometry, the allocator
///   accounting, and the super-root blockset, each 512-byte sector protected by
///   an iCRC and the whole 64 KB header by <c>icrc_volheader</c>;</description></item>
///   <item><description>the super-root inode (<c>HAMMER2_PFSTYPE_SUPROOT</c>)
///   whose embedded blockset references one or more PFS inodes;</description></item>
///   <item><description>two PFS inodes — the default <c>"LOCAL"</c> plus the
///   labelled PFS (e.g. <c>"DATA"</c>) — each a master with an empty root
///   directory.</description></item>
/// </list>
///
/// <para>The on-disk topology mirrors <c>newfs_hammer2</c> exactly: the volume
/// header lives in the first of four redundant 64 KB slots (only slot #0 is
/// populated by newfs, the kernel rolls the others forward on the first sync),
/// the boot area starts at 4 MB, the aux area at 12 MB, and the reserved
/// topology area (where the inodes live) begins at 20 MB
/// (<c>allocator_beg</c>). HAMMER2 builds its freemap lazily, so a freshly
/// formatted volume carries no freemap blocks at all — exactly what
/// <c>newfs_hammer2</c> writes.</para>
///
/// <para>The root directories are created empty (exactly as
/// <c>newfs_hammer2</c> leaves them). File payloads passed to
/// <see cref="AddFile"/> are recorded for the label surface but are not yet
/// materialised as directory entries — adding real directory entries requires
/// laying down child inodes plus the DIRENT blockrefs and is out of scope for
/// this writer.</para>
/// </summary>
public sealed class Hammer2Writer {
  // ===== Constants from sys/vfs/hammer2/hammer2_disk.h =====
  private const ulong VolumeIdHbo = 0x48414d3205172011UL;           // HAMMER2_VOLUME_ID_HBO
  private const int VolumeBytes = 65536;                            // HAMMER2_VOLUME_BYTES / PBUFSIZE
  private const int NumVolhdrs = 4;                                 // HAMMER2_NUM_VOLHDRS
  private const int MaxVolumes = 64;                                // HAMMER2_MAX_VOLUMES
  private const uint VolVersionDefault = 2;                         // HAMMER2_VOL_VERSION_MULTI_VOLUMES
  private const int BlockrefBytes = 128;                            // HAMMER2_BLOCKREF_BYTES
  private const int SetCount = 4;                                   // HAMMER2_SET_COUNT
  private const int InodeBytes = 1024;                              // HAMMER2_INODE_BYTES
  private const int RadixForInode = 10;                            // log2(1024)

  // newfs_hammer2 geometry (8 MB-aligned): boot 8 MB @ 4 MB, aux 8 MB @ 12 MB,
  // topo-reserved 4 MB @ 20 MB. allocator_beg == the topo area = 20 MB.
  private const long BootBeg = 4L * 1024 * 1024;
  private const long BootSize = 8L * 1024 * 1024;
  private const long AuxSize = 8L * 1024 * 1024;
  private const long TopoReserved = 4L * 1024 * 1024;

  // blockref check methods: HAMMER2_ENC_CHECK(n) = (n & 15) << 4.
  private const int CheckXxhash64 = 3;                             // HAMMER2_CHECK_XXHASH64
  private const int CompNone = 0;                                  // HAMMER2_COMP_NONE
  private const int CompAutozero = 1;                             // HAMMER2_COMP_AUTOZERO
  private const int CompLz4 = 2;                                  // HAMMER2_COMP_LZ4

  private const byte BrefTypeInode = 1;                          // HAMMER2_BREF_TYPE_INODE
  private const byte BrefFlagPfsroot = 0x01;                     // HAMMER2_BREF_FLAG_PFSROOT

  private const byte PfsTypeMaster = 6;                          // HAMMER2_PFSTYPE_MASTER
  private const byte PfsTypeSuproot = 8;                         // HAMMER2_PFSTYPE_SUPROOT
  private const ushort InodeVersionOne = 1;                       // HAMMER2_INODE_VERSION_ONE
  private const byte OpflagPfsroot = 0x02;                       // HAMMER2_OPFLAG_PFSROOT
  private const long PfsRootInum = 16;                           // pfs_inum newfs_hammer2 assigns a fresh PFS root

  private const long VolumeAlign = 8L * 1024 * 1024;             // HAMMER2_VOLUME_ALIGN

  // The fixed HAMMER2 filesystem-type UUID "d19abb5c-2d86-11dc-a94d-01301bb8a9f5",
  // serialised in DragonFly uuid_t order (time_low/mid/hi little-endian, then
  // clock_seq + node as raw bytes).
  private static readonly byte[] FsTypeUuid =
    [0xd1, 0x9a, 0xbb, 0x5c, 0x2d, 0x86, 0xdc, 0x11, 0xa9, 0x4d, 0x01, 0x30, 0x1b, 0xb8, 0xa9, 0xf5];

  private readonly List<(string Name, byte[] Content)> _files = [];
  private string _label = "DATA";
  private long _volumeSize = 256L * 1024 * 1024;                  // newfs_hammer2 minimum-ish

  /// <summary>Filesystem PFS label (max 63 chars). Mirrors <c>newfs_hammer2 -L</c>.</summary>
  public string Label {
    get => this._label;
    set => this._label = string.IsNullOrEmpty(value) ? "DATA" : value;
  }

  /// <summary>
  /// Total volume size in bytes. Forced up to the HAMMER2 minimum so the boot,
  /// aux and topology-reserved areas fit, and aligned down to the 8 MB volume
  /// alignment internally.
  /// </summary>
  public long VolumeSize {
    get => this._volumeSize;
    set => this._volumeSize = value;
  }

  /// <summary>
  /// Records a file for inclusion. This writer stores the entry for the label
  /// surface; directory-entry materialisation is not yet implemented (the root
  /// directory is created empty, exactly as <c>newfs_hammer2</c> leaves it).
  /// </summary>
  public void AddFile(string name, byte[] content) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    this._files.Add((name, content ?? []));
  }

  // ===== layout state =====
  private long _voluSize;
  private long _allocatorBeg;        // topo area start (where the inodes live)
  private long _allocatorSize;       // free space size newfs reports
  private byte[] _fsid = new byte[16];

  // A single 1 MB scratch buffer holding the topology-reserved inode blocks,
  // written at device offset _allocatorBeg.
  private byte[] _topo = [];

  private ulong _mirrorTid = 0x10;   // newfs stamps mirror_tid/freemap_tid = 0x10

  /// <summary>Formats the HAMMER2 volume and writes it to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Force a sane minimum and align down to the 8 MB volume alignment.
    var minimum = BootBeg + BootSize + AuxSize + TopoReserved + VolumeAlign; // ≈ 32 MB
    var size = AlignDown(Math.Max(this._volumeSize, minimum), VolumeAlign);

    this._voluSize = size;
    this._allocatorBeg = BootBeg + BootSize + AuxSize;             // 20 MB
    // newfs: allocator_size = free space below the volume end; allocator_beg
    // accounts for the boot+aux+topo reserved region.
    this._allocatorSize = size - (this._allocatorBeg + TopoReserved);
    this._fsid = Guid.NewGuid().ToByteArray();

    // Build the topology area: super-root inode + the PFS inodes, then the
    // volume header that points at the super-root.
    var sroot = this.BuildTopology(out var srootCheck, out var srootDataOff);

    output.SetLength(size);

    // Topology-reserved area at allocator_beg.
    output.Seek(this._allocatorBeg, SeekOrigin.Begin);
    output.Write(this._topo, 0, this._topo.Length);

    // Volume header in slot #0 (the other three slots stay zero, as newfs leaves them).
    var vh = this.BuildVolumeHeader(srootCheck, srootDataOff);
    output.Seek(0, SeekOrigin.Begin);
    output.Write(vh, 0, vh.Length);

    output.Seek(size, SeekOrigin.Begin);
    output.Flush();
    _ = sroot;
  }

  // ---- topology: SUPROOT inode + PFS inodes (mirrors newfs_hammer2) ----
  private byte[] BuildTopology(out byte[] srootCheck, out long srootDataOff) {
    // The reserved area is 4 MB; we only touch the first few 1 KB inode slots.
    this._topo = new byte[TopoReserved];

    // Inode slot offsets within the topo area (1 KB granularity), mirroring newfs:
    //   slot 0 -> SUPROOT, slot 1 -> first PFS, slot 2 -> second PFS, ...
    const int SrootSlot = 0;

    // The PFS list: the default "LOCAL" plus the user-labelled PFS. newfs orders
    // them so the embedded blockset is sorted ascending by name_key; we lay the
    // inodes out and then sort the blockrefs.
    var pfsNames = new List<string> { "LOCAL", Truncate(this._label, 63) };

    // Lay PFS inodes into slots 1..n.
    var children = new List<(ulong Key, byte[] Inode, long DataOff, int Slot)>();
    for (var i = 0; i < pfsNames.Count; ++i) {
      var slot = 1 + i;
      var name = pfsNames[i];
      var inode = this.BuildPfsInode(name, out var key);
      var dataOff = EncodeDataOff(this._allocatorBeg + (long)slot * InodeBytes, RadixForInode);
      children.Add((key, inode, dataOff, slot));
    }

    // Build the super-root inode with its embedded blockset referencing the PFS
    // inodes, sorted ascending by name_key (newfs invariant for the chain).
    children.Sort((a, b) => a.Key.CompareTo(b.Key));

    var srootInode = this.BuildSuprootInode(children);

    // Write the SUPROOT inode and compute its check (XXHASH64 over the 1 KB inode).
    Place(this._topo, SrootSlot * InodeBytes, srootInode);
    srootCheck = ComputeXxCheck(srootInode);
    srootDataOff = EncodeDataOff(this._allocatorBeg + (long)SrootSlot * InodeBytes, RadixForInode);

    // Write the PFS inodes.
    foreach (var c in children)
      Place(this._topo, c.Slot * InodeBytes, c.Inode);

    return srootInode;
  }

  // ---- super-root inode (HAMMER2_PFSTYPE_SUPROOT) ----
  private byte[] BuildSuprootInode(List<(ulong Key, byte[] Inode, long DataOff, int Slot)> children) {
    var inode = new byte[InodeBytes];
    var now = NowMicros();

    // meta region (struct hammer2_inode_meta, first 256 bytes).
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x00, 2), InodeVersionOne);   // version
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x10, 8), now);               // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x18, 8), now);               // mtime
    // atime stays 0 (newfs leaves it 0).
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x28, 8), now);               // btime
    inode[0x50] = BrefTypeInode;                                                         // type = INODE
    inode[0x51] = 0;                                                                     // op_flags
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x52, 2), 0);                  // cap_flags
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x54, 4), 0x1C0);              // mode 0700
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x58, 8), 0);                  // inum = 0
    BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(0x60, 8), 0);                   // size
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x68, 8), 2);                  // nlinks = 2
    // name_key (0x78) stays 0 for the super-root.
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x80, 2), 7);                  // name_len "SUPROOT"
    inode[0x83] = CompAutozero;                                                          // comp_algo (matches newfs)
    inode[0x85] = CheckXxhash64;                                                         // check_algo
    inode[0x87] = PfsTypeSuproot;                                                        // pfs_type = SUPROOT
    // pfs_clid (0x90) and pfs_fsid (0xA0): newfs assigns distinct UUIDs.
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0x90, 16));                         // pfs_clid
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0xA0, 16));                         // pfs_fsid

    Encoding.ASCII.GetBytes("SUPROOT").CopyTo(inode.AsSpan(0x100));                      // filename[]

    // Embedded blockset (u union @0x200): one blockref per PFS, comp=NONE,
    // check=XXHASH64, PFSROOT flag, vradix=10, key=name_key, sorted ascending.
    for (var i = 0; i < children.Count && i < SetCount; ++i) {
      var c = children[i];
      var brOff = 0x200 + i * BlockrefBytes;
      this.WriteBlockref(inode.AsSpan(brOff, BlockrefBytes), BrefTypeInode,
        checkAlgo: CheckXxhash64, compAlgo: CompNone, flags: BrefFlagPfsroot,
        key: c.Key, vradix: RadixForInode, dataOff: c.DataOff,
        check: ComputeXxCheck(c.Inode));
    }

    return inode;
  }

  // ---- PFS inode (HAMMER2_PFSTYPE_MASTER, empty root directory) ----
  private byte[] BuildPfsInode(string name, out ulong nameKey) {
    var inode = new byte[InodeBytes];
    var now = NowMicros();
    var nmeBytes = Encoding.ASCII.GetBytes(name);
    nameKey = Hammer2Crc.DirHash(nmeBytes);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x00, 2), InodeVersionOne);   // version
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x10, 8), now);               // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x18, 8), now);               // mtime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x28, 8), now);               // btime
    inode[0x50] = BrefTypeInode;                                                         // type = INODE
    inode[0x51] = OpflagPfsroot;                                                         // op_flags = PFSROOT
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x54, 4), 0x1ED);              // mode 0755
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x58, 8), 1);                  // inum = 1
    BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(0x60, 8), 0);                   // size
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x68, 8), 1);                  // nlinks = 1
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x78, 8), nameKey);            // name_key
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x80, 2), (ushort)nmeBytes.Length); // name_len
    inode[0x83] = CompLz4;                                                               // comp_algo = LZ4 (newfs default)
    inode[0x85] = CheckXxhash64;                                                         // check_algo
    inode[0x87] = PfsTypeMaster;                                                         // pfs_type = MASTER
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x88, 8), (ulong)PfsRootInum); // pfs_inum (newfs writes 16)
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0x90, 16));                         // pfs_clid
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0xA0, 16));                         // pfs_fsid

    nmeBytes.CopyTo(inode.AsSpan(0x100));                                                // filename[]
    // Embedded blockset (u @0x200) stays all-zero: an empty root directory.
    return inode;
  }

  // ---- volume header (struct hammer2_volume_data) ----
  private byte[] BuildVolumeHeader(byte[] srootCheck, long srootDataOff) {
    var vh = new byte[VolumeBytes];

    BinaryPrimitives.WriteUInt64LittleEndian(vh.AsSpan(0x00, 8), VolumeIdHbo);           // magic
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x08, 8), BootBeg);                // boot_beg
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x10, 8), BootBeg + BootSize);     // boot_end
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x18, 8), BootBeg + BootSize);     // aux_beg
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x20, 8), BootBeg + BootSize + AuxSize); // aux_end
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x28, 8), this._voluSize);         // volu_size
    BinaryPrimitives.WriteUInt32LittleEndian(vh.AsSpan(0x30, 4), VolVersionDefault);     // version
    BinaryPrimitives.WriteUInt32LittleEndian(vh.AsSpan(0x34, 4), 0);                     // flags
    vh[0x38] = 0;                                                                        // copyid
    vh[0x39] = 0;                                                                        // freemap_version
    vh[0x3A] = 3;                                                                        // peer_type (newfs writes 3)
    vh[0x3B] = 0;                                                                        // volu_id
    vh[0x3C] = 1;                                                                        // nvolumes
    this._fsid.CopyTo(vh.AsSpan(0x40, 16));                                              // fsid
    FsTypeUuid.CopyTo(vh.AsSpan(0x50, 16));                                              // fstype
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x60, 8), this._allocatorSize);    // allocator_size
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x68, 8), this._allocatorSize);    // allocator_free
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x70, 8), this._allocatorBeg);     // allocator_beg
    BinaryPrimitives.WriteUInt64LittleEndian(vh.AsSpan(0x78, 8), this._mirrorTid);       // mirror_tid
    BinaryPrimitives.WriteUInt64LittleEndian(vh.AsSpan(0x90, 8), this._mirrorTid);       // freemap_tid
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0xC0, 8), this._voluSize);         // total_size

    // sroot_blockset @0x200 (sector #1): blockref[0] -> the super-root inode.
    this.WriteBlockref(vh.AsSpan(0x200, BlockrefBytes), BrefTypeInode,
      checkAlgo: CheckXxhash64, compAlgo: CompAutozero, flags: 0,
      key: 0, vradix: RadixForInode, dataOff: srootDataOff, check: srootCheck);

    // freemap_blockset @0x800 stays all-zero — HAMMER2 builds the freemap lazily.

    // volu_loff[HAMMER2_MAX_VOLUMES] @0x0E00 (sector #7): the per-volume device
    // offset table. Volume 0 lives at offset 0; every unused slot must be -1 or
    // the kernel's hammer2_ondisk check rejects the mount.
    BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x0E00, 8), 0);
    for (var i = 1; i < MaxVolumes; ++i)
      BinaryPrimitives.WriteInt64LittleEndian(vh.AsSpan(0x0E00 + i * 8, 8), -1L);

    // iCRCs.  icrc_sects[7] (SECT0) over [0, 508); icrc_sects[6] (SECT1) over
    // [512, 1024).  The array sits at 0x1E0, entry n at 0x1E0 + n*4.  SECT1 must
    // be stamped first: SECT0's [0, 508) range covers the SECT1 field at 0x1F8
    // (but stops just before the SECT0 field at 0x1FC).
    var sect1 = Hammer2Crc.Iscsi32(vh.AsSpan(512, 512));
    BinaryPrimitives.WriteUInt32LittleEndian(vh.AsSpan(0x1E0 + 6 * 4, 4), sect1);
    var sect0 = Hammer2Crc.Iscsi32(vh.AsSpan(0, 512 - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(vh.AsSpan(0x1E0 + 7 * 4, 4), sect0);

    // icrc_volheader @0xFFFC over [0, 65536-4), computed last (covers the
    // icrc_sects we just wrote).
    var vhc = Hammer2Crc.Iscsi32(vh.AsSpan(0, VolumeBytes - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(vh.AsSpan(VolumeBytes - 4, 4), vhc);

    return vh;
  }

  // ---- a single 128-byte blockref ----
  private void WriteBlockref(Span<byte> br, byte type, int checkAlgo, int compAlgo,
                             byte flags, ulong key, int vradix, long dataOff,
                             ReadOnlySpan<byte> check) {
    br[0] = type;                                                            // type
    br[1] = (byte)(((checkAlgo & 15) << 4) | (compAlgo & 15));               // methods
    br[2] = 0xFF;                                                            // copyid (newfs uses 0xFF)
    br[3] = 0;                                                               // keybits
    br[4] = (byte)vradix;                                                    // vradix
    br[5] = flags;                                                           // flags
    BinaryPrimitives.WriteUInt16LittleEndian(br.Slice(6, 2), 0);             // leaf_count
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(8, 8), key);           // key
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(16, 8), this._mirrorTid); // mirror_tid
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(24, 8), 0);            // modify_tid
    BinaryPrimitives.WriteInt64LittleEndian(br.Slice(32, 8), dataOff);       // data_off (radix in low bits)
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(40, 8), 0);            // update_tid
    // embed @0x30 stays zero (stats: data_count/inode_count = 0).
    // check union @0x40: ISCSI32/XXHASH64 value goes in the first 8 bytes.
    check.CopyTo(br.Slice(64, check.Length));
  }

  // ===== helpers =====

  // data_off encodes the device byte offset with the allocation radix in the
  // low HAMMER2_OFF_MASK_RADIX (6) bits.
  private static long EncodeDataOff(long deviceOffset, int radix) =>
    (deviceOffset & ~0x3FL) | (long)(uint)(radix & 0x3F);

  private static byte[] ComputeXxCheck(byte[] data) {
    var h = Hammer2Crc.XxHash64(data, Hammer2Crc.Hammer2Seed);
    var b = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, h);
    return b;
  }

  private static void Place(byte[] dst, int offset, byte[] src) =>
    src.CopyTo(dst.AsSpan(offset, src.Length));

  private static long AlignDown(long v, long align) => v & ~(align - 1);

  private static ulong NowMicros() =>
    (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
