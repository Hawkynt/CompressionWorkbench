#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// In-place add and remove on little-endian UFS1 volumes, kept consistent the way
/// FreeBSD's <c>fsck_ffs</c> checks them.
/// <list type="bullet">
///   <item>A path is resolved component by component; missing directories are
///   created as real directories (one <c>DIRBLKSIZ</c> chunk holding <c>.</c> and
///   <c>..</c>, the parent's link count raised), never as a root entry whose name
///   holds a slash.</item>
///   <item>Files take whole blocks plus a tail of fragments in the last direct
///   block (<c>fragroundup</c>), and <c>di_blocks</c> counts exactly what was
///   allocated. Up to the twelve direct blocks are written; larger files are
///   refused rather than given an indirect tree this editor does not maintain.</item>
///   <item>Allocation searches every cylinder group. After the free-fragment map
///   of a group changes, its derived summaries — <c>cs_nbfree</c>,
///   <c>cs_nffree</c>, <c>cg_frsum</c>, the cluster map and
///   <c>cg_clustersum</c> — are recomputed from that map; the per-group
///   <c>fs_cs</c> records and both superblock totals are re-summed.</item>
///   <item>All writes are staged and land only after every check has passed, so a
///   refused edit leaves the image byte-identical.</item>
/// </list>
/// References: FreeBSD <c>sys/ufs/ffs/fs.h</c> (<c>struct fs</c>, <c>struct cg</c>),
/// <c>sys/ufs/ufs/dinode.h</c>, <c>sys/ufs/ufs/dir.h</c>; McKusick et al., "A Fast File
/// System for UNIX" (1984).
/// </summary>
public static class UfsModifier {
  private const int SuperblockOffset = 8192;
  private const int SuperblockSize = 1376;
  private const int InodeSize = 128;
  private const int RootIno = 2;
  private const int MaxDirectBlocks = 12;
  private const int DirBlkSiz = 512;
  private const ushort IfMt = 0xF000;
  private const ushort IfDir = 0x4000;
  private const ushort IfReg = 0x8000;
  private const byte DtDir = 4;
  private const byte DtReg = 8;

  /// <summary>Adds a file at <paramref name="path"/>, creating missing parent directories.</summary>
  /// <exception cref="IOException">The entry exists, a path component is a file, the
  /// file needs more than twelve blocks, or the volume has no room.</exception>
  public static void AddFile(Stream image, string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    var parts = Split(path);
    var v = new Volume(image);
    if ((long)data.Length > (long)MaxDirectBlocks * v.BlockSize)
      throw new IOException($"UFS: '{path}' is {data.Length} bytes; in-place edits write at most {MaxDirectBlocks} direct blocks ({MaxDirectBlocks * v.BlockSize} bytes).");

    long dir = RootIno;
    for (var i = 0; i < parts.Length - 1; ++i) {
      var child = v.Lookup(dir, parts[i]);
      if (child is { } found) {
        if ((v.Mode(found.Ino) & IfMt) != IfDir) throw new IOException($"UFS: '{parts[i]}' in '{path}' is not a directory.");
        dir = found.Ino;
      } else {
        dir = v.MakeDirectory(dir, parts[i]);
      }
    }
    var name = parts[^1];
    if (v.Lookup(dir, name) != null) throw new IOException($"UFS: entry '{path}' already exists; remove it first to replace.");
    var ino = v.MakeFile(data);
    v.Link(dir, name, ino, DtReg);
    v.Commit();
  }

