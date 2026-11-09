using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Lfs;

/// <summary>One file or directory of a 4.4BSD LFS volume.</summary>
/// <param name="Path">Slash-separated path from the root (inode 2).</param>
/// <param name="Ino">Inode number.</param>
/// <param name="Mode">Unix mode word.</param>
/// <param name="Uid">Owner.</param>
/// <param name="Gid">Group.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="MTime">Modification time (UNIX seconds).</param>
public sealed record LfsEntry(string Path, uint Ino, ushort Mode, uint Uid, uint Gid, long Size, int MTime) {
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  public bool IsFile => (this.Mode & 0xF000) == 0x8000;
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
}

/// <summary>
/// Reads NetBSD's 4.4BSD log-structured file system, version 2 with 32-bit block addresses
/// ("LFS32", the format <c>newfs_lfs</c> creates by default): the newer of the two checkpoint
/// superblocks names the inode of the IFILE, whose entries map every inode number to the
/// block holding its newest copy. See <c>docs/LFS-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// The state read is the last checkpoint — what the kernel mounts. Partial segments written
/// after it are not rolled forward. Converted from NetBSD's BSD-licensed LFS headers
/// (<c>sys/ufs/lfs/lfs.h</c>, <c>lfs_cksum.c</c>); see <c>THIRD-PARTY-NOTICE.Lfs.txt</c>.
/// </remarks>
public sealed class LfsReader {
  public const uint Magic = 0x070162;
  public const uint IfileIno = 1, RootIno = 2;
  private const int DinodeSize = 128, IfileEntrySize = 20, NDirect = 12, DirBlockSize = 512;

  private readonly Stream _stream;
  private readonly int _fsbtodb, _ibsize, _maxSymlinkLen, _cleansz, _segtabsz, _ifpb, _nindir;
  private readonly byte[] _ifileInode;

  /// <summary>File system block size.</summary>
  public int BlockSize { get; }
  /// <summary>Fragment size.</summary>
  public int FragmentSize { get; }
  /// <summary>Serial number of the checkpoint read.</summary>
  public ulong Serial { get; }
  /// <summary>The 512-byte superblock of the checkpoint read.</summary>
  internal byte[] SuperblockBytes { get; }
  /// <summary>Every file and directory below the root, parents first.</summary>
  public IReadOnlyList<LfsEntry> Entries { get; }

