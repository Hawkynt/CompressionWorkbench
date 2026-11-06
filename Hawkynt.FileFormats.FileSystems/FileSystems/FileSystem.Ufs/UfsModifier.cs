#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// In-place add and remove on little-endian UFS1 and UFS2 volumes, kept consistent the
/// way FreeBSD's <c>fsck_ffs</c> checks them.
/// <list type="bullet">
///   <item>A path is resolved component by component; missing directories are
///   created as real directories (one <c>DIRBLKSIZ</c> chunk holding <c>.</c> and
///   <c>..</c>, the parent's link count raised), never as a root entry whose name
///   holds a slash.</item>
///   <item>A file of up to twelve blocks takes whole blocks plus a tail of fragments
///   in its last block (<c>fragroundup</c>). A larger one takes whole blocks only, the
///   first twelve through <c>di_db</c>, the rest through the single- and
///   double-indirect pointer blocks (32-bit pointers in UFS1, 64-bit in UFS2).
///   <c>di_blocks</c> counts every fragment allocated, pointer blocks included.</item>
///   <item>Allocation searches every cylinder group. After the free-fragment map
///   of a group changes, its derived summaries — <c>cs_nbfree</c>,
///   <c>cs_nffree</c>, <c>cg_frsum</c>, the cluster map and
///   <c>cg_clustersum</c> — are recomputed from that map; the per-group
///   <c>fs_cs</c> records and the superblock totals (<c>fs_cstotal</c>, plus
///   <c>fs_old_cstotal</c> in UFS1) are re-summed. In UFS2 an inode past
///   <c>cg_initediblk</c> first has its inode block zeroed and the mark advanced,
///   as FreeBSD allocates.</item>
///   <item>All writes are staged and land only after every check has passed, so a
///   refused edit leaves the image byte-identical.</item>
/// </list>
/// References: FreeBSD <c>sys/ufs/ffs/fs.h</c> (<c>struct fs</c>, <c>struct cg</c>),
/// <c>sys/ufs/ufs/dinode.h</c> (<c>ufs1_dinode</c>, <c>ufs2_dinode</c>),
/// <c>sys/ufs/ufs/dir.h</c>; McKusick et al., "A Fast File System for UNIX" (1984).
/// </summary>
public static class UfsModifier {
  private const int SuperblockSize = 1376;
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
  /// file needs more than the double-indirect tree, or the volume has no room.</exception>
  public static void AddFile(Stream image, string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    var parts = Split(path);
    var v = new Volume(image);
    if (data.LongLength > v.MaxFileBytes)
      throw new IOException($"UFS: '{path}' is {data.Length} bytes; in-place edits address at most the double-indirect tree ({v.MaxFileBytes} bytes).");

    var dir = v.Directory(parts[..^1], path);
    var name = parts[^1];
    if (v.Lookup(dir, name) != null) throw new IOException($"UFS: entry '{path}' already exists; remove it first to replace.");
    var ino = v.MakeFile(data);
    v.Link(dir, name, ino, DtReg);
    v.Commit();
  }

  /// <summary>Creates the directory <paramref name="path"/> and any missing parents; an existing directory is left as it is.</summary>
  public static void AddDirectory(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    var v = new Volume(image);
    v.Directory(Split(path), path);
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
    private readonly int _frag, _ncg, _ipg, _fpg, _cblkno, _iblkno, _contigSumSize;
    private readonly long _csaddr, _sbOffset;
    private readonly bool _ufs2;
    private readonly int _inodeSize, _pointerSize;
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private int _wholeCursorCg, _wholeCursorBlock;

    public int BlockSize { get; }
    public int FragSize { get; }
    private int PointersPerBlock => BlockSize / _pointerSize;
    public long MaxFileBytes => ((long)MaxDirectBlocks + PointersPerBlock + (long)PointersPerBlock * PointersPerBlock) * BlockSize;

    public Volume(Stream image) {
      _image = image;
      var probe = UfsReader.Probe(image) ?? throw new InvalidDataException("UFS: no UFS1 or UFS2 superblock found.");
      if (probe.BigEndian) throw new NotSupportedException("UFS: big-endian volumes are not edited in place.");
      _ufs2 = probe.Version == 2;
      _sbOffset = probe.Offset;
      var sb = new byte[SuperblockSize];
      image.Position = _sbOffset;
      image.ReadExactly(sb);
      int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(o));
      _cblkno = I(12); _iblkno = I(16); _ncg = I(44);
      BlockSize = I(48); FragSize = I(52); _frag = I(56);
      _ipg = I(184); _fpg = I(188); _contigSumSize = I(1316);
      _csaddr = _ufs2 ? BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(1096)) : I(152);
      _inodeSize = _ufs2 ? 256 : 128;
      _pointerSize = _ufs2 ? 8 : 4;
      if (FragSize < 512 || BlockSize < FragSize || _frag != BlockSize / FragSize || _ipg <= 0 || _fpg <= 0 || _ncg <= 0 || _csaddr <= 0)
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

