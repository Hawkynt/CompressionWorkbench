#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Gfs2;

/// <summary>
/// Read-only GFS2 (Global File System 2) image walker. Mainline Linux since
/// 2.6.19; Red Hat cluster filesystem. Big-endian on-disk.
///
/// What we parse:
/// <list type="bullet">
///   <item><description>Superblock at byte offset 65536 (= sector 128 × 512 B). Magic
///   <c>mh_magic = 0x01161970</c> at the start of the gfs2_meta_header.</description></item>
///   <item><description>Block size from <c>sb_bsize</c>, root + master inum from
///   <c>sb_root_dir</c> / <c>sb_master_dir</c>.</description></item>
///   <item><description>Root inode (<c>gfs2_dinode</c>) and any <c>gfs2_dirent</c>
///   records living inline in the inode block (single-leaf directories).</description></item>
/// </list>
///
/// What we deliberately skip (multi-week effort each):
/// <list type="bullet">
///   <item><description>ExHash directories (multi-level leaf blocks)</description></item>
///   <item><description>Multi-level block indirection (di_height > 0)</description></item>
///   <item><description>Journal recovery, cluster lock manager state</description></item>
///   <item><description>Extended attributes</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/gfs2/</c> — primary on-disk definition</description></item>
///   <item><description><c>include/uapi/linux/gfs2_ondisk.h</c> — magic constants &amp; struct layout</description></item>
///   <item><description>Red Hat Cluster Suite / Resilient Storage Add-On docs</description></item>
/// </list>
/// </summary>
public sealed class Gfs2Reader {

  /// <summary>GFS2 metadata magic — <c>mh_magic</c> at start of every metadata block.</summary>
  public const uint MetaMagic = 0x01161970u;

  /// <summary>gfs2_meta_header.mh_type for the superblock.</summary>
  public const uint MetaTypeSuperblock = 1;

  /// <summary>gfs2_meta_header.mh_type for a dinode.</summary>
  public const uint MetaTypeDinode = 4;

  /// <summary>Filesystem format version expected in sb_fs_format (GFS2_FORMAT_FS).</summary>
  public const uint FormatFs = 1802;

  /// <summary>Multi-host format version expected in sb_multihost_format (GFS2_FORMAT_MULTI).</summary>
  public const uint FormatMultihost = 1900;

  /// <summary>Superblock byte offset within the device (sector 128 × 512 B).</summary>
  public const long SbByteOffset = 65536;

  /// <summary>
  /// Size of <c>struct gfs2_meta_header</c> on disk: 24 bytes —
  /// <c>mh_magic</c>(4) + <c>mh_type</c>(4) + <c>__pad0</c>(8) + <c>mh_format</c>(4) +
  /// <c>mh_jid</c>(4). Real <c>mkfs.gfs2</c> output uses the 24-byte header; every
  /// metadata struct (sb, dinode, leaf, rgrp, log header) embeds it at offset 0.
  /// </summary>
  public const int MetaHeaderSize = 24;

  private readonly byte[] _image;
  private readonly List<Gfs2Entry> _entries = new();

  public bool SuperblockValid { get; private set; }
  public uint BlockSize { get; private set; }
  public uint BlockSizeShift { get; private set; }
  public ulong RootInodeBlock { get; private set; }
  public ulong RootFormalIno { get; private set; }
  public ulong MasterInodeBlock { get; private set; }
  public ulong MasterFormalIno { get; private set; }
  public string LockProto { get; private set; } = "";
  public string LockTable { get; private set; } = "";
  public string UuidHex { get; private set; } = "";

  public IReadOnlyList<Gfs2Entry> Entries => this._entries;

  public Gfs2Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._image = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    // Need at least superblock + a bit more.
    if (this._image.LongLength < SbByteOffset + 256)
      return;

    var sb = this._image.AsSpan((int)SbByteOffset);

    // Meta header check.
    var mhMagic = BinaryPrimitives.ReadUInt32BigEndian(sb[..4]);
    var mhType = BinaryPrimitives.ReadUInt32BigEndian(sb[4..8]);
    if (mhMagic != MetaMagic || mhType != MetaTypeSuperblock)
      return;

    this.SuperblockValid = true;

    // gfs2_sb layout, all fields after the 24-byte gfs2_meta_header (mh ends @24):
    // u32 sb_fs_format         @24
    // u32 sb_multihost_format  @28
    // u32 __pad0               @32
    // u32 sb_bsize             @36
    // u32 sb_bsize_shift       @40
    // u32 __pad1               @44
    // gfs2_inum sb_master_dir  @48  (u64 no_formal_ino @48, u64 no_addr @56)
    // gfs2_inum __pad2         @64  (16 bytes — historically sb_quota_di, now reserved)
    // gfs2_inum sb_root_dir    @80  (u64 no_formal_ino @80, u64 no_addr @88)
    // char sb_lockproto[64]    @96
    // char sb_locktable[64]    @160
    // gfs2_inum __pad3         @224
    // gfs2_inum __pad4         @240
    // u8 sb_uuid[16]           @256
    this.BlockSize = BinaryPrimitives.ReadUInt32BigEndian(sb.Slice(36, 4));
    this.BlockSizeShift = BinaryPrimitives.ReadUInt32BigEndian(sb.Slice(40, 4));

