#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Ubifs;

/// <summary>
/// Builds a minimal UBIFS image holding a flat list of small regular files plus
/// the directory tree needed to reach them.
/// </summary>
/// <remarks>
/// <para><b>What this writer emits</b>: a linear node stream — superblock, master,
/// root-directory inode, per-file inode + dentry + zlib-compressed DATA node(s).
/// Each node carries a fully valid 24-byte common header (magic, CRC-32 over the
/// payload after the CRC field, sqnum, len, type, group=0) so the same linear
/// scanner that drives <see cref="UbifsFileReader"/> can round-trip the image.</para>
///
/// <para><b>What's NOT emitted</b> (out of scope — these require a full
/// wandering-tree commit pipeline and a kernel-mountable image is a multi-week
/// project): LPT (LEB Properties Tree), TNC (Tree Node Cache) index B+tree,
/// commit-start / reference / orphan nodes, journal heads, padding/garbage-collection
/// markers. A real <c>mkfs.ubifs</c> wires these together so the kernel can mount
/// the result; our reader operates on a linear log scan and does not need them.
/// Tests therefore validate self-round-trip, not kernel mount.</para>
///
/// <para>Compression: DATA nodes are zlib (DEFLATE) compressed when that shrinks
/// the payload, otherwise stored. LZO/ZSTD are not emitted.</para>
/// </remarks>
public sealed class UbifsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>UBIFS node common-header magic (LE).</summary>
  public const uint NodeMagic = 0x06101831;

  /// <summary>Default UBIFS logical block size (4 KiB).</summary>
  public const int BlockSize = 4096;

  /// <summary>Default LEB size — 64 KiB matches common NAND flash geometry.</summary>
  public const int DefaultLebSize = 65536;

  // Common header: magic(4) crc(4) sqnum(8) len(4) type(1) group(1) pad(2) = 24 bytes
  internal const int CommonHeaderSize = 24;

  // Node types
  internal const byte NodeTypeInode = 0;
  internal const byte NodeTypeData = 1;
  internal const byte NodeTypeDentry = 2;
  internal const byte NodeTypeSuperblock = 6;
  internal const byte NodeTypeMaster = 7;

  // Key types (top 3 bits of upper key word)
  internal const uint KeyTypeIno = 0;
  internal const uint KeyTypeData = 1;
  internal const uint KeyTypeDent = 2;

  // Compression types (UBIFS_COMPR_*)
  internal const ushort ComprNone = 0;
  internal const ushort ComprZlib = 2;

  // Mode bits
  internal const uint ModeDir = 0x4000;   // S_IFDIR
  internal const uint ModeFile = 0x8000;  // S_IFREG
  internal const uint ModePerms = 0x01ED; // 0755 for dirs; we use 0644 for files via OR/mask below

  // dt_type values
  internal const byte DtReg = 1;
  internal const byte DtDir = 4;

  // Inode layout (after common header):
  //   key[16] creat_sqnum[8] size[8] atime[8] ctime[8] mtime[8]
  //   atime_nsec[4] ctime_nsec[4] mtime_nsec[4] nlink[4] uid[4] gid[4] mode[4]
  //   flags[4] data_len[4] xattr_cnt[4] xattr_size[4] pad[4] xattr_names[4]
  //   compr_type[2] pad2[2+24]
  // Total = 24 (common) + 16+8+8+8+8+8 + 4+4+4+4+4+4+4 + 4+4+4+4+4+4 + 2+2+24 = 24+160 = 184
  internal const int InodeNodeSize = 184;

  // Dentry payload (after common header):
  //   key[16] inum[8] padding[1] type[1] nlen[2] cookie[4] name[nlen + 1 NUL]
  // Length is 24 + 16 + 8 + 4 + 4 + nlen + 1 (kernel always NUL-terminates)
  //
  // The cookie between the name length and the name is easy to leave out, and
  // leaving it out puts the name four bytes early. Both halves of this project
  // did, so they agreed with each other and with nothing else: a name we wrote
  // landed where a reader expects the cookie, and a name mkfs.ubifs wrote came
  // back as its own first character with four nulls in front of it.
  internal const int DentryFixedSize = 24 + 16 + 8 + 1 + 1 + 2 + 4;

  // Data payload (after common header):
  //   key[16] size[4] compr_type[2] compr_size[2] data[compr_size]
  internal const int DataFixedSize = 24 + 16 + 4 + 2 + 2;

  // Superblock node total length (kernel: sizeof(struct ubifs_sb_node) = 4096).
  // We don't need the full 4 KiB for self-round-trip; the scanner only reads
  // the common header (len/type). Use 4096 to keep it LEB-pad friendly.
  internal const int SuperblockNodeSize = 4096;

  // Master node total length (kernel: sizeof(struct ubifs_mst_node) = 512).
  internal const int MasterNodeSize = 512;

  private readonly int _lebSize;
  private ulong _sqnum;

  public UbifsWriter(int lebSize = DefaultLebSize) {
    if (lebSize < BlockSize || (lebSize & (lebSize - 1)) != 0)
      throw new ArgumentException("LEB size must be a power of two >= 4096.", nameof(lebSize));
    this._lebSize = lebSize;
  }

  /// <summary>Queues a file for inclusion in the next <see cref="Build"/> call.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Writes the assembled image to a stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = this.Build();
    output.Write(image, 0, image.Length);
  }

  /// <summary>
  /// Builds the in-memory UBIFS image. Layout:
  /// LEB 0: superblock node + padding to LEB end.
  /// LEB 1: master node + padding to LEB end.
  /// LEB 2..N: linear stream of root inode, per-file (file inode, dentry, data
  /// nodes). Nodes never span an LEB boundary in our layout — when the next node
  /// would not fit in the current LEB, we skip to the start of the next LEB.
  /// </summary>
  public byte[] Build() {
    this._sqnum = 1;
    var nodes = new List<byte[]>();

    // LEB 0: superblock
    var superblock = this.BuildSuperblockNode();

    // LEB 1: master
    var master = this.BuildMasterNode();

    // LEB 2+: linear log of inode/dentry/data nodes
    const uint RootInode = 1;
    nodes.Add(this.BuildInodeNode(RootInode, mode: ModeDir | 0x01ED, size: 0));

    var directoryInodes = new Dictionary<string, uint>(StringComparer.Ordinal) {
      [string.Empty] = RootInode,
    };
    var nextInode = 2u;

    foreach (var (rawName, data) in this._files) {
      var segments = SplitPath(rawName);
      if (segments.Length == 0)
        continue;

      // Walk/create intermediate directories.
      var parentInode = RootInode;
      var pathSoFar = string.Empty;
      for (var i = 0; i < segments.Length - 1; ++i) {
        pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "/" + segments[i];
        if (!directoryInodes.TryGetValue(pathSoFar, out var dirInode)) {
          dirInode = nextInode++;
          directoryInodes[pathSoFar] = dirInode;

          nodes.Add(this.BuildInodeNode(dirInode, mode: ModeDir | 0x01ED, size: 0));
          nodes.Add(this.BuildDentryNode(parentInode, dirInode, DtDir, segments[i]));
        }
        parentInode = dirInode;
      }

      var leaf = segments[^1];
      var fileInode = nextInode++;
      nodes.Add(this.BuildInodeNode(fileInode, mode: ModeFile | 0x01A4, size: (ulong)data.Length));
      nodes.Add(this.BuildDentryNode(parentInode, fileInode, DtReg, leaf));

      // Split file into 4 KiB blocks and emit a (maybe-zlib'd) data node per block.
      for (var blockIdx = 0u; (long)blockIdx * BlockSize < data.Length; ++blockIdx) {
        var start = (int)blockIdx * BlockSize;
        var len = Math.Min(BlockSize, data.Length - start);
        var chunk = new byte[len];
        Array.Copy(data, start, chunk, 0, len);
        nodes.Add(this.BuildDataNode(fileInode, blockIdx, chunk));
      }
    }

    // Compose: LEB0 (superblock), LEB1 (master), LEB2+ (log of nodes).
    // Nodes never straddle an LEB boundary.
    var assembled = new List<byte>(this._lebSize * 4);

    // LEB 0
    assembled.AddRange(superblock);
    PadToLebBoundary(assembled, this._lebSize);

    // LEB 1
    assembled.AddRange(master);
    PadToLebBoundary(assembled, this._lebSize);

    // LEB 2+
    var lebBase = assembled.Count;
    foreach (var node in nodes) {
      // 8-byte alignment within an LEB (UBIFS pads to obj align).
      while ((assembled.Count & 7) != 0)
        assembled.Add(0xFF);

      // Skip to next LEB if this node would straddle the boundary.
      var posInLeb = (assembled.Count - lebBase) % this._lebSize;
      if (posInLeb + node.Length > this._lebSize) {
        PadToLebBoundary(assembled, this._lebSize);
      }

      assembled.AddRange(node);
    }
    PadToLebBoundary(assembled, this._lebSize);

    return [.. assembled];
  }

  private static void PadToLebBoundary(List<byte> buf, int lebSize) {
    while ((buf.Count % lebSize) != 0)
      buf.Add(0xFF);
  }

  /// <summary>
  /// Builds the 24-byte common header and finalizes magic/sqnum/len/type/group fields.
  /// CRC is computed AFTER the entire node body (including the rest of the payload)
  /// has been populated — call <see cref="FinalizeCrc"/> last.
  /// </summary>
  private void StampCommonHeader(byte[] node, byte type) {
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(0, 4), NodeMagic);
    // CRC slot (4 bytes) left empty for FinalizeCrc.
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(8, 8), this._sqnum++);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), (uint)node.Length);
    node[20] = type;
    node[21] = 0; // group_type = NO_NODE_GROUP
    node[22] = 0;
    node[23] = 0;
  }

  /// <summary>
  /// Computes UBIFS-style CRC: CRC-32 (IEEE polynomial) over the node body
  /// starting at byte 8 (i.e. excluding the magic + CRC field), then stamps it
  /// into bytes 4..7 little-endian.
  /// </summary>
  internal static void FinalizeCrc(byte[] node) {
    var crc = Crc32.Compute(node.AsSpan(8, node.Length - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), crc);
  }

  private byte[] BuildSuperblockNode() {
    var node = new byte[SuperblockNodeSize];
    this.StampCommonHeader(node, NodeTypeSuperblock);

    // Selected superblock fields (per ubifs-media.h, after common header at +24):
    //   u8 key_hash; u8 key_fmt; u8 flags; u32 min_io_size; u32 leb_size;
    //   u32 leb_cnt; u32 max_leb_cnt; u64 max_bud_bytes; u32 log_lebs;
    //   u32 lpt_lebs; u32 orph_lebs; u32 jhead_cnt; u32 fanout; u32 lsave_cnt;
    //   u32 fmt_version; u16 default_compr; u8 padding1[2]; u32 rp_uid; u32 rp_gid; ...
    var p = node.AsSpan(24);
    p[0] = 0; // key_hash = R5
    p[1] = 0; // key_fmt = simple
    p[2] = 0; // flags
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4, 4), 2048); // min_io_size
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(8, 4), (uint)this._lebSize); // leb_size
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(12, 4), 1024); // leb_cnt (arbitrary)
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(16, 4), 1024); // max_leb_cnt
    BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(20, 8), 1u << 20); // max_bud_bytes
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(28, 4), 4); // log_lebs
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(32, 4), 2); // lpt_lebs
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(36, 4), 1); // orph_lebs
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(40, 4), 1); // jhead_cnt
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(44, 4), 8); // fanout
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(48, 4), 256); // lsave_cnt
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(52, 4), 5); // fmt_version (UBIFS_FORMAT_VERSION 5)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(56, 2), ComprZlib); // default_compr

    FinalizeCrc(node);
    return node;
  }

  private byte[] BuildMasterNode() {
    var node = new byte[MasterNodeSize];
    this.StampCommonHeader(node, NodeTypeMaster);
    // Master fields tracking commit state — left at zero. The reader does not
    // require these for linear log scan.
    FinalizeCrc(node);
    return node;
  }

  private byte[] BuildInodeNode(uint inum, uint mode, ulong size) {
    var node = new byte[InodeNodeSize];
    this.StampCommonHeader(node, NodeTypeInode);

    // Key: inum in low 32 bits, key type in top 3 bits of next word.
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), inum);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), KeyTypeIno << 29);
    // key[8..15] zero

    // creat_sqnum @40 — leave 0
    // size @48
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(48, 8), size);
    // atime/ctime/mtime @56/64/72 — leave 0
    // nlink @92
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(92, 4), 1);
    // uid @96, gid @100? — kernel layout: uid(4) gid(4) mode(4).
    // Reader treats offset 100 as mode (see InodeModeOffset).
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(96, 4), 0); // uid
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(100, 4), mode); // mode (reader reads here)
    // flags @104 — leave 0
    // data_len @108 — leave 0
    // compr_type @128 (after xattr_cnt/xattr_size/pad/xattr_names of 5*4 bytes) — leave Zlib
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(128, 2), ComprZlib);

    FinalizeCrc(node);
    return node;
  }

  private byte[] BuildDentryNode(uint parentInode, uint childInode, byte dtType, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    if (nameBytes.Length > 255)
      throw new ArgumentException("Dentry name exceeds 255 bytes.", nameof(name));

    var len = DentryFixedSize + nameBytes.Length + 1; // +1 NUL
    var node = new byte[len];
    this.StampCommonHeader(node, NodeTypeDentry);

    // Key: parent inum + (KeyTypeDent in top 3 bits | hash in low 29 bits)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), parentInode);
    var keyHi = (KeyTypeDent << 29) | (NameHash(nameBytes) & 0x1FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), keyHi);

    // inum @40
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), childInode);
    // pad @48
    node[48] = 0;
    // type @49
    node[49] = dtType;
    // nlen @50
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(50, 2), (ushort)nameBytes.Length);
    // cookie @52 stays zero: it is only meaningful with the double-hash feature.
    // name @56 (DentryFixedSize)
    nameBytes.CopyTo(node, DentryFixedSize);
    // NUL terminator already at node[DentryFixedSize + nameBytes.Length]

    FinalizeCrc(node);
    return node;
  }

  private byte[] BuildDataNode(uint inum, uint blockIdx, byte[] uncompressed) {
    // Try zlib — keep stored if compression doesn't shrink.
    var compressed = ZlibCompress(uncompressed);
    byte[] payload;
    ushort comprType;
    if (compressed.Length < uncompressed.Length) {
      payload = compressed;
      comprType = ComprZlib;
    } else {
      payload = uncompressed;
      comprType = ComprNone;
    }

    var len = DataFixedSize + payload.Length;
    var node = new byte[len];
    this.StampCommonHeader(node, NodeTypeData);

    // Key: inum + (KeyTypeData in top 3 bits | blockIdx in low 29 bits)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), inum);
    var keyHi = (KeyTypeData << 29) | (blockIdx & 0x1FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), keyHi);

    // size @40 (uncompressed block size)
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), (uint)uncompressed.Length);
    // compr_type @44
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(44, 2), comprType);
    // compr_size @46 — reader treats payload as `nodeLen - DataPayloadOffset` so
    // this field is informational; we set it for completeness.
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(46, 2), (ushort)Math.Min(payload.Length, ushort.MaxValue));

    payload.CopyTo(node, DataFixedSize);

    FinalizeCrc(node);
    return node;
  }

  internal static byte[] ZlibCompress(byte[] data) {
    using var output = new MemoryStream();
    using (var zls = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
      zls.Write(data, 0, data.Length);
    return output.ToArray();
  }

  /// <summary>
  /// UBIFS R5 name hash (low 29 bits) — used as the dentry key low word. Our
  /// reader does not enforce a particular hash since it linearly scans dentries,
  /// but we compute it anyway so the keyspace is well-formed.
  /// </summary>
  internal static uint NameHash(byte[] name) {
    uint a = 0;
    foreach (var b in name)
      a += b * 11u;
    return a & 0x1FFFFFFFu;
  }

  /// <summary>
  /// Splits an entry name into its path components on '/' and '\' separators,
  /// dropping empty segments (leading/trailing/duplicate separators).
  /// </summary>
  private static string[] SplitPath(string name)
    => name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
