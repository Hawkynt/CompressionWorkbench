#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
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
///   <item><description>Root and nested directory inodes whose <c>gfs2_dirent</c>
///   records are stuffed inline in the dinode.</description></item>
///   <item><description>Regular files stored either inline or through GFS2's
///   indirect metadata tree.</description></item>
/// </list>
///
/// What we deliberately skip:
/// <list type="bullet">
///   <item><description>ExHash directories (multi-level leaf blocks)</description></item>
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
public sealed class Gfs2Reader : IDisposable {

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
  /// <c>mh_jid</c>(4).
  /// </summary>
  public const int MetaHeaderSize = 24;

  private readonly ImageAccessor _image;
  private readonly List<Gfs2Entry> _entries = new();

  /// <summary>Gets a value indicating whether superblock valid.</summary>
  public bool SuperblockValid { get; private set; }
  /// <summary>Gets or sets the block size.</summary>
  public uint BlockSize { get; private set; }
  /// <summary>Gets or sets the block size shift.</summary>
  public uint BlockSizeShift { get; private set; }
  /// <summary>Gets or sets the root inode block.</summary>
  public ulong RootInodeBlock { get; private set; }
  /// <summary>Gets or sets the root formal ino.</summary>
  public ulong RootFormalIno { get; private set; }
  /// <summary>Gets or sets the master inode block.</summary>
  public ulong MasterInodeBlock { get; private set; }
  /// <summary>Gets or sets the master formal ino.</summary>
  public ulong MasterFormalIno { get; private set; }
  /// <summary>Gets or sets the lock proto.</summary>
  public string LockProto { get; private set; } = "";
  /// <summary>Gets or sets the lock table.</summary>
  public string LockTable { get; private set; } = "";
  /// <summary>Gets or sets the uuid hex.</summary>
  public string UuidHex { get; private set; } = "";

  /// <summary>Gets the entries.</summary>
  public IReadOnlyList<Gfs2Entry> Entries => this._entries;

