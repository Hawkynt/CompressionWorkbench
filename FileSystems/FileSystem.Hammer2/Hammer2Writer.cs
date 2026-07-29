using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

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
/// <para>Files passed to <see cref="AddFile"/> are materialised in the labelled
/// PFS root exactly the way the DragonFly kernel lays them down: each file gets
/// a regular-file inode (<c>HAMMER2_OBJTYPE_REGFILE</c>) keyed in the root
/// blockset by its inode number, plus a <c>HAMMER2_BREF_TYPE_DIRENT</c> blockref
/// keyed by <c>hammer2_dirhash(name)</c> carrying the filename inline. Payloads
/// up to 512 bytes are embedded directly in the inode's union
/// (<c>HAMMER2_OPFLAG_DIRECTDATA</c>); larger payloads are written to an
/// allocated <c>HAMMER2_BREF_TYPE_DATA</c> block sized to the next power-of-two
/// logical buffer. When the root's four embedded blockrefs overflow they spill
/// into a <c>HAMMER2_BREF_TYPE_INDIRECT</c> block. All data blocks are stored
/// uncompressed (<c>HAMMER2_COMP_NONE</c>) and protected by an xxHash64 check.</para>
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

  private const byte BrefTypeInode = 1;                          // HAMMER2_BREF_TYPE_INODE
  private const byte BrefTypeIndirect = 2;                       // HAMMER2_BREF_TYPE_INDIRECT
  private const byte BrefTypeData = 3;                           // HAMMER2_BREF_TYPE_DATA
  private const byte BrefTypeDirent = 4;                         // HAMMER2_BREF_TYPE_DIRENT
  private const byte BrefFlagPfsroot = 0x01;                     // HAMMER2_BREF_FLAG_PFSROOT

  private const byte ObjTypeDirectory = 1;                       // HAMMER2_OBJTYPE_DIRECTORY
  private const byte ObjTypeRegfile = 2;                         // HAMMER2_OBJTYPE_REGFILE
  private const byte OpflagDirectData = 0x01;                    // HAMMER2_OPFLAG_DIRECTDATA

  private const int EmbeddedDataMax = 512;                       // inode union HAMMER2_EMBEDDED_BYTES
  private const int DataBlockRadix = 16;                         // HAMMER2_PBUFSIZE — largest data block
  private const int DataBlockBytes = 1 << DataBlockRadix;        // 64 KB
  private const int IndRadix = 12;                               // HAMMER2_IND_BYTES = 4 KB
  private const int IndFanout = (1 << IndRadix) / BlockrefBytes; // 32 blockrefs per indirect
  private const long FirstInum = 0x400;                          // kernel's first allocated inode number

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

  private readonly List<(string Name, FilePayload Payload)> _files = [];
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
  /// Records a regular file to materialise in the labelled PFS root: an inode
  /// holding the content (embedded when ≤512 bytes, otherwise in an allocated
  /// data block) plus a directory entry carrying <paramref name="name"/>.
  /// </summary>
  public void AddFile(string name, byte[] content) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    this._files.Add((name, FilePayload.FromBytes(content ?? [])));
  }

  /// <summary>
  /// Records a regular file whose bytes are pulled from <paramref name="openStream" />
  /// as the volume is written. Only the length is needed to lay out the blockrefs, so
  /// the file never has to be resident.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  // ===== layout state =====
  private long _voluSize;
  private long _allocatorBeg;        // topo area start (where the inodes live)
  private long _allocatorSize;       // free space size newfs reports
  private byte[] _fsid = new byte[16];

  // The topology scratch buffer, based at device offset _allocatorBeg. It holds
  // every block we lay down — inodes, dirent-bearing indirect blocks, and file
  // data blocks — and grows as the bump allocator hands out space.
  private byte[] _topo = [];
  private long _bump;                // next free byte within _topo
  private long _topoReserved = TopoReserved;

  // File payloads live past the topology area, in the general allocator space,
  // and are written straight to the output as they are laid out — a data block
  // never passes through _topo, so the volume is not bounded by an array.
  private long _dataBump;
  private Stream? _sink;

  private ulong _mirrorTid = 0x10;   // newfs stamps mirror_tid/freemap_tid = 0x10

  /// <summary>Formats the HAMMER2 volume and writes it to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    this._allocatorBeg = BootBeg + BootSize + AuxSize;             // topo area start
    this._fsid = Guid.NewGuid().ToByteArray();
    this._topoReserved = this.ComputeTopoReserve();
    this._dataBump = this._allocatorBeg + this._topoReserved;
    this._sink = output;

    // Build the topology area: super-root inode + the PFS inodes (with the
    // labelled PFS root populated), then the volume header that points at the
    // super-root. BuildTopology fills _topo and advances _bump.
    var srootDataOff = this.BuildTopology(out var srootCheck);

    // Size the volume to cover everything the bump allocator handed out, keep at
    // least the historical minimum, and align down to the volume alignment.
    var topoEnd = Math.Max(this._allocatorBeg + this._bump, this._dataBump);
    var minimum = this._allocatorBeg + this._topoReserved + VolumeAlign;
    var size = AlignUp(Math.Max(Math.Max(this._volumeSize, minimum), topoEnd + VolumeAlign), VolumeAlign);

    this._voluSize = size;
    this._allocatorSize = size - (this._allocatorBeg + this._topoReserved);

    output.SetLength(size);

    // Topology area at allocator_beg.
    output.Seek(this._allocatorBeg, SeekOrigin.Begin);
    output.Write(this._topo, 0, this._topo.Length);

    // Volume header in slot #0 (the other three slots stay zero, as newfs leaves them).
    var vh = this.BuildVolumeHeader(srootCheck, srootDataOff);
    output.Seek(0, SeekOrigin.Begin);
    output.Write(vh, 0, vh.Length);

    output.Seek(size, SeekOrigin.Begin);
    output.Flush();
    this._sink = null;
  }

  /// <summary>
  /// Sizes the topology-reserved area. Metadata scales with the payload: each
  /// 64 KB data block needs a 128-byte blockref, and those blockrefs need their
  /// own indirect blocks, so a fixed 4 MB reserve only covers small volumes.
  /// </summary>
  private long ComputeTopoReserve() {
    var dataBlocks = 0L;
    foreach (var (_, payload) in this._files) {
      if (payload.Size <= EmbeddedDataMax) continue;
      dataBlocks += (payload.Size + DataBlockBytes - 1) / DataBlockBytes;
    }
    // Leaf blockrefs plus every interior level: the levels above a fanout-32
    // tree add about a thirtieth again, so doubling the leaf cost covers them
    // with room to spare.
    var needed = TopoReserved + dataBlocks * BlockrefBytes * 2;
    return AlignUp(Math.Max(TopoReserved, needed), 1L << 20);
  }

  // ---- bump allocator over the topology area ----------------------------------
  // Hands out 'size' bytes at the requested power-of-two radix alignment and
  // returns the encoded data_off (device offset | radix). Grows _topo as needed.
  private long Allocate(int radix, out long deviceOffset) {
    var size = 1L << radix;
    // Align the bump pointer up to the allocation size (HAMMER2 requires a block
    // to be naturally aligned to its radix).
    this._bump = AlignUp(this._bump, size);
    var offsetInTopo = this._bump;
    this._bump += size;
    if (this._bump > this._topoReserved)
      throw new InvalidOperationException(
        $"HAMMER2: topology needs {this._bump:N0} bytes but only {this._topoReserved:N0} are reserved.");
    EnsureCapacity(ref this._topo, this._bump);
    deviceOffset = this._allocatorBeg + offsetInTopo;
    return EncodeDataOff(deviceOffset, radix);
  }

  // ---- topology: SUPROOT inode + PFS inodes -----------------------------------
  private long BuildTopology(out byte[] srootCheck) {
    this._topo = new byte[Math.Min(this._topoReserved, 4L * 1024 * 1024)];
    // newfs reserves the first 0xc00 bytes of the topo area before its first
    // allocation; mirror that so our offsets resemble the kernel's.
    this._bump = 0xC00;

    var labelName = Truncate(this._label, 63);
    var children = new List<(ulong Key, long DataOff, byte[] Check)>();

    // "LOCAL" — an empty root directory (as newfs leaves it).
    var localInode = this.BuildPfsInode("LOCAL", brefs: null, out var localKey);
    var localOff = this.PlaceBlock(localInode, RadixForInode);
    children.Add((localKey, localOff, ComputeXxCheck(localInode)));

    // The labelled PFS — root directory populated with the user's files.
    var labelOff = this.MaterialiseLabelledRoot(labelName, out var labelKey, out var labelCheck);
    children.Add((labelKey, labelOff, labelCheck));

    // Super-root inode references the PFS inodes, sorted ascending by name_key.
    children.Sort((a, b) => a.Key.CompareTo(b.Key));
    var srootInode = this.BuildSuprootInode(children);
    var srootOff = this.PlaceBlock(srootInode, RadixForInode);
    srootCheck = ComputeXxCheck(srootInode);
    return srootOff;
  }

  // ---- materialise the labelled PFS root and its files ------------------------
  private long MaterialiseLabelledRoot(string label, out ulong nameKey, out byte[] check) {
    // The root blockset carries both inode brefs (key = inum) and dirent brefs
    // (key = dirhash). We gather every blockref, then sort by key and either
    // embed them in the inode (≤4) or spill into an indirect block.
    var brefs = new List<(ulong Key, byte[] Bref)>();

    var inum = (ulong)FirstInum;
    foreach (var (fileName, payload) in this._files) {
      var thisInum = inum++;

      // The file inode (objtype REGFILE). Its on-disk filename is the hex of the
      // inode number — exactly what the kernel writes; the human name lives only
      // in the dirent.
      var inode = this.BuildRegFileInode(thisInum, payload);
      var inodeOff = this.PlaceBlock(inode, RadixForInode);

      var inodeBref = new byte[BlockrefBytes];
      this.WriteBlockref(inodeBref, BrefTypeInode, checkAlgo: CheckXxhash64, compAlgo: CompNone,
        flags: 0, key: thisInum, vradix: 0, dataOff: inodeOff, check: ComputeXxCheck(inode));
      brefs.Add((thisInum, inodeBref));

      // The directory entry — name carried inline, pointing at the inode number.
      brefs.Add(this.BuildDirentBref(fileName, thisInum, ObjTypeRegfile));
    }

    var rootInode = this.BuildPfsInode(label, brefs, out nameKey);
    var off = this.PlaceBlock(rootInode, RadixForInode);
    check = ComputeXxCheck(rootInode);
    return off;
  }

  // ---- a regular-file inode (HAMMER2_OBJTYPE_REGFILE) -------------------------
  private byte[] BuildRegFileInode(ulong thisInum, FilePayload payload) {
    var inode = new byte[InodeBytes];
    var now = NowMicros();
    var name = "0x" + thisInum.ToString("x16");           // kernel's on-disk inode name
    var nameBytes = Encoding.ASCII.GetBytes(name);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x00, 2), InodeVersionOne);   // version
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x10, 8), now);               // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x18, 8), now);               // mtime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x28, 8), now);               // btime
    inode[0x50] = ObjTypeRegfile;                                                        // type = REGFILE
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x54, 4), 0x1A4);              // mode 0644
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x58, 8), thisInum);           // inum
    BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(0x60, 8), payload.Size);        // size
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x68, 8), 1);                  // nlinks = 1
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x70, 8), (ulong)PfsRootInum); // iparent = root
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x78, 8), thisInum);           // name_key = inum
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x80, 2), (ushort)nameBytes.Length); // name_len
    inode[0x83] = CompNone;                                                              // comp_algo = NONE
    inode[0x85] = CheckXxhash64;                                                         // check_algo
    nameBytes.CopyTo(inode.AsSpan(0x100));                                               // filename[]

    if (payload.Size <= EmbeddedDataMax) {
      // Direct data: the payload lives in the inode's 512-byte union.
      inode[0x51] = OpflagDirectData;                                                    // op_flags
      var embedded = payload.ToArray();
      embedded.CopyTo(inode.AsSpan(0x200, embedded.Length));
      return inode;
    }

    // Larger files are a run of HAMMER2_BREF_TYPE_DATA blocks, each keyed by its
    // logical offset in the file and capped at HAMMER2_PBUFSIZE. Four fit in the
    // inode's embedded blockset; past that they hang off indirect blocks.
    var brefs = new List<(ulong Key, byte[] Bref)>();
    var block = new byte[DataBlockBytes];
    using (var source = payload.Open()) {
      long logical = 0;
      while (logical < payload.Size) {
        var want = (int)Math.Min(DataBlockBytes, payload.Size - logical);
        var got = 0;
        while (got < want) {
          var n = source.Read(block, got, want - got);
          if (n <= 0) break;
          got += n;
        }
        if (got <= 0)
          throw new IOException($"HAMMER2: inode 0x{thisInum:x} ended after {logical:N0} of {payload.Size:N0} bytes.");

        // A block is exactly 1<<radix bytes and its check covers all of them, so
        // the tail of a short final block is zero-filled.
        var radix = Math.Max(DataRadix(got), 10);
        var span = 1 << radix;
        Array.Clear(block, got, span - got);
        var blockOff = this.WriteDataBlock(block.AsSpan(0, span), radix);

        var bref = new byte[BlockrefBytes];
        this.WriteBlockref(bref, BrefTypeData, checkAlgo: CheckXxhash64, compAlgo: CompNone,
          flags: 0, key: (ulong)logical, vradix: radix, dataOff: blockOff,
          check: ComputeXxCheck(block.AsSpan(0, span)));
        brefs.Add(((ulong)logical, bref));
        logical += got;
      }
    }

    this.LayoutBlockset(inode.AsSpan(0x200, SetCount * BlockrefBytes), brefs);
    return inode;
  }

  // ---- a directory-entry blockref (HAMMER2_BREF_TYPE_DIRENT) ------------------
  // The dirent head overlays the bref: inum at +0x30, name_len at +0x38, type at
  // +0x3A, and the filename inline starting at +0x40 (the check union) when it
  // fits in 64 bytes — which it always does here.
  private (ulong Key, byte[] Bref) BuildDirentBref(string fileName, ulong inum, byte objType) {
    var nameBytes = Encoding.ASCII.GetBytes(fileName);
    var key = Hammer2Crc.DirHash(nameBytes);

    var br = new byte[BlockrefBytes];
    br[0] = BrefTypeDirent;                                                  // type
    br[1] = (byte)(((CheckXxhash64 & 15) << 4) | (CompNone & 15));           // methods
    br[2] = 0xFF;                                                            // copyid
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(8, 8), key);          // key = dirhash
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(16, 8), this._mirrorTid); // mirror_tid
    // data_off stays 0: the name is carried inline (name_len ≤ 64).
    BinaryPrimitives.WriteUInt64LittleEndian(br.AsSpan(0x30, 8), inum);      // dirent.inum
    BinaryPrimitives.WriteUInt16LittleEndian(br.AsSpan(0x38, 2), (ushort)nameBytes.Length); // dirent.namlen
    br[0x3A] = objType;                                                      // dirent.type
    nameBytes.CopyTo(br.AsSpan(0x40, Math.Min(nameBytes.Length, 64)));       // inline name
    return (key, br);
  }

  // ---- super-root inode (HAMMER2_PFSTYPE_SUPROOT) ----
  private byte[] BuildSuprootInode(List<(ulong Key, long DataOff, byte[] Check)> children) {
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
        check: c.Check);
    }

    return inode;
  }

  // ---- PFS inode (HAMMER2_PFSTYPE_MASTER) ----
  // When <paramref name="brefs"/> is null the root directory is left empty (as
  // newfs leaves the "LOCAL" PFS); otherwise the supplied inode/dirent blockrefs
  // are laid into the embedded blockset, spilling into an indirect block when
  // there are more than four.
  private byte[] BuildPfsInode(string name, List<(ulong Key, byte[] Bref)>? brefs, out ulong nameKey) {
    var inode = new byte[InodeBytes];
    var now = NowMicros();
    var nmeBytes = Encoding.ASCII.GetBytes(name);
    nameKey = Hammer2Crc.DirHash(nmeBytes);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x00, 2), InodeVersionOne);   // version
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x10, 8), now);               // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x18, 8), now);               // mtime
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x28, 8), now);               // btime
    inode[0x50] = ObjTypeDirectory;                                                      // type = DIRECTORY
    inode[0x51] = OpflagPfsroot;                                                         // op_flags = PFSROOT
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(0x54, 4), 0x1ED);              // mode 0755
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x58, 8), 1);                  // inum = 1
    BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(0x60, 8), 0);                   // size
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x68, 8), 1);                  // nlinks = 1
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x70, 8), (ulong)PfsRootInum); // iparent
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x78, 8), nameKey);            // name_key
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x80, 2), (ushort)nmeBytes.Length); // name_len
    inode[0x83] = CompNone;                                                              // comp_algo = NONE
    inode[0x85] = CheckXxhash64;                                                         // check_algo
    inode[0x87] = PfsTypeMaster;                                                         // pfs_type = MASTER
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(0x88, 8), (ulong)PfsRootInum); // pfs_inum
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0x90, 16));                         // pfs_clid
    Guid.NewGuid().ToByteArray().CopyTo(inode.AsSpan(0xA0, 16));                         // pfs_fsid

    nmeBytes.CopyTo(inode.AsSpan(0x100));                                                // filename[]

    if (brefs is { Count: > 0 })
      this.LayoutBlockset(inode.AsSpan(0x200, SetCount * BlockrefBytes), brefs);

    return inode;
  }

  // ---- lay a set of blockrefs into a 4-entry blockset, spilling to indirect ---
  // The blockrefs are sorted ascending by key. When ≤4 they fit in the embedded
  // blockset directly; otherwise indirect blocks are built bottom-up until four
  // or fewer roots remain.
  private void LayoutBlockset(Span<byte> blockset, List<(ulong Key, byte[] Bref)> brefs) {
    var sorted = brefs.OrderBy(b => b.Key).ToList();

    if (sorted.Count <= SetCount) {
      for (var i = 0; i < sorted.Count; ++i)
        sorted[i].Bref.CopyTo(blockset.Slice(i * BlockrefBytes, BlockrefBytes));
      return;
    }

    var roots = this.BuildIndirectLevels(sorted);
    if (roots.Count > SetCount)
      throw new InvalidOperationException(
        $"HAMMER2: {roots.Count} indirect roots do not fit a {SetCount}-entry blockset.");
    for (var i = 0; i < roots.Count; ++i)
      roots[i].Bref.CopyTo(blockset.Slice(i * BlockrefBytes, BlockrefBytes));
  }

  /// <summary>
  /// Builds HAMMER2_BREF_TYPE_INDIRECT levels over key-sorted blockrefs until at
  /// most <see cref="SetCount" /> roots remain.
  /// </summary>
  /// <remarks>
  /// Grouping is by position rather than by keyspace bisection: a bisecting split
  /// yields however many children the key distribution happens to produce, which
  /// for a file's logical offsets is far more than the four an embedded blockset
  /// holds. The low and high halves of the 64-bit keyspace are grouped separately
  /// so no indirect ever needs keybits 64 — a full-64-bit span the kernel cannot
  /// express (inode keys sit below 2^63, dirent hashes above it).
  /// </remarks>
  private List<(ulong Key, byte[] Bref)> BuildIndirectLevels(List<(ulong Key, byte[] Bref)> entries) {
    const ulong Mid = 1UL << 63;
    var low = entries.Where(e => e.Key < Mid).ToList();
    var high = entries.Where(e => e.Key >= Mid).ToList();

    while (low.Count + high.Count > SetCount) {
      var before = low.Count + high.Count;
      if (low.Count > 1) low = this.CollapseLevel(low);
      if (high.Count > 1) high = this.CollapseLevel(high);
      if (low.Count + high.Count >= before)
        throw new InvalidOperationException("HAMMER2: indirect tree failed to converge.");
    }

    return [.. low, .. high];
  }

  /// <summary>Packs one level of blockrefs into indirect blocks, returning the level above.</summary>
  private List<(ulong Key, byte[] Bref)> CollapseLevel(List<(ulong Key, byte[] Bref)> level) {
    var next = new List<(ulong Key, byte[] Bref)>();
    for (var i = 0; i < level.Count; i += IndFanout) {
      var count = Math.Min(IndFanout, level.Count - i);
      var block = new byte[1 << IndRadix];
      for (var j = 0; j < count; ++j)
        level[i + j].Bref.CopyTo(block.AsSpan(j * BlockrefBytes, BlockrefBytes));

      var lo = level[i].Key;
      var hi = level[i + count - 1].Key;
      var (baseKey, keyBits) = SpanOf(lo, hi);

      var off = this.PlaceBlock(block, IndRadix);
      var bref = new byte[BlockrefBytes];
      this.WriteBlockref(bref, BrefTypeIndirect, checkAlgo: CheckXxhash64, compAlgo: CompNone,
        flags: 0, key: baseKey, vradix: IndRadix, dataOff: off, check: ComputeXxCheck(block));
      bref[3] = (byte)keyBits;                     // keybits: span of this indirect
      // leaf_count: number of leaf blockrefs under this indirect. The kernel uses
      // it to size directory iteration; 0 makes readdir treat the subtree as empty.
      BinaryPrimitives.WriteUInt16LittleEndian(bref.AsSpan(6, 2), (ushort)Math.Min(count, ushort.MaxValue));
      next.Add((baseKey, bref));
    }
    return next;
  }

  /// <summary>
  /// Smallest aligned keyspace covering [lo, hi]: the number of low bits the two
  /// keys differ in, and the base key with those bits cleared.
  /// </summary>
  private static (ulong BaseKey, int KeyBits) SpanOf(ulong lo, ulong hi) {
    var bits = 0;
    while (bits < 63 && (lo >> bits) != (hi >> bits))
      ++bits;
    return (bits == 0 ? lo : (lo >> bits) << bits, bits);
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
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(24, 8), this._mirrorTid); // modify_tid (non-zero; the live kernel treats modify_tid==0 as uninitialised)
    BinaryPrimitives.WriteInt64LittleEndian(br.Slice(32, 8), dataOff);       // data_off (radix in low bits)
    BinaryPrimitives.WriteUInt64LittleEndian(br.Slice(40, 8), 0);            // update_tid
    // embed @0x30 stays zero (stats: data_count/inode_count = 0).
    // check union @0x40: ISCSI32/XXHASH64 value goes in the first 8 bytes.
    check.CopyTo(br.Slice(64, check.Length));
  }

  // ===== helpers =====

  // Allocates a block of the given radix and writes 'block' into it, returning
  // the encoded data_off. 'block' must be exactly 1<<radix bytes.
  private long PlaceBlock(byte[] block, int radix) {
    var dataOff = this.Allocate(radix, out var deviceOffset);
    PlaceAt(this._topo, this._allocatorBeg, deviceOffset, block);
    return dataOff;
  }

  /// <summary>
  /// Allocates a data block past the topology area and writes it straight to the
  /// output, returning the encoded data_off. File contents never enter _topo.
  /// </summary>
  private long WriteDataBlock(ReadOnlySpan<byte> block, int radix) {
    var size = 1L << radix;
    this._dataBump = AlignUp(this._dataBump, size);
    var deviceOffset = this._dataBump;
    this._dataBump += size;

    var sink = this._sink ?? throw new InvalidOperationException("HAMMER2: no output stream bound.");
    sink.Seek(deviceOffset, SeekOrigin.Begin);
    sink.Write(block);
    return EncodeDataOff(deviceOffset, radix);
  }

  // Writes 'src' at the device offset, translating through the topo base.
  private static void PlaceAt(byte[] topo, long topoBase, long deviceOffset, byte[] src) =>
    src.CopyTo(topo.AsSpan((int)(deviceOffset - topoBase), src.Length));

  // The device byte offset (radix bits masked off) of an encoded data_off.
  private static long DecodeOffset(long dataOff) => dataOff & ~0x3FL;

  // The smallest power-of-two radix whose block holds at least 'bytes', floored
  // at the minimum HAMMER2 allocation radix (64 bytes).
  private static int DataRadix(long bytes) {
    var radix = 6; // minimum HAMMER2 allocation radix (64 bytes)
    while ((1L << radix) < bytes)
      ++radix;
    return radix;
  }

  private static void EnsureCapacity(ref byte[] buffer, long needed) {
    if (buffer.LongLength >= needed)
      return;
    var newLen = Math.Max(buffer.LongLength * 2, needed);
    Array.Resize(ref buffer, (int)newLen);
  }

  // data_off encodes the device byte offset with the allocation radix in the
  // low HAMMER2_OFF_MASK_RADIX (6) bits.
  private static long EncodeDataOff(long deviceOffset, int radix) =>
    (deviceOffset & ~0x3FL) | (long)(uint)(radix & 0x3F);

  private static byte[] ComputeXxCheck(ReadOnlySpan<byte> data) {
    var h = Hammer2Crc.XxHash64(data, Hammer2Crc.Hammer2Seed);
    var b = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, h);
    return b;
  }

  private static long AlignUp(long v, long align) => (v + align - 1) & ~(align - 1);

  private static ulong NowMicros() =>
    (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