    // ── inodes (struct ufs1_dinode / struct ufs2_dinode) ────────────────

    private long InodeOffset(long ino) => ((ino / _ipg) * _fpg + _iblkno) * (long)FragSize + (ino % _ipg) * _inodeSize;
    private byte[] Inode(long ino) => Read(InodeOffset(ino), _inodeSize);
    private void PutInode(long ino, byte[] inode) => Write(InodeOffset(ino), inode);
    public ushort Mode(long ino) => BinaryPrimitives.ReadUInt16LittleEndian(Inode(ino));

    private int SizeOffset => _ufs2 ? 16 : 8;
    private int DirectOffset => _ufs2 ? 112 : 40;
    private int IndirectOffset => _ufs2 ? 208 : 88;

    private long Size(byte[] inode) => BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(SizeOffset));
    private void SetSize(byte[] inode, long size) => BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(SizeOffset), size);

    private long Pointer(ReadOnlySpan<byte> s) => _ufs2 ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
    private void SetPointer(Span<byte> s, long value) {
      if (_ufs2) BinaryPrimitives.WriteInt64LittleEndian(s, value);
      else BinaryPrimitives.WriteInt32LittleEndian(s, checked((int)value));
    }

    private long Db(byte[] inode, int i) => Pointer(inode.AsSpan(DirectOffset + i * _pointerSize));
    private void SetDb(byte[] inode, int i, long frag) => SetPointer(inode.AsSpan(DirectOffset + i * _pointerSize), frag);
    private long Ib(byte[] inode, int level) => Pointer(inode.AsSpan(IndirectOffset + level * _pointerSize));
    private void SetIb(byte[] inode, int level, long frag) => SetPointer(inode.AsSpan(IndirectOffset + level * _pointerSize), frag);

    private long Blocks(byte[] inode) => _ufs2 ? BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(24)) : BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(104));
    private void SetBlocks(byte[] inode, long sectors) {
      if (_ufs2) BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(24), sectors);
      else BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(104), checked((uint)sectors));
    }

    private void Touch(byte[] inode) {
      if (_ufs2) {
        BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(40), _now); // di_mtime
        BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(48), _now); // di_ctime
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(24), (uint)_now);
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(32), (uint)_now);
      }
    }

    private byte[] NewInode(ushort mode, short links, long size) {
      var inode = new byte[_inodeSize];
      BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
      BinaryPrimitives.WriteInt16LittleEndian(inode.AsSpan(2), links);
      SetSize(inode, size);
      if (_ufs2) {
        BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(32), _now); // di_atime
        BinaryPrimitives.WriteInt64LittleEndian(inode.AsSpan(56), _now); // di_birthtime
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(16), (uint)_now);
      }
      Touch(inode);
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(_ufs2 ? 80 : 108), (uint)Random.Shared.Next(1, int.MaxValue)); // di_gen
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
        for (var n = 0; n < (pass == 1 ? _ncg + 1 : _ncg); ++n) { // the extra lap revisits the cursor group's start
          // Whole blocks are searched from where the last one was found, so laying out a
          // large file stays linear instead of rescanning the filled prefix every time.
          var cg = pass == 1 ? (_wholeCursorCg + n) % _ncg : n;
          var block = Group(cg);
          var ndblk = G(block, 20);
          for (var b = pass == 1 && n == 0 ? _wholeCursorBlock : 0; b + _frag <= ndblk; b += _frag) {
            var free = 0;
            for (var f = 0; f < _frag; ++f) if (FragFree(cg, b + f)) ++free;
            if (free < count || (pass == 0 && free == _frag) || (pass == 1 && free != _frag)) continue;
            var run = 0;
            for (var f = 0; f < _frag; ++f) {
              run = FragFree(cg, b + f) ? run + 1 : 0;
              if (run == count) {
                var at = (long)cg * _fpg + b + f - count + 1;
                SetFrags(at, count, free: false);
                if (pass == 1) { _wholeCursorCg = cg; _wholeCursorBlock = b; }
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
          if (_ufs2) EnsureInitialized(cg, block, i);
          SetBit(block, iused, i, true);
          SetG(block, 32, G(block, 32) - 1);                 // cs_nifree
          if (directory) SetG(block, 24, G(block, 24) + 1);  // cs_ndir
          _dirtyGroups.Add(cg);
          return ino;
        }
      }
      throw new IOException("UFS: no free inodes.");
    }

    // UFS2 initialises inode blocks lazily: cg_initediblk inodes of the group have
    // been zeroed. FreeBSD keeps it one inode block ahead of the inode it hands out.
    private void EnsureInitialized(int cg, byte[] block, int index) {
      var perBlock = BlockSize / _inodeSize;
      var inited = G(block, 120);
      while (index + perBlock > inited && inited < _ipg) {
        var count = Math.Min(perBlock, _ipg - inited);
        Write(InodeOffset((long)cg * _ipg + inited), new byte[count * _inodeSize]);
        inited += count;
      }
      SetG(block, 120, inited);
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
      SetBlocks(inode, frags * (long)(FragSize / 512));
      var ino = AllocateInode(directory: false);
      PutInode(ino, inode);
      return ino;
    }

    // Lays `data` out and returns the fragments used, pointer blocks included. Up to
    // twelve blocks: whole blocks plus a fragment tail. Beyond: whole blocks through
    // di_db, then the single- and double-indirect trees.
    private long Place(byte[] inode, ReadOnlySpan<byte> data) {
      long used = 0;
      var blocks = (data.Length + (long)BlockSize - 1) / BlockSize;
      var ptrs = PointersPerBlock;
      byte[]? single = null, dbl = null, leaf = null;
      long singleAt = 0, dblAt = 0, leafAt = 0;
      for (long b = 0; b < blocks; ++b) {
        var bytes = (int)Math.Min(BlockSize, data.Length - b * BlockSize);
        var count = b == blocks - 1 && blocks <= MaxDirectBlocks ? (bytes + FragSize - 1) / FragSize : _frag;
        var at = count == _frag ? AllocateBlock() : AllocateFrags(count);
        var chunk = new byte[count * FragSize];
        data.Slice((int)(b * BlockSize), bytes).CopyTo(chunk);
        Write(at * FragSize, chunk);
        used += count;
        if (b < MaxDirectBlocks) {
          SetDb(inode, (int)b, at);
        } else if (b < MaxDirectBlocks + ptrs) {
          if (single == null) { singleAt = AllocateBlock(); used += _frag; single = new byte[BlockSize]; SetIb(inode, 0, singleAt); }
          SetPointer(single.AsSpan((int)(b - MaxDirectBlocks) * _pointerSize), at);
        } else {
          var k = b - MaxDirectBlocks - ptrs;
          if (dbl == null) { dblAt = AllocateBlock(); used += _frag; dbl = new byte[BlockSize]; SetIb(inode, 1, dblAt); }
          if (k % ptrs == 0) {
            if (leaf != null) Write(leafAt * FragSize, leaf);
            leafAt = AllocateBlock();
            used += _frag;
            leaf = new byte[BlockSize];
            SetPointer(dbl.AsSpan((int)(k / ptrs) * _pointerSize), leafAt);
          }
          SetPointer(leaf!.AsSpan((int)(k % ptrs) * _pointerSize), at);
        }
      }
      if (single != null) Write(singleAt * FragSize, single);
      if (leaf != null) Write(leafAt * FragSize, leaf);
      if (dbl != null) Write(dblAt * FragSize, dbl);
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
      // A fast symlink keeps its target in the pointer area: nothing to free.
      if (!((mode & IfMt) == 0xA000 && Blocks(inode) == 0)) {
        var lblocks = (size + BlockSize - 1) / BlockSize;
        for (var b = 0; b < MaxDirectBlocks && b < lblocks; ++b) {
          var frag = Db(inode, b);
          if (frag == 0) continue;
          var count = b == lblocks - 1 && lblocks <= MaxDirectBlocks
            ? (int)(((size - b * (long)BlockSize) + FragSize - 1) / FragSize)
            : _frag;
          Release(frag, count, wipe);
        }
        for (var level = 0; level < 3; ++level) {
          var root = Ib(inode, level);
          if (root != 0) ReleaseTree(root, level + 1, wipe);
        }
      }
      PutInode(ino, new byte[_inodeSize]);
      FreeInode(ino, directory: (mode & IfMt) == IfDir);
    }

    private void ReleaseTree(long pointerBlock, int level, bool wipe) {
      var table = Read(pointerBlock * FragSize, BlockSize);
      for (var i = 0; i < PointersPerBlock; ++i) {
        var p = Pointer(table.AsSpan(i * _pointerSize));
        if (p == 0) continue;
        if (level > 1) ReleaseTree(p, level - 1, wipe);
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

    /// <summary>Walks <paramref name="parts"/> from the root, creating missing directories.</summary>
    public long Directory(string[] parts, string path) {
      long dir = RootIno;
      foreach (var part in parts) {
        if (Lookup(dir, part) is { } found) {
          if ((Mode(found.Ino) & IfMt) != IfDir) throw new IOException($"UFS: '{part}' in '{path}' is not a directory.");
          dir = found.Ino;
        } else {
          dir = MakeDirectory(dir, part);
        }
      }
      return dir;
    }

    private byte[] DirectoryData(long ino, out byte[] inode) {
      inode = Inode(ino);
      var size = Size(inode);
      if (size <= 0 || size % DirBlkSiz != 0 || size > (long)MaxDirectBlocks * BlockSize)
        throw new NotSupportedException($"UFS: directory inode {ino} ({size} bytes) is outside what the in-place editor handles.");
      var data = new byte[size];
      for (var b = 0; b * (long)BlockSize < size; ++b) {
        var n = (int)Math.Min(BlockSize, size - b * (long)BlockSize);
        Read(Db(inode, b) * FragSize, n).CopyTo(data, b * BlockSize);
      }
      return data;
    }

    private void PutDirectoryChunk(byte[] inode, int chunk, ReadOnlySpan<byte> bytes) {
      var at = (long)chunk * DirBlkSiz;
      Write(Db(inode, (int)(at / BlockSize)) * FragSize + at % BlockSize, bytes);
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
        SetDb(inode, newLastBlock, at);
        var frag = new byte[FragSize];
        chunk.CopyTo(frag, 0);
        Write(at * FragSize, frag);
        AddBlocks(inode, 1);
      } else {
        var held = (int)(((size - lastBlock * (long)BlockSize) + FragSize - 1) / FragSize);
        var needed = (int)(((newSize - lastBlock * (long)BlockSize) + FragSize - 1) / FragSize);
        if (needed > held) {
          var start = Db(inode, lastBlock);
          var cg = (int)(start / _fpg);
          var rel = (int)(start % _fpg);
          var inPlace = (rel % _frag) + needed <= _frag;
          for (var f = held; inPlace && f < needed; ++f) inPlace = FragFree(cg, rel + f);
          if (inPlace) {
            SetFrags(start + held, needed - held, free: false);
          } else {
            var old = Read(start * FragSize, held * FragSize);
            var moved = needed == _frag ? AllocateBlock() : AllocateFrags(needed);
            Write(moved * FragSize, old);
            Release(start, held, wipe: false);
            SetDb(inode, lastBlock, moved);
          }
          AddBlocks(inode, needed - held);
        }
        Write(Db(inode, lastBlock) * FragSize + (size - lastBlock * (long)BlockSize), chunk);
      }
      SetSize(inode, newSize);
      Touch(inode);
      PutInode(dir, inode);
    }

    private void AddBlocks(byte[] inode, int frags) => SetBlocks(inode, Blocks(inode) + frags * (long)(FragSize / 512));

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
      SetDb(inode, 0, at);
      SetBlocks(inode, FragSize / 512);
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
      // fs_cs records (ndir, nbfree, nifree, nffree per group) and the superblock totals.
      long ndir = 0, nbfree = 0, nifree = 0, nffree = 0;
      for (var cg = 0; cg < _ncg; ++cg) {
        var rec = _csaddr * FragSize + cg * 16;
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
      if (!_ufs2) {
        var oldTotal = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(oldTotal, (int)ndir);
        BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(4), (int)nbfree);
        BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(8), (int)nifree);
        BinaryPrimitives.WriteInt32LittleEndian(oldTotal.AsSpan(12), (int)nffree);
        Write(_sbOffset + 192, oldTotal);                            // fs_old_cstotal
      }
      var total = new byte[32];
      BinaryPrimitives.WriteInt64LittleEndian(total, ndir);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(8), nbfree);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(16), nifree);
      BinaryPrimitives.WriteInt64LittleEndian(total.AsSpan(24), nffree);
      Write(_sbOffset + 1008, total);                                // fs_cstotal

      foreach (var index in _dirty.Order()) {
        _image.Position = index * FragSize;
        _image.Write(_pages[index]);
      }
      _image.Flush();
    }
  }
}
