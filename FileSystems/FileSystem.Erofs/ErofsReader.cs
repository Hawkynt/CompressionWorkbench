#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Erofs;

/// <summary>
/// Reads EROFS (Enhanced Read-Only File System) images as used by Android system/APEX
/// partitions and produced by <c>mkfs.erofs</c>. Handles the uncompressed inode layouts
/// (FLAT_PLAIN and FLAT_INLINE) for both compact (32-byte) and extended (64-byte)
/// inodes; LZ4 / LZMA compressed clusters and fragments are deferred — an inode with a
/// compressed datalayout surfaces as a zero-length / unsupported payload rather than
/// failing the whole image.
/// <para>
/// The superblock lives at file offset 1024; on-disk magic is the little-endian word
/// <c>0xE0F5E1E2</c> (bytes <c>E2 E1 F5 E0</c>). Block size is <c>2^sb.blkszbits</c>
/// (almost always 4096). A node id (<c>nid</c>) addresses a 32-byte granule measured
/// from <c>meta_blkaddr * blockSize</c>, i.e. the inode lives at
/// <c>meta_blkaddr * blockSize + nid * 32</c>.
/// </para>
/// </summary>
public sealed class ErofsReader {
    /// <summary>
  /// Represents an entry.
  /// </summary>
public sealed record Entry(string Path, long Size, bool IsDirectory, ulong Nid,
    bool IsSymlink = false, string? LinkTarget = null);

  /// <summary>On-disk superblock magic, little-endian word <c>0xE0F5E1E2</c>.</summary>
  public const uint Magic = 0xE0F5E1E2u;

  // EROFS datalayouts (see erofs_inode datalayout field == (i_format >> 1) & 7).
  private const int LayoutFlatPlain = 0;
  private const int LayoutFlatInline = 2;

  /// <summary>
  /// Random-access view over the image. Copying it into a byte[] capped the
  /// reader at the array limit, which EROFS's block addresses do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private int _blockSize;
  private uint _metaBlkAddr;
  private ulong _rootNid;
  private readonly List<Entry> _entries = [];
  private readonly HashSet<ulong> _visited = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Volume label from the superblock <c>volume_name</c> field (16 bytes, NUL-trimmed ASCII).</summary>
  public string VolumeName { get; private set; } = "";

  /// <summary>Reads an image straight from a seekable stream.</summary>
  public ErofsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