  /// <summary>
  /// Removes the file at <paramref name="path"/>. Returns false when there is no such
  /// entry or it is a directory. With <paramref name="wipeData"/> the freed fragments
  /// are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string path, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    string[] parts;
    try { parts = Split(path); } catch (ArgumentException) { return false; }
    var v = new Volume(image);
    long dir = RootIno;
    for (var i = 0; i < parts.Length - 1; ++i) {
      if (v.Lookup(dir, parts[i]) is not { } found || (v.Mode(found.Ino) & IfMt) != IfDir) return false;
      dir = found.Ino;
    }
    if (v.Lookup(dir, parts[^1]) is not { } entry) return false;
    if ((v.Mode(entry.Ino) & IfMt) == IfDir) return false;
    v.Unlink(dir, entry);
    v.ReleaseInode(entry.Ino, wipeData);
    v.Commit();
    return true;
  }

  private static string[] Split(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new ArgumentException("UFS: empty path.", nameof(path));
    foreach (var p in parts) {
      if (p is "." or "..") throw new ArgumentException($"UFS: '{path}' holds '.' or '..'.", nameof(path));
      if (Encoding.UTF8.GetByteCount(p) > 255) throw new ArgumentException($"UFS: '{p}' is longer than 255 bytes.", nameof(path));
    }
    return parts;
  }

  private readonly record struct DirEntry(long Ino, int Chunk, int Offset, int Previous);

  /// <summary>Superblock geometry, a fragment-granular write-back cache, and the edit primitives.</summary>
  private sealed class Volume {
    private readonly Stream _image;
    private readonly Dictionary<long, byte[]> _pages = [];
    private readonly HashSet<long> _dirty = [];
    private readonly HashSet<int> _dirtyGroups = [];
    private readonly Dictionary<int, byte[]> _groups = [];
    private readonly int _frag, _ncg, _ipg, _fpg, _cblkno, _iblkno, _csaddr, _contigSumSize;
    private readonly uint _now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public int BlockSize { get; }
    public int FragSize { get; }

    public Volume(Stream image) {
      _image = image;
      if (image.Length < SuperblockOffset + SuperblockSize) throw new InvalidDataException("UFS: image too small for a UFS1 superblock.");
      var sb = new byte[SuperblockSize];
      image.Position = SuperblockOffset;
      image.ReadExactly(sb);
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SuperblockSize - 4));
      if (magic != 0x00011954)
        throw new InvalidDataException($"UFS: superblock magic 0x{magic:X8} is not little-endian UFS1; only those volumes are edited in place.");
      int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(o));
      _cblkno = I(12); _iblkno = I(16); _ncg = I(44);
      BlockSize = I(48); FragSize = I(52); _frag = I(56);
      _csaddr = I(152); _ipg = I(184); _fpg = I(188); _contigSumSize = I(1316);
      if (FragSize < 512 || BlockSize < FragSize || _frag != BlockSize / FragSize || _ipg <= 0 || _fpg <= 0 || _ncg <= 0)
        throw new InvalidDataException("UFS: superblock geometry is inconsistent.");
      if (_contigSumSize < 0 || _contigSumSize > 64) _contigSumSize = 0;
    }

    // ── fragment-page cache ─────────────────────────────────────────────

    private byte[] Page(long index) {
      if (_pages.TryGetValue(index, out var page)) return page;
      page = new byte[FragSize];
      var offset = index * FragSize;
      if (offset < 0 || offset + FragSize > _image.Length) throw new IOException($"UFS: fragment {index} lies outside the image.");
      _image.Position = offset;
      _image.ReadExactly(page);
      _pages[index] = page;
      return page;
    }

    public byte[] Read(long offset, int length) {
      var result = new byte[length];
      var done = 0;
      while (done < length) {
        var at = offset + done;
        var page = Page(at / FragSize);
        var within = (int)(at % FragSize);
        var n = Math.Min(length - done, FragSize - within);
        page.AsSpan(within, n).CopyTo(result.AsSpan(done));
        done += n;
      }
      return result;
    }

    public void Write(long offset, ReadOnlySpan<byte> data) {
      var done = 0;
      while (done < data.Length) {
        var at = offset + done;
        var index = at / FragSize;
        var page = Page(index);
        var within = (int)(at % FragSize);
        var n = Math.Min(data.Length - done, FragSize - within);
        data.Slice(done, n).CopyTo(page.AsSpan(within));
        _dirty.Add(index);
        done += n;
      }
    }

    // ── inodes ──────────────────────────────────────────────────────────

    private long InodeOffset(long ino) => ((ino / _ipg) * _fpg + _iblkno) * (long)FragSize + (ino % _ipg) * InodeSize;
    private byte[] Inode(long ino) => Read(InodeOffset(ino), InodeSize);
    private void PutInode(long ino, byte[] inode) => Write(InodeOffset(ino), inode);
    public ushort Mode(long ino) => BinaryPrimitives.ReadUInt16LittleEndian(Inode(ino));

    private static long Size(byte[] inode) => BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(8));
    private static int Db(byte[] inode, int i) => BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(40 + i * 4));
    private static void SetDb(byte[] inode, int i, int frag) => BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(40 + i * 4), frag);
    private static int Ib(byte[] inode, int level) => BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(88 + level * 4));

    private void Touch(byte[] inode) {
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(24), _now); // di_mtime
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(32), _now); // di_ctime
    }

    private byte[] NewInode(ushort mode, short links, long size) {
      var inode = new byte[InodeSize];
      BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
      BinaryPrimitives.WriteInt16LittleEndian(inode.AsSpan(2), links);
      BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(8), size);
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(16), _now);
      Touch(inode);
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(108), (uint)Random.Shared.Next(1, int.MaxValue)); // di_gen
      return inode;
    }

    // ── cylinder groups ─────────────────────────────────────────────────

    private byte[] Group(int cg) {
      if (_groups.TryGetValue(cg, out var block)) return block;
      block = Read(((long)cg * _fpg + _cblkno) * FragSize, BlockSize);
      if (BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(4)) != 0x090255)
        throw new InvalidDataException($"UFS: cylinder group {cg} has no cg_magic.");
      _groups[cg] = block;
      return block;
    }

    private static int G(byte[] cg, int offset) => BinaryPrimitives.ReadInt32LittleEndian(cg.AsSpan(offset));
    private static void SetG(byte[] cg, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(cg.AsSpan(offset), value);
    private static bool Bit(byte[] cg, int at, int bit) => (cg[at + bit / 8] & (1 << (bit % 8))) != 0;
    private static void SetBit(byte[] cg, int at, int bit, bool on) {
      if (on) cg[at + bit / 8] |= (byte)(1 << (bit % 8));
      else cg[at + bit / 8] &= (byte)~(1 << (bit % 8));
    }

    private bool FragFree(int cg, int rel) => Bit(Group(cg), G(Group(cg), 96), rel);

    private void SetFrags(long frag, int count, bool free) {
      var cg = (int)(frag / _fpg);
      var block = Group(cg);
      var freeOff = G(block, 96);
      for (var i = 0; i < count; ++i) {
        var rel = (int)(frag % _fpg) + i;
        if (Bit(block, freeOff, rel) == free) throw new InvalidDataException($"UFS: fragment {frag + i} is already {(free ? "free" : "in use")}.");
        SetBit(block, freeOff, rel, free);
      }
      _dirtyGroups.Add(cg);
    }

    /// <summary>A run of <paramref name="count"/> free fragments inside one block; partly used blocks first.</summary>
    private long AllocateFrags(int count) {
      for (var pass = 0; pass < 2; ++pass)
        for (var cg = 0; cg < _ncg; ++cg) {
          var block = Group(cg);
          var ndblk = G(block, 20);
          for (var b = 0; b + _frag <= ndblk; b += _frag) {
            var free = 0;
            for (var f = 0; f < _frag; ++f) if (FragFree(cg, b + f)) ++free;
            if (free < count || (pass == 0 && free == _frag) || (pass == 1 && free != _frag)) continue;
            var run = 0;
            for (var f = 0; f < _frag; ++f) {
              run = FragFree(cg, b + f) ? run + 1 : 0;
              if (run == count) {
                var at = (long)cg * _fpg + b + f - count + 1;
                SetFrags(at, count, free: false);
                return at;
              }
            }
          }
        }
      throw new IOException("UFS: not enough free space.");
    }

    private long AllocateBlock() => AllocateFrags(_frag);

    private long AllocateInode(bool directory) {
      for (var cg = 0; cg < _ncg; ++cg) {
        var block = Group(cg);
        var iused = G(block, 92);
        for (var i = 0; i < _ipg; ++i) {
          var ino = (long)cg * _ipg + i;
          if (ino <= RootIno || Bit(block, iused, i)) continue;
          SetBit(block, iused, i, true);
          SetG(block, 32, G(block, 32) - 1);                 // cs_nifree
          if (directory) SetG(block, 24, G(block, 24) + 1);  // cs_ndir
          _dirtyGroups.Add(cg);
          return ino;
        }
      }
      throw new IOException("UFS: no free inodes.");
    }

    private void FreeInode(long ino, bool directory) {
      var cg = (int)(ino / _ipg);
      var block = Group(cg);
      SetBit(block, G(block, 92), (int)(ino % _ipg), false);
      SetG(block, 32, G(block, 32) + 1);
      if (directory) SetG(block, 24, G(block, 24) - 1);
      _dirtyGroups.Add(cg);
    }

    /// <summary>Re-derives a group's block/fragment summaries from its free-fragment map.</summary>
    private void Recount(int cg) {
      var block = Group(cg);
      var ndblk = G(block, 20);
      var freeOff = G(block, 96);
      var clusterOff = G(block, 108);
      var clusterSumOff = G(block, 104);
      int nbfree = 0, nffree = 0, run = 0;
      var frsum = new int[_frag];
      var sums = new int[_contigSumSize + 1];
      void Close() {
        if (run > 0 && _contigSumSize > 0) sums[Math.Min(run, _contigSumSize)]++;
        run = 0;
      }
      for (var b = 0; b + _frag <= ndblk; b += _frag) {
        var free = 0;
        for (var f = 0; f < _frag; ++f) if (Bit(block, freeOff, b + f)) ++free;
        var whole = free == _frag;
        if (_contigSumSize > 0) SetBit(block, clusterOff, b / _frag, whole);
        if (whole) { ++nbfree; ++run; continue; }
        Close();
        nffree += free;
        var r = 0;
        for (var f = 0; f <= _frag; ++f) {
          if (f < _frag && Bit(block, freeOff, b + f)) { ++r; continue; }
          if (r > 0) frsum[r]++;
          r = 0;
        }
      }
      Close();
      // A trailing partial block (ndblk not a multiple of frag) holds only free fragments.
      for (var f = ndblk - ndblk % _frag; f < ndblk; ++f) if (Bit(block, freeOff, f)) ++nffree;
      SetG(block, 28, nbfree);
      SetG(block, 36, nffree);
      for (var i = 1; i < _frag; ++i) SetG(block, 52 + i * 4, frsum[i]);
      for (var i = 1; i <= _contigSumSize; ++i) SetG(block, clusterSumOff + i * 4, sums[i]);
    }

    // ── data ────────────────────────────────────────────────────────────

    public long MakeFile(byte[] data) {
      var inode = NewInode((ushort)(IfReg | 0x1A4), 1, data.Length); // 0644
      var frags = Place(inode, data);
      BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(104), frags * (FragSize / 512)); // di_blocks
      var ino = AllocateInode(directory: false);
      PutInode(ino, inode);
      return ino;
    }

    // Lays `data` out as whole blocks plus a fragment tail; returns the fragments used.
    private int Place(byte[] inode, ReadOnlySpan<byte> data) {
      var used = 0;
      var blocks = (data.Length + BlockSize - 1) / BlockSize;
      for (var b = 0; b < blocks; ++b) {
        var bytes = Math.Min(BlockSize, data.Length - b * BlockSize);
        var count = b == blocks - 1 ? (bytes + FragSize - 1) / FragSize : _frag;
        var at = count == _frag ? AllocateBlock() : AllocateFrags(count);
        var chunk = new byte[count * FragSize];
        data.Slice(b * BlockSize, bytes).CopyTo(chunk);
        Write(at * FragSize, chunk);
        SetDb(inode, b, (int)at);
        used += count;
      }
      return used;
    }

    public void ReleaseInode(long ino, bool wipe) {
      var inode = Inode(ino);
      var links = BinaryPrimitives.ReadInt16LittleEndian(inode.AsSpan(2));
      if (links > 1) {
        BinaryPrimitives.WriteInt16LittleEndian(inode.AsSpan(2), (short)(links - 1));
        Touch(inode);
        PutInode(ino, inode);
        return;
      }
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
      var size = Size(inode);
      var blocks = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(104));
      // A fast symlink keeps its target in the pointer area: nothing to free.
      if (!((mode & IfMt) == 0xA000 && blocks == 0)) {
        var lblocks = (size + BlockSize - 1) / BlockSize;
        for (var b = 0; b < MaxDirectBlocks && b < lblocks; ++b) {
          var frag = Db(inode, b);
          if (frag == 0) continue;
          var count = b == lblocks - 1 && lblocks <= MaxDirectBlocks
            ? (int)(((size - b * (long)BlockSize) + FragSize - 1) / FragSize)
            : _frag;
          Release(frag, count, wipe);
        }
        var pointers = BlockSize / 4;
        for (var level = 0; level < 3; ++level) {
          var root = Ib(inode, level);
          if (root != 0) ReleaseTree(root, level + 1, pointers, wipe);
        }
      }
      PutInode(ino, new byte[InodeSize]);
      FreeInode(ino, directory: (mode & IfMt) == IfDir);
    }

    private void ReleaseTree(int pointerBlock, int level, int pointers, bool wipe) {
      var table = Read((long)pointerBlock * FragSize, BlockSize);
      for (var i = 0; i < pointers; ++i) {
        var p = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
        if (p == 0) continue;
        if (level > 1) ReleaseTree(p, level - 1, pointers, wipe);
        else Release(p, _frag, wipe);
      }
      Release(pointerBlock, _frag, wipe);
    }

    private void Release(long frag, int count, bool wipe) {
      SetFrags(frag, count, free: true);
      if (wipe) Write(frag * FragSize, new byte[count * FragSize]);
    }

    // ── directories ─────────────────────────────────────────────────────

    private static int MinRecLen(int nameLength) => (8 + nameLength + 1 + 3) & ~3; // DIRSIZ: name plus NUL, 4-aligned

    private byte[] DirectoryData(long ino, out byte[] inode) {
      inode = Inode(ino);
      var size = Size(inode);
      if (size <= 0 || size % DirBlkSiz != 0 || size > (long)MaxDirectBlocks * BlockSize)
        throw new NotSupportedException($"UFS: directory inode {ino} ({size} bytes) is outside what the in-place editor handles.");
      var data = new byte[size];
      for (var b = 0; b * (long)BlockSize < size; ++b) {
        var n = (int)Math.Min(BlockSize, size - b * (long)BlockSize);
        Read((long)Db(inode, b) * FragSize, n).CopyTo(data, b * BlockSize);
      }
      return data;
    }

    private void PutDirectoryChunk(byte[] inode, int chunk, ReadOnlySpan<byte> bytes) {
      var at = (long)chunk * DirBlkSiz;
      Write((long)Db(inode, (int)(at / BlockSize)) * FragSize + at % BlockSize, bytes);
    }

    public DirEntry? Lookup(long dir, string name) {
      var data = DirectoryData(dir, out _);
      var wanted = Encoding.UTF8.GetBytes(name);
      for (var chunk = 0; chunk * DirBlkSiz < data.Length; ++chunk) {
        var start = chunk * DirBlkSiz;
        var prev = -1;
        for (var off = 0; off + 8 <= DirBlkSiz;) {
          var at = start + off;
          var ino = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
          var reclen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
          if (reclen < 8 || off + reclen > DirBlkSiz) break;
          var namlen = data[at + 7];
          if (ino != 0 && namlen == wanted.Length && data.AsSpan(at + 8, namlen).SequenceEqual(wanted))
            return new DirEntry(ino, chunk, off, prev);
          prev = off;
          off += reclen;
        }
      }
      return null;
    }

    public void Link(long dir, string name, long ino, byte type) {
      var data = DirectoryData(dir, out var dirInode);
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var need = MinRecLen(nameBytes.Length);
      for (var chunk = 0; chunk * DirBlkSiz < data.Length; ++chunk) {
        var bytes = data.AsSpan(chunk * DirBlkSiz, DirBlkSiz).ToArray();
        for (var off = 0; off + 8 <= DirBlkSiz;) {
          var entIno = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off));
          var reclen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 4));
          if (reclen < 8 || off + reclen > DirBlkSiz) break;
          var used = entIno == 0 ? 0 : MinRecLen(bytes[off + 7]);
          if (reclen - used >= need) {
            var at = off + used;
            if (used > 0) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off + 4), (ushort)used);
            Entry(bytes, at, ino, reclen - used, type, nameBytes);
            PutDirectoryChunk(dirInode, chunk, bytes);
            Touch(dirInode);
            PutInode(dir, dirInode);
            return;
          }
          off += reclen;
        }
      }
      // No room in any chunk: grow the directory by one chunk.
      var fresh = new byte[DirBlkSiz];
      Entry(fresh, 0, ino, DirBlkSiz, type, nameBytes);
      GrowDirectory(dir, dirInode, fresh);
    }

    private static void Entry(byte[] chunk, int at, long ino, int reclen, byte type, byte[] name) {
      chunk.AsSpan(at, reclen).Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(at), (uint)ino);
      BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(at + 4), (ushort)reclen);
      chunk[at + 6] = type;
      chunk[at + 7] = (byte)name.Length;
      name.CopyTo(chunk, at + 8);
    }

    // Appends one DIRBLKSIZ chunk, extending the last fragment run or taking a new block slot.
    private void GrowDirectory(long dir, byte[] inode, byte[] chunk) {
      var size = Size(inode);
      var newSize = size + DirBlkSiz;
      var lastBlock = (int)((size - 1) / BlockSize);
      var newLastBlock = (int)((newSize - 1) / BlockSize);
      if (newLastBlock >= MaxDirectBlocks) throw new IOException("UFS: directory is full (twelve direct blocks).");
      if (newLastBlock != lastBlock) {
        var at = AllocateFrags(1);
        SetDb(inode, newLastBlock, (int)at);
        var frag = new byte[FragSize];
        chunk.CopyTo(frag, 0);
        Write(at * FragSize, frag);
        AddBlocks(inode, 1);
      } else {
        var held = (int)(((size - lastBlock * (long)BlockSize) + FragSize - 1) / FragSize);
        var needed = (int)(((newSize - lastBlock * (long)BlockSize) + FragSize - 1) / FragSize);
        if (needed > held) {
          var start = Db(inode, lastBlock);
          var cg = start / _fpg;
          var rel = start % _fpg;
          var inPlace = (rel % _frag) + needed <= _frag;
          for (var f = held; inPlace && f < needed; ++f) inPlace = FragFree(cg, rel + f);
          if (inPlace) {
            SetFrags(start + held, needed - held, free: false);
          } else {
            var old = Read((long)start * FragSize, held * FragSize);
            var moved = needed == _frag ? AllocateBlock() : AllocateFrags(needed);
            Write(moved * FragSize, old);
            Release(start, held, wipe: false);
            SetDb(inode, lastBlock, (int)moved);
          }
          AddBlocks(inode, needed - held);
        }
        Write((long)Db(inode, lastBlock) * FragSize + (size - lastBlock * (long)BlockSize), chunk);
      }
      BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(8), newSize);
      Touch(inode);
      PutInode(dir, inode);
    }

    private void AddBlocks(byte[] inode, int frags) {
      var blocks = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(104));
      BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(104), blocks + frags * (FragSize / 512));
    }

    public long MakeDirectory(long parent, string name) {
      var ino = AllocateInode(directory: true);
      var inode = NewInode((ushort)(IfDir | 0x1ED), 2, DirBlkSiz); // 0755
      var chunk = new byte[DirBlkSiz];
      Entry(chunk, 0, ino, 12, DtDir, "."u8.ToArray());
      Entry(chunk, 12, parent, DirBlkSiz - 12, DtDir, ".."u8.ToArray());
      var at = AllocateFrags(1);
      var frag = new byte[FragSize];
      chunk.CopyTo(frag, 0);
      Write(at * FragSize, frag);
      SetDb(inode, 0, (int)at);
      BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(104), FragSize / 512);
      PutInode(ino, inode);

      var parentInode = Inode(parent);
      var links = BinaryPrimitives.ReadInt16LittleEndian(parentInode.AsSpan(2));
      BinaryPrimitives.WriteInt16LittleEndian(parentInode.AsSpan(2), (short)(links + 1));
      PutInode(parent, parentInode);
      Link(parent, name, ino, DtDir);
      return ino;
    }

    public void Unlink(long dir, DirEntry entry) {
      var dirInode = Inode(dir);
      var chunkBytes = DirectoryData(dir, out _).AsSpan(entry.Chunk * DirBlkSiz, DirBlkSiz).ToArray();
      var reclen = BinaryPrimitives.ReadUInt16LittleEndian(chunkBytes.AsSpan(entry.Offset + 4));
      if (entry.Previous >= 0) {
        var prevLen = BinaryPrimitives.ReadUInt16LittleEndian(chunkBytes.AsSpan(entry.Previous + 4));
        BinaryPrimitives.WriteUInt16LittleEndian(chunkBytes.AsSpan(entry.Previous + 4), (ushort)(prevLen + reclen));
        chunkBytes.AsSpan(entry.Offset, reclen).Clear();
      } else {
        // First entry of a chunk: keep the record, mark it unused.
        chunkBytes.AsSpan(entry.Offset, 4).Clear();
        chunkBytes.AsSpan(entry.Offset + 6, reclen - 6).Clear();
      }
      PutDirectoryChunk(dirInode, entry.Chunk, chunkBytes);
      Touch(dirInode);
      PutInode(dir, dirInode);
    }

    // ── commit ──────────────────────────────────────────────────────────

    public void Commit() {
      foreach (var cg in _dirtyGroups) {
        Recount(cg);
        Write(((long)cg * _fpg + _cblkno) * FragSize, Group(cg));
      }
      // fs_cs records (ndir, nbfree, nifree, nffree per group) and both superblock totals.
      long ndir = 0, nbfree = 0, nifree = 0, nffree = 0;
      for (var cg = 0; cg < _ncg; ++cg) {
        var rec = (long)_csaddr * FragSize + cg * 16;
        int[] cs;
        if (_dirtyGroups.Contains(cg)) {
          var block = Group(cg);
          cs = [G(block, 24), G(block, 28), G(block, 32), G(block, 36)];
          var bytes = new byte[16];
          for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), cs[i]);
          Write(rec, bytes);
        } else {
          var bytes = Read(rec, 16);
          cs = [.. Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4)))];
        }
        ndir += cs[0]; nbfree += cs[1]; nifree += cs[2]; nffree += cs[3];
      }
      var oldTotal = new byte[16];
      BinaryPrimitives.WriteInt32LittleEndian(oldTotal, (int)ndir);
      BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(4), (int)nbfree);
      BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(8), (int)nifree);
      BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(12), (int)nffree);
      Write(SuperblockOffset + 192, oldTotal);                       // fs_old_cstotal
      var total = new byte[32];
      BinaryPrimitives.WriteInt64LittleEndian(total, ndir);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(8), nbfree);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(16), nifree);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(24), nffree);
      Write(SuperblockOffset + 1008, total);                         // fs_cstotal

      foreach (var index in _dirty.Order()) {
        _image.Position = index * FragSize;
        _image.Write(_pages[index]);
      }
      _image.Flush();
    }
  }
}