  /// <summary>Initializes a new instance of <see cref="Gfs2Reader"/>.</summary>
  public Gfs2Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream);
    this.Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() => this._image.Dispose();

  /// <summary>Reads one whole block, or an empty span when it falls outside the image.</summary>
  private byte[] Block(long blockNumber) {
    var bs = (long)this.BlockSize;
    var off = blockNumber * bs;
    if (blockNumber <= 0 || off + bs > this._image.Length) return [];
    return this._image.Read(off, (int)bs);
  }

  private void Parse() {
    if (this._image.Length < SbByteOffset + 1024)
      return;

    var sb = this._image.Read(SbByteOffset, 1024).AsSpan();
    var mhMagic = BinaryPrimitives.ReadUInt32BigEndian(sb[..4]);
    var mhType = BinaryPrimitives.ReadUInt32BigEndian(sb[4..8]);
    if (mhMagic != MetaMagic || mhType != MetaTypeSuperblock)
      return;

    this.SuperblockValid = true;

    // gfs2_sb layout, all fields after the 24-byte gfs2_meta_header:
    // u32 sb_fs_format @24, u32 sb_multihost_format @28,
    // u32 sb_bsize @36, u32 sb_bsize_shift @40,
    // gfs2_inum sb_master_dir @48, gfs2_inum sb_root_dir @80,
    // char sb_lockproto[64] @96, char sb_locktable[64] @160, uuid @256.
    this.BlockSize = BinaryPrimitives.ReadUInt32BigEndian(sb.Slice(36, 4));
    this.BlockSizeShift = BinaryPrimitives.ReadUInt32BigEndian(sb.Slice(40, 4));
    this.MasterFormalIno = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(48, 8));
    this.MasterInodeBlock = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(56, 8));
    this.RootFormalIno = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(80, 8));
    this.RootInodeBlock = BinaryPrimitives.ReadUInt64BigEndian(sb.Slice(88, 8));
    this.LockProto = ReadCString(sb.Slice(96, 64));
    this.LockTable = ReadCString(sb.Slice(160, 64));

    if (sb.Length >= 272)
      this.UuidHex = Convert.ToHexString(sb.Slice(256, 16));

    if (this.BlockSize is >= 512 and <= 65536) {
      try {
        this.WalkDirectory(this.RootInodeBlock, "", new HashSet<ulong>(), 0);
      } catch {
        // Read-only best effort: malformed user metadata must not hide a valid superblock.
      }
    }
  }

  /// <summary>
  /// Walks one native stuffed directory and recursively descends through directory
  /// entries. ExHash directories have a non-zero height and remain deliberately
  /// unsupported. The visited set and depth guard make malformed cycles fail closed.
  /// </summary>
  private void WalkDirectory(ulong inodeBlock, string prefix, HashSet<ulong> visited, int depth) {
    const int maxDepth = 256;
    if (depth > maxDepth || inodeBlock == 0 || !visited.Add(inodeBlock))
      return;

    var directory = this.Block((long)inodeBlock).AsSpan();
    if (directory.Length < DinodeHeaderSize)
      return;

    var mhMagic = BinaryPrimitives.ReadUInt32BigEndian(directory[..4]);
    var mhType = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(4, 4));
    if (mhMagic != MetaMagic || mhType != MetaTypeDinode)
      return;

    var diMode = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(40, 4));
    var diMtime = BinaryPrimitives.ReadUInt64BigEndian(directory.Slice(80, 8));
    var diHeight = BinaryPrimitives.ReadUInt16BigEndian(directory.Slice(138, 2));
    var diEntries = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(148, 4));
    if ((diMode & 0xF000) != 0x4000 || diHeight != 0)
      return;

    this.ParseDentries(
      directory[DinodeHeaderSize..],
      diMtime,
      (uint)Math.Min(diEntries, 4096u),
      prefix,
      visited,
      depth);
  }

  private void ParseDentries(ReadOnlySpan<byte> area, ulong dirMtimeBe, uint maxEntries,
                             string prefix, HashSet<ulong> visited, int depth) {
    // gfs2_dirent layout (BE): inum @0, hash @16, rec_len @20, name_len @22,
    // type @24, reserved tail through byte 39, UTF-8 name at byte 40.
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

      if (recLen < direntHeaderSize + nameLen || off + recLen > area.Length || (recLen & 7) != 0)
        break;

      count++;
      if (nameLen == 0 || nameLen > 255 || noAddr == 0) {
        off += recLen;
        continue;
      }

      var name = Encoding.UTF8.GetString(de.Slice(direntHeaderSize, nameLen));
      if (name is "." or "..") {
        off += recLen;
        continue;
      }

      var fullName = prefix.Length == 0 ? name : $"{prefix}/{name}";
      var isDirectory = deType == 4;
      var entry = new Gfs2Entry {
        Name = fullName,
        InodeBlock = noAddr,
        FormalIno = formalIno,
        IsDirectory = isDirectory,
        LastModified = TryGetTime(dirMtimeBe),
      };

      if (!isDirectory && this.TryReadDinodeSize(noAddr, out var size, out var mtime)) {
        entry = new Gfs2Entry {
          Name = fullName,
          InodeBlock = noAddr,
          FormalIno = formalIno,
          IsDirectory = false,
          Size = (long)size,
          LastModified = TryGetTime(mtime) ?? entry.LastModified,
        };
      }

      this._entries.Add(entry);
      if (isDirectory)
        this.WalkDirectory(noAddr, fullName, visited, depth + 1);

      off += recLen;
    }
  }

  private bool TryReadDinodeSize(ulong block, out ulong size, out ulong mtimeBe) {
    size = 0;
    mtimeBe = 0;
    var b = this.Block((long)block).AsSpan();
    if (b.Length < DinodeHeaderSize)
      return false;
    var magic = BinaryPrimitives.ReadUInt32BigEndian(b[..4]);
    var type = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
    if (magic != MetaMagic || type != MetaTypeDinode) return false;
    size = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(56, 8));
    mtimeBe = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(80, 8));
    return true;
  }

  /// <summary>Reads a regular file's content. Only valid below the array limit.</summary>
  public byte[] Extract(Gfs2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"GFS2: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s content into <paramref name="destination" />.
  /// A body up to <c>blocksize - 232</c> is stuffed in the dinode; a longer one hangs
  /// off a metadata tree whose depth <c>di_height</c> gives.
  /// </summary>
  public long ExtractTo(Gfs2Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;

    var dinode = this.Block((long)entry.InodeBlock);
    if (dinode.Length < DinodeHeaderSize) return 0;
    var magic = BinaryPrimitives.ReadUInt32BigEndian(dinode.AsSpan(0, 4));
    var type = BinaryPrimitives.ReadUInt32BigEndian(dinode.AsSpan(4, 4));
    if (magic != MetaMagic || type != MetaTypeDinode) return 0;

    var diSize = (long)BinaryPrimitives.ReadUInt64BigEndian(dinode.AsSpan(56, 8));
    var diHeight = BinaryPrimitives.ReadUInt16BigEndian(dinode.AsSpan(138, 2));
    if (diSize <= 0) return 0;

    if (diHeight == 0) {
      var n = (int)Math.Min(diSize, dinode.Length - DinodeHeaderSize);
      if (n <= 0) return 0;
      destination.Write(dinode, DinodeHeaderSize, n);
      return n;
    }

    var bs = (int)this.BlockSize;
    var hole = new byte[bs];
    long written = 0;
    foreach (var dataBlock in this.WalkTree(dinode, DinodeHeaderSize, diHeight)) {
      if (written >= diSize) break;
      var take = (int)Math.Min(bs, diSize - written);
      var block = this.Block(dataBlock);
      if (block.Length == 0)
        destination.Write(hole, 0, take);
      else
        destination.Write(block, 0, take);
      written += take;
    }
    return written;
  }

  /// <summary>
  /// Where on disk <paramref name="entry" />'s bytes actually sit, as runs of
  /// whole blocks, along with the byte offset of the first pointer that names
  /// each run.
  /// </summary>
  public IEnumerable<(long Offset, long Length, long PointerOffset)> EnumerateDataExtents(Gfs2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) yield break;

    var dinode = this.Block((long)entry.InodeBlock);
    if (dinode.Length < DinodeHeaderSize) yield break;
    var magic = BinaryPrimitives.ReadUInt32BigEndian(dinode.AsSpan(0, 4));
    var type = BinaryPrimitives.ReadUInt32BigEndian(dinode.AsSpan(4, 4));
    if (magic != MetaMagic || type != MetaTypeDinode) yield break;

    var diSize = (long)BinaryPrimitives.ReadUInt64BigEndian(dinode.AsSpan(56, 8));
    var diHeight = BinaryPrimitives.ReadUInt16BigEndian(dinode.AsSpan(138, 2));
    if (diSize <= 0 || diHeight == 0) yield break;

    var blockSize = (long)this.BlockSize;
    var runFirstBlock = 0L;
    var runPointer = -1L;
    var runBlocks = 0;

    foreach (var (dataBlock, pointerOffset) in this.WalkTreePointers(
        dinode, (long)entry.InodeBlock * blockSize, DinodeHeaderSize, diHeight)) {
      if (runPointer >= 0 && dataBlock == runFirstBlock + runBlocks
          && pointerOffset == runPointer + (long)runBlocks * 8) {
        ++runBlocks;
        continue;
      }

      if (runPointer >= 0)
        yield return (runFirstBlock * blockSize, (long)runBlocks * blockSize, runPointer);

      runFirstBlock = dataBlock;
      runPointer = pointerOffset;
      runBlocks = 1;
    }

    if (runPointer >= 0)
      yield return (runFirstBlock * blockSize, (long)runBlocks * blockSize, runPointer);
  }

  private IEnumerable<(long DataBlock, long PointerOffset)> WalkTreePointers(
      byte[] block, long blockOffset, int pointerBase, int levels) {
    var capacity = (block.Length - pointerBase) / 8;
    for (var i = 0; i < capacity; ++i) {
      var pointer = (long)BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(pointerBase + i * 8, 8));
      if (pointer == 0) continue;
      var pointerOffset = blockOffset + pointerBase + (long)i * 8;
      if (levels <= 1) {
        yield return (pointer, pointerOffset);
        continue;
      }
      var child = this.Block(pointer);
      if (child.Length == 0) continue;
      foreach (var leaf in this.WalkTreePointers(child, pointer * this.BlockSize, IndPointerBase, levels - 1))
        yield return leaf;
    }
  }

  private IEnumerable<long> WalkTree(byte[] block, int pointerBase, int levels) {
    var capacity = (block.Length - pointerBase) / 8;
    for (var i = 0; i < capacity; ++i) {
      var pointer = (long)BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(pointerBase + i * 8, 8));
      if (pointer == 0) continue;
      if (levels <= 1) {
        yield return pointer;
        continue;
      }
      var child = this.Block(pointer);
      if (child.Length == 0) continue;
      foreach (var leaf in this.WalkTree(child, IndPointerBase, levels - 1))
        yield return leaf;
    }
  }

  /// <summary>Bytes of gfs2_dinode before its pointer or stuffed-data area.</summary>
  private const int DinodeHeaderSize = 232;

  /// <summary>Bytes of gfs2_meta_header before an indirect block's pointer area.</summary>
  private const int IndPointerBase = 24;

  private static DateTime? TryGetTime(ulong gfs2Time) {
    if (gfs2Time == 0) return null;
    if (gfs2Time > 0x0000_0000_FFFF_FFFFUL * 4) return null;
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
      if (this._image.Length < SbByteOffset + cap)
        return [];
      return this._image.Read(SbByteOffset, cap);
    }
  }
}