  public LfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var sb = this.PickSuperblock();
    this.SuperblockBytes = sb;
    this.BlockSize = (int)U32(sb, 20);
    this.FragmentSize = (int)U32(sb, 24);
    this._fsbtodb = (int)U32(sb, 372);
    this._ibsize = (int)U32(sb, 344);
    this._maxSymlinkLen = (int)U32(sb, 184);
    this._cleansz = (int)U32(sb, 124);
    this._segtabsz = (int)U32(sb, 128);
    this._ifpb = (int)U32(sb, 104);
    this._nindir = (int)U32(sb, 112);
    this.Serial = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(336));
    if (this.BlockSize is < 512 or > 65536 || this.FragmentSize is < 512 || this.FragmentSize > this.BlockSize
        || this._ibsize < DinodeSize || this._ifpb <= 0 || this._nindir != this.BlockSize / 4 || this._fsbtodb > 16)
      throw new InvalidDataException("LFS superblock geometry is inconsistent.");
    if (U32(sb, 360) > 1) throw new InvalidDataException($"LFS inode format {U32(sb, 360)} is not supported.");

    this._ifileInode = this.ReadInodeAt(BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(52)), IfileIno)
      ?? throw new InvalidDataException("LFS checkpoint does not lead to the IFILE inode.");

    var entries = new List<LfsEntry>();
    var root = this.ReadInode(RootIno) ?? throw new InvalidDataException("LFS root inode 2 is not allocated.");
    if ((U16(root, 0) & 0xF000) != 0x4000) throw new InvalidDataException("LFS inode 2 is not a directory.");
    this.Walk(root, "", entries, 0);
    this.Entries = entries;
  }

  /// <summary>The bytes of a file, or a symlink's target.</summary>
  public byte[] Extract(LfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inode = this.ReadInode(entry.Ino) ?? throw new InvalidDataException($"LFS inode {entry.Ino} vanished.");
    if (entry.IsSymlink && entry.Size < this._maxSymlinkLen && U32(inode, 104) == 0)
      return inode.AsSpan(40, (int)entry.Size).ToArray();
    return this.ReadFile(inode);
  }

  /// <summary>The content of a file through the pointers of <paramref name="inode"/>.</summary>
  internal byte[] ReadInodeData(byte[] inode) => this.ReadFile(inode);

  private byte[] PickSuperblock() {
    byte[]? best = null;
    var primary = this.TrySuperblock(8192);
    var candidates = new List<byte[]>();
    if (primary is not null) {
      candidates.Add(primary);
      var fsbtodb = (int)U32(primary, 372);
      for (var i = 1; i < 10; ++i) {
        var off = BinaryPrimitives.ReadInt32LittleEndian(primary.AsSpan(188 + 4 * i));
        if (off <= 0) continue;
        if (this.TrySuperblock(((long)off << fsbtodb) * 512) is { } other) candidates.Add(other);
      }
    }
    foreach (var c in candidates)
      if (best is null || BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(336)) > BinaryPrimitives.ReadUInt64LittleEndian(best.AsSpan(336)))
        best = c;
    return best ?? throw new InvalidDataException("Not an LFS (v2, 32-bit) volume: no valid superblock at 8 KiB.");
  }

  private byte[]? TrySuperblock(long offset) {
    if (offset < 0 || offset + 512 > this._stream.Length) return null;
    var sb = this.Read(offset, 512);
    if (U32(sb, 0) != Magic || U32(sb, 4) != 2) return null;
    // lfs_sb_cksum: XOR of the little-endian 16-bit words before the checksum field.
    uint sum = 0;
    for (var i = 0; i < 508; i += 2) sum ^= BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(i));
    return sum == U32(sb, 508) ? sb : null;
  }

  private void Walk(byte[] dir, string prefix, List<LfsEntry> entries, int depth) {
    if (depth > 256) throw new InvalidDataException("LFS directory nesting beyond 256 levels.");
    var data = this.ReadFile(dir);
    var children = new List<(string Name, uint Ino)>();
    for (var block = 0; block < data.Length; block += DirBlockSize) {
      var off = block;
      var end = Math.Min(block + DirBlockSize, data.Length);
      while (off + 8 <= end) {
        var ino = U32(data, off);
        int reclen = U16(data, off + 4);
        int namlen = data[off + 7];
        if (reclen < 8 || off + reclen > end || 8 + namlen > reclen) throw new InvalidDataException("LFS directory record is malformed.");
        if (ino != 0) {
          var name = Encoding.UTF8.GetString(data, off + 8, namlen);
          if (name is not "." and not "..") children.Add((name, ino));
        }
        off += reclen;
      }
    }
    foreach (var (name, ino) in children.OrderBy(c => c.Name, StringComparer.Ordinal)) {
      if (name.Length == 0 || name.Contains('/')) throw new InvalidDataException($"LFS directory entry has an unusable name '{name}'.");
      var inode = this.ReadInode(ino) ?? throw new InvalidDataException($"LFS name '{name}' points at unallocated inode {ino}.");
      var path = prefix.Length == 0 ? name : prefix + "/" + name;
      var e = new LfsEntry(path, ino, U16(inode, 0), U32(inode, 112), U32(inode, 116),
        (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(24)));
      entries.Add(e);
      if (e.IsDirectory) this.Walk(inode, path, entries, depth + 1);
    }
  }

  /// <summary>The newest copy of inode <paramref name="ino"/>, located through its IFILE entry.</summary>
  private byte[]? ReadInode(uint ino) {
    if (ino == IfileIno) return this._ifileInode;
    var perBlock = (long)this._ifpb;
    var lbn = this._cleansz + this._segtabsz + ino / perBlock;
    var block = this.ReadFileBlock(this._ifileInode, lbn);
    var at = (int)(ino % perBlock) * IfileEntrySize;
    var daddr = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(at + 4));
    return daddr <= 0 ? null : this.ReadInodeAt(daddr, ino);
  }

  private byte[]? ReadInodeAt(int daddr, uint ino) {
    var block = this.Read(this.ByteAddress(daddr), this._ibsize);
    for (var at = 0; at + DinodeSize <= block.Length; at += DinodeSize)
      if (U32(block, at + 4) == ino) return block.AsSpan(at, DinodeSize).ToArray();
    return null;
  }

  private byte[] ReadFile(byte[] inode) {
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(8));
    if (size < 0 || size > int.MaxValue) throw new InvalidDataException("LFS file too large to buffer.");
    var data = new byte[size];
    for (long lbn = 0; lbn * this.BlockSize < size; ++lbn) {
      var block = this.ReadFileBlock(inode, lbn);
      var at = lbn * this.BlockSize;
      var n = (int)Math.Min(this.BlockSize, size - at);
      block.AsSpan(0, Math.Min(n, block.Length)).CopyTo(data.AsSpan((int)at));
    }
    return data;
  }

  /// <summary>Logical block <paramref name="lbn"/> of a file: 12 direct, then single, double and triple indirect.</summary>
  private byte[] ReadFileBlock(byte[] inode, long lbn) {
    int Ptr(int index) => BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(40 + 4 * index));
    int daddr;
    if (lbn < NDirect) daddr = Ptr((int)lbn);
    else {
      lbn -= NDirect;
      long span = this._nindir;
      var level = 0;
      while (lbn >= span) {
        lbn -= span;
        if (++level > 2) throw new InvalidDataException("LFS block number beyond triple indirection.");
        span *= this._nindir;
      }
      daddr = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(88 + 4 * level));
      for (var l = level; l >= 0 && daddr > 0; --l) {
        span /= this._nindir;
        var index = (int)(lbn / span);
        lbn %= span;
        daddr = BinaryPrimitives.ReadInt32LittleEndian(this.Read(this.ByteAddress(daddr), this.BlockSize).AsSpan(4 * index));
      }
    }
    if (daddr <= 0) return new byte[this.BlockSize];   // hole
    // A file's last block may be a fragment that ends exactly at the end of the volume.
    var at = this.ByteAddress(daddr);
    var n = (int)Math.Min(this.BlockSize, this._stream.Length - at);
    var b = this.Read(at, n);
    if (n < this.BlockSize) Array.Resize(ref b, this.BlockSize);
    return b;
  }

  private long ByteAddress(int daddr) {
    var at = ((long)daddr << this._fsbtodb) * 512;
    if (at < 0 || at >= this._stream.Length) throw new InvalidDataException($"LFS block address {daddr} lies outside the volume.");
    return at;
  }

  private byte[] Read(long offset, int count) {
    if (offset < 0 || offset + count > this._stream.Length)
      throw new InvalidDataException($"LFS read at {offset} (+{count}) lies past the end of the volume.");
    var buf = new byte[count];
    this._stream.Position = offset;
    this._stream.ReadExactly(buf);
    return buf;
  }

  private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
  private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
}