    this.MasterFormalIno = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(48, 8));
    this.MasterInodeBlock = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(56, 8));
    this.RootFormalIno = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(80, 8));
    this.RootInodeBlock = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(88, 8));

    this.LockProto = ReadCString(sb.Slice(96, 64));
    this.LockTable = ReadCString(sb.Slice(160, 64));

    if (sb.Length >= 256 + 16)
      this.UuidHex = Convert.ToHexString(sb.Slice(256, 16));

    // Walk the root inode. Errors are non-fatal — we still surface the
    // superblock metadata.
    if (this.BlockSize is >= 512 and <= 65536) {
      try {
        this.WalkRoot();
      } catch {
        // Eat — read-only best-effort walker.
      }
    }
  }

  private void WalkRoot() {
    var bs = (long)this.BlockSize;
    var rootOff = (long)this.RootInodeBlock * bs;
    if (rootOff <= 0 || rootOff + bs > this._image.LongLength)
      return;

    var rootBlock = this._image.AsSpan((int)rootOff, (int)bs);
    var mhMagic = BinaryPrimitives.ReadUInt32BigEndian(rootBlock[..4]);
    var mhType = BinaryPrimitives.ReadUInt32BigEndian(rootBlock[4..8]);
    if (mhMagic != MetaMagic || mhType != MetaTypeDinode)
      return;

    // gfs2_dinode layout (BE) after the 24-byte gfs2_meta_header:
    // gfs2_inum di_num         @24  (16 bytes: no_formal_ino @24, no_addr @32)
    // u32 di_mode              @40
    // u32 di_uid               @44
    // u32 di_gid               @48
    // u32 di_nlink             @52
    // u64 di_size              @56
    // u64 di_blocks            @64
    // u64 di_atime             @72
    // u64 di_mtime             @80
    // u64 di_ctime             @88
    // u32 di_major             @96
    // u32 di_minor             @100
    // u64 di_goal_meta         @104
    // u64 di_goal_data         @112
    // u64 di_generation        @120
    // u32 di_flags             @128
    // u32 di_payload_format    @132
    // u16 __pad1               @136
    // u16 di_height            @138
    // u32 __pad2               @140
    // u16 __pad3               @144
    // u16 di_depth             @146
    // u32 di_entries           @148
    // The dinode header is 232 bytes (sizeof gfs2_dinode). Inline directory
    // entries (or inline data) follow at offset 232.
    var diMode = BinaryPrimitives.ReadUInt32BigEndian(rootBlock.Slice(40, 4));
    var diSize = BinaryPrimitives.ReadUInt64BigEndian(rootBlock.Slice(56, 8));
    var diMtime = BinaryPrimitives.ReadUInt64BigEndian(rootBlock.Slice(80, 8));
    var diHeight = BinaryPrimitives.ReadUInt16BigEndian(rootBlock.Slice(138, 2));
    var diEntries = BinaryPrimitives.ReadUInt32BigEndian(rootBlock.Slice(148, 4));

    // S_IFDIR check — must be a directory for root.
    var isDir = (diMode & 0xF000) == 0x4000;
    if (!isDir)
      return;

    // Single-leaf inline directory only (di_height == 0). Multi-level ExHash
    // is multi-week work and out of scope.
    if (diHeight != 0)
      return;

    // Inline directory entries (gfs2_dirent) start at offset 232 and run to
    // end-of-block. Each dirent is variable length, rec_len-terminated.
    const int dinodeHeaderSize = 232;
    if (rootBlock.Length <= dinodeHeaderSize)
      return;

    var dentries = rootBlock[dinodeHeaderSize..];
    this.ParseDentries(dentries, diMtime, (uint)Math.Min(diEntries, 4096u));
  }

  private void ParseDentries(ReadOnlySpan<byte> area, ulong dirMtimeBe, uint maxEntries) {
    // gfs2_dirent layout (BE) — sizeof on disk is 40 bytes (verified against
    // real mkfs.gfs2 output, gfs2-utils 3.5.1):
    // gfs2_inum de_inum     @0   (u64 no_formal_ino, u64 no_addr) - 16 bytes
    // u32 de_hash           @16
    // u16 de_rec_len        @20
    // u16 de_name_len       @22
    // u16 de_type           @24
    // u16 de_rahead         @26
    // u8  de_cookie/__pad[12] @28  (reserved tail — the name starts AFTER it)
    // name (variable)       @40
    const int direntHeaderSize = 40;

    var off = 0;
    uint count = 0;
    while (off + direntHeaderSize <= area.Length && count < maxEntries) {
      var de = area[off..];
      var formalIno = BinaryPrimitives.ReadUInt64BigEndian(de.Slice(0, 8));
      var noAddr = BinaryPrimitives.ReadUInt64BigEndian(de.Slice(8, 8));
      var recLen = BinaryPrimitives.ReadUInt16BigEndian(de.Slice(20, 2));
      var nameLen = BinaryPrimitives.ReadUInt16BigEndian(de.Slice(22, 2));
      var deType = BinaryPrimitives.ReadUInt16BigEndian(de.Slice(24, 2));

      // Sanity. rec_len must be ≥ header+name, ≤ remaining area, and 8-aligned.
      if (recLen < direntHeaderSize + nameLen || off + recLen > area.Length ||
          (recLen & 7) != 0)
        break;
      if (nameLen == 0 || nameLen > 255) {
        off += recLen;
        continue;
      }
      if (noAddr == 0) {
        off += recLen;
        continue;
      }

      var nameBytes = de.Slice(direntHeaderSize, nameLen);
      var name = Encoding.UTF8.GetString(nameBytes);

      // Skip "." and ".."
      if (name is "." or "..") {
        off += recLen;
        continue;
      }

      var entry = new Gfs2Entry {
        Name = name,
        InodeBlock = noAddr,
        FormalIno = formalIno,
        IsDirectory = deType == 4, // DT_DIR
        LastModified = TryGetTime(dirMtimeBe),
      };
      // For regular files, also try to read di_size from the target dinode.
      if (!entry.IsDirectory && this.TryReadDinodeSize(noAddr, out var size, out var mtime)) {
        entry = new Gfs2Entry {
          Name = name,
          InodeBlock = noAddr,
          FormalIno = formalIno,
          IsDirectory = false,
          Size = (long)size,
          LastModified = TryGetTime(mtime) ?? entry.LastModified,
        };
      }
      this._entries.Add(entry);
      count++;
      off += recLen;
    }
  }

  private bool TryReadDinodeSize(ulong block, out ulong size, out ulong mtimeBe) {
    size = 0;
    mtimeBe = 0;
    var bs = (long)this.BlockSize;
    var off = (long)block * bs;
    if (off <= 0 || off + 232 > this._image.LongLength)
      return false;
    var b = this._image.AsSpan((int)off);
    var magic = BinaryPrimitives.ReadUInt32BigEndian(b[..4]);
    var type = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
    if (magic != MetaMagic || type != MetaTypeDinode) return false;
    size = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(56, 8));
    mtimeBe = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(80, 8));
    return true;
  }

  /// <summary>
  /// Attempts to read file content for a regular-file entry. Only supports
  /// inline data (di_height == 0) — files stored entirely in the dinode block
  /// after the 232-byte header. Returns empty array if anything is off.
  /// </summary>
  public byte[] Extract(Gfs2Entry entry) {
    if (entry.IsDirectory) return [];
    var bs = (long)this.BlockSize;
    var off = (long)entry.InodeBlock * bs;
    if (off <= 0 || off + 232 > this._image.LongLength)
      return [];
    var b = this._image.AsSpan((int)off, (int)Math.Min(bs, this._image.LongLength - off));
    var magic = BinaryPrimitives.ReadUInt32BigEndian(b[..4]);
    var type = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
    if (magic != MetaMagic || type != MetaTypeDinode) return [];

    var diSize = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(56, 8));
    var diHeight = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(138, 2));
    if (diHeight != 0) return []; // multi-level indirection not supported

    const int dinodeHeaderSize = 232;
    if (b.Length <= dinodeHeaderSize) return [];

    var available = b.Length - dinodeHeaderSize;
    var n = (int)Math.Min((long)diSize, available);
    if (n <= 0) return [];
    return b.Slice(dinodeHeaderSize, n).ToArray();
  }

  private static DateTime? TryGetTime(ulong gfs2Time) {
    // GFS2 stores seconds since UNIX epoch.
    if (gfs2Time == 0) return null;
    if (gfs2Time > 0x0000_0000_FFFF_FFFFUL * 4) return null; // sanity
    try {
      return DateTimeOffset.FromUnixTimeSeconds((long)gfs2Time).UtcDateTime;
    } catch {
      return null;
    }
  }

  private static string ReadCString(ReadOnlySpan<byte> span) {
    var n = span.IndexOf((byte)0);
    if (n < 0) n = span.Length;
    if (n == 0) return "";
    var sb = new StringBuilder(n);
    for (var i = 0; i < n; i++) {
      var c = span[i];
      sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
    }
    return sb.ToString();
  }

  /// <summary>Raw superblock bytes (1024 bytes captured from offset 65536), for diagnostics.</summary>
  public byte[] SuperblockRaw {
    get {
      const int cap = 1024;
      if (this._image.LongLength < SbByteOffset + cap)
        return [];
      var buf = new byte[cap];
      Array.Copy(this._image, SbByteOffset, buf, 0, cap);
      return buf;
    }
  }
}