    /// <summary>
  /// Initializes a new instance of <see cref="ErofsReader"/>.
  /// </summary>
public ErofsReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._data = ImageAccessor.FromBytes(data);
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 1024 + 128)
      throw new InvalidDataException("EROFS image too small for superblock.");

    // erofs_super_block at offset 1024:
    //   magic@0, checksum@4, feature_compat@8, blkszbits@12, sb_extslots@13,
    //   root_nid@14 (u16), inos@16 (u64), build_time@24 (u64),
    //   build_time_nsec@32 (u32), blocks@36 (u32), meta_blkaddr@40 (u32),
    //   xattr_blkaddr@44 (u32), uuid@48[16], volume_name@64[16],
    //   feature_incompat@80 (u32), ...
    var sb = this._data.Read(1024, 128).AsSpan();
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (magic != Magic)
      throw new InvalidDataException($"EROFS: bad superblock magic 0x{magic:X8} (want 0x{Magic:X8}).");

    var blkszbits = sb[12];
    if (blkszbits is < 9 or > 16)
      throw new InvalidDataException($"EROFS: implausible blkszbits {blkszbits}.");
    this._blockSize = 1 << blkszbits;
    this._rootNid = BinaryPrimitives.ReadUInt16LittleEndian(sb[14..]);
    this._metaBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]);

    // volume_name @64[16] — NUL-trimmed ASCII label.
    var nameSpan = sb.Slice(64, 16);
    var nameLen = nameSpan.IndexOf((byte)0);
    if (nameLen < 0) nameLen = 16;
    this.VolumeName = nameLen == 0 ? "" : System.Text.Encoding.ASCII.GetString(nameSpan[..nameLen]);

    this.Walk(this._rootNid, "");
  }

  private void Walk(ulong nid, string pathPrefix) {
    if (!this._visited.Add(nid)) return; // guard against directory cycles
    var inodeOffset = (long)this._metaBlkAddr * this._blockSize + (long)(nid * 32);
    if (inodeOffset < 0 || inodeOffset + 32 > this._data.Length) return;

    var meta = this.ReadInodeMeta(nid);
    if (meta is null) return;
    var m = meta.Value;

    var isDir = (m.Mode & 0xF000) == 0x4000;  // S_IFDIR
    var isReg = (m.Mode & 0xF000) == 0x8000;  // S_IFREG
    var isLink = (m.Mode & 0xF000) == 0xA000; // S_IFLNK
    if (!isDir && !isReg && !isLink) return;  // devices/sockets skipped for this pass

    if (isDir) {
      var dirData = this.ReadInodeData(m);
      this.WalkDirBlock(dirData, pathPrefix, nid);
    } else if (isLink) {
      // EROFS stores a symlink's target path as the inode's data (FLAT_INLINE for
      // the common short-target case); the link's own size is that path's length.
      var target = Encoding.UTF8.GetString(this.ReadInodeData(m));
      this._entries.Add(new Entry(pathPrefix.TrimEnd('/'), m.Size, IsDirectory: false, nid,
        IsSymlink: true, LinkTarget: target));
    } else {
      this._entries.Add(new Entry(pathPrefix.TrimEnd('/'), m.Size, IsDirectory: false, nid));
    }
  }

  private readonly record struct InodeMeta(
    ulong Nid, long InodeOffset, int HeaderSize, ushort Mode, long Size, int Layout, uint RawBlkAddr);

  private InodeMeta? ReadInodeMeta(ulong nid) {
    var inodeOffset = (long)this._metaBlkAddr * this._blockSize + (long)(nid * 32);
    if (inodeOffset < 0 || inodeOffset + 32 > this._data.Length) return null;

    var inode = this._data.Read(inodeOffset, 64).AsSpan();
    var format = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var isExtended = (format & 0x01) != 0;       // EROFS_I_VERSION_BIT
    var layout = (format >> 1) & 0x07;            // EROFS_I_DATALAYOUT

    ushort mode;
    long size;
    uint rawBlkAddr;
    int headerSize;
    if (isExtended) {
      if (inodeOffset + 64 > this._data.Length) return null;
      mode = BinaryPrimitives.ReadUInt16LittleEndian(inode[4..]);
      size = BinaryPrimitives.ReadInt64LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      headerSize = 64;
    } else {
      mode = BinaryPrimitives.ReadUInt16LittleEndian(inode[4..]);
      size = BinaryPrimitives.ReadUInt32LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      headerSize = 32;
    }
    return new InodeMeta(nid, inodeOffset, headerSize, mode, size, layout, rawBlkAddr);
  }

  /// <summary>
  /// Where an entry's full data blocks live: EROFS lays them out contiguously
  /// from the inode's raw block address. A residual tail stored inline with the
  /// inode is part of the metadata region, not of this run. Returns false when
  /// the inode has no out-of-line data at all.
  /// </summary>
  public bool TryGetDataExtent(Entry entry, out long offset, out long length) {
    ArgumentNullException.ThrowIfNull(entry);
    offset = 0;
    length = 0;
    if (entry.IsDirectory || entry.Size <= 0) return false;
    if (this.ReadInodeMeta(entry.Nid) is not { } m) return false;
    if (m.Layout is not (LayoutFlatPlain or LayoutFlatInline)) return false;
    if (m.RawBlkAddr == 0xFFFFFFFFu) return false;

    var blocks = m.Layout == LayoutFlatPlain
      ? (m.Size + this._blockSize - 1) / this._blockSize
      : m.Size / this._blockSize;
    if (blocks <= 0) return false;

    offset = (long)m.RawBlkAddr * this._blockSize;
    if (offset < 0 || offset >= this._data.Length) return false;
    length = Math.Min(blocks * this._blockSize, this._data.Length - offset);
    return length > 0;
  }

  private byte[] ReadInodeData(InodeMeta m) {
    if (m.Size == 0) return [];
    if (m.Size > int.MaxValue) throw new InvalidDataException("EROFS: object too large.");

    return m.Layout switch {
      LayoutFlatPlain => this.ReadPlain((long)m.RawBlkAddr * this._blockSize, (int)m.Size),
      LayoutFlatInline => this.ReadInline(m.InodeOffset, m.HeaderSize, m.RawBlkAddr, (int)m.Size),
      _ => [], // compressed layouts not yet supported
    };
  }

  private byte[] ReadPlain(long offset, int length) {
    if (offset < 0) return [];
    if (offset + length > this._data.Length)
      length = (int)Math.Max(0, this._data.Length - offset);
    var buf = new byte[length];
    if (length > 0)
      this._data.Read(offset, length).CopyTo(buf);
    return buf;
  }

  private byte[] ReadInline(long inodeOffset, int headerSize, uint rawBlkAddr, int size) {
    // FLAT_INLINE: full blocks (if any) live contiguously at rawBlkAddr; the residual
    // tail (size mod blockSize) sits immediately after the inode header within the meta
    // region. The inline tail is what mkfs.erofs uses for the common small-object case
    // where size < blockSize and rawBlkAddr is the sentinel 0xFFFFFFFF (no full block).
    var fullBlocks = size / this._blockSize;
    var tail = size - fullBlocks * this._blockSize;
    var buf = new byte[size];

    if (fullBlocks > 0 && rawBlkAddr != 0xFFFFFFFFu) {
      var src = (long)rawBlkAddr * this._blockSize;
      var want = fullBlocks * this._blockSize;
      var take = (int)Math.Min(want, this._data.Length - src);
      if (take > 0)
        this._data.Read(src, take).CopyTo(buf);
    }
    if (tail > 0) {
      var tailSrc = inodeOffset + headerSize;
      var take = (int)Math.Min(tail, this._data.Length - tailSrc);
      if (take > 0)
        this._data.Read(tailSrc, take).CopyTo(buf.AsSpan(fullBlocks * this._blockSize));
    }
    return buf;
  }

  // Each directory chunk is packed as: [erofs_dirent[] headers][name bytes].
  // erofs_dirent is 12 bytes: nid (u64) @0, nameoff (u16) @8, file_type (u8) @10,
  // reserved (u8) @11. The first entry's nameoff equals the header-array byte length,
  // so the entry count is firstNameOff / 12. A name extends from its nameoff to the
  // next entry's nameoff (or the chunk end for the last entry), trimmed at NUL.
  //
  // Large directories span multiple blocks; each block restarts the header/name layout,
  // so we decode block-by-block over the directory's logical data.
  private void WalkDirBlock(byte[] dirData, string pathPrefix, ulong selfNid) {
    var total = dirData.Length;
    for (var blockStart = 0; blockStart < total; blockStart += this._blockSize) {
      var blockLen = Math.Min(this._blockSize, total - blockStart);
      this.WalkDirChunk(dirData.AsSpan(blockStart, blockLen), pathPrefix, selfNid);
    }
  }

  private void WalkDirChunk(ReadOnlySpan<byte> chunk, string pathPrefix, ulong selfNid) {
    if (chunk.Length < 12) return;
    var firstNameOff = BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]);
    if (firstNameOff < 12 || firstNameOff > chunk.Length) return;
    var entryCount = firstNameOff / 12;

    for (var i = 0; i < entryCount; ++i) {
      var eOff = i * 12;
      if (eOff + 12 > chunk.Length) break;
      var nid = BinaryPrimitives.ReadUInt64LittleEndian(chunk[eOff..]);
      var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(eOff + 8)..]);
      if (nameOff > chunk.Length) break;

      int nameEnd;
      if (i + 1 < entryCount) {
        nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(chunk[(eOff + 12 + 8)..]);
        if (nameEnd < nameOff || nameEnd > chunk.Length) nameEnd = chunk.Length;
      } else {
        nameEnd = chunk.Length;
      }

      var rawName = chunk[nameOff..nameEnd];
      var zero = rawName.IndexOf((byte)0);
      if (zero >= 0) rawName = rawName[..zero];
      var name = Encoding.UTF8.GetString(rawName);

      if (name is "." or "..") continue;
      if (nid == selfNid) continue;
      if (name.Length == 0) continue;

      this.Walk(nid, pathPrefix + name + "/");
    }
  }

  /// <summary>
  /// Extracts the raw bytes of a given entry. Throws <see cref="NotSupportedException"/>
  /// if the entry points at a compressed-layout inode (until LZ4 support lands).
  /// </summary>
  public byte[] ExtractFile(Entry entry) {
    var meta = this.ReadInodeMeta(entry.Nid)
      ?? throw new InvalidDataException($"EROFS: inode for nid {entry.Nid} is out of range.");
    return meta.Layout switch {
      LayoutFlatPlain or LayoutFlatInline => this.ReadInodeData(meta),
      _ => throw new NotSupportedException(
        $"EROFS inode at nid {entry.Nid} uses compressed datalayout {meta.Layout}; decompression not yet implemented."),
    };
  }
}
