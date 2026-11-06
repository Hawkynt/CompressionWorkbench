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
  private const ushort IfLnk = 0xA000;
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

  /// <summary>
  /// Makes every file contiguous: each regular file or symlink with blocks moves, pointer
  /// blocks ahead of the data they address, into the lowest (or with <paramref name="atEnd"/>
  /// the highest) run of free blocks inside one cylinder group that holds it whole. Files
  /// are taken in the order of their first block, so the volume packs towards that end.
  /// A file already contiguous moves only when that brings it closer to the end packed
  /// towards; sparse files, directories and files no single run holds stay where they are.
  /// The image keeps its length and every file its bytes. Returns the files moved.
  /// </summary>
  public static int Defragment(Stream image, bool atEnd = false) {
    ArgumentNullException.ThrowIfNull(image);
    // A file that found no room early in a pass may find it once later files have
    // moved, so passes repeat until one moves nothing. Every move brings a file closer
    // to the end packed towards or makes it contiguous, so the passes come to an end.
    var total = 0;
    for (var pass = 0; pass < 64; ++pass) {
      var scan = new Volume(image);
      var files = scan.Walk()
        .Where(e => (e.Mode & IfMt) is IfReg or IfLnk)
        .Select(e => (e.Ino, First: scan.FirstFragment(e.Ino)))
        .Where(e => e.First > 0)
        .ToList();
      files = atEnd ? [.. files.OrderByDescending(e => e.First)] : [.. files.OrderBy(e => e.First)];
      var moved = 0;
      foreach (var (ino, _) in files) {
        var v = new Volume(image);
        if (!v.Relocate(ino, atEnd)) continue;
        v.Commit();
        ++moved;
      }
      if (moved == 0) break;
      total += moved;
    }
    return total;
  }

  /// <summary>
  /// Zeros what no file holds: every fragment the cylinder groups' free maps mark free,
  /// with <paramref name="clusterTips"/> the bytes between a file's (or directory's) end
  /// and the end of its last fragment, and with <paramref name="deletedEntries"/> the
  /// bytes of every directory record past its live name (the remains of removed
  /// entries). Allocation, inodes and live directory entries are untouched. Returns the
  /// bytes zeroed.
  /// </summary>
  public static long Wipe(Stream image, bool clusterTips = true, bool deletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var v = new Volume(image);
    var wiped = v.ZeroSlack(clusterTips, deletedEntries);
    var free = v.FreeRuns();
    v.Commit();
    foreach (var (frag, count) in free) {
      Compression.Core.DiskImage.ExtentCopy.Zero(image, frag * v.FragSize, (long)count * v.FragSize);
      wiped += (long)count * v.FragSize;
    }
    image.Flush();
    return wiped;
  }

  /// <summary>
  /// The volume's layout: each cylinder group's metadata (boot area, superblock copy,
  /// group header, inode table), the <c>fs_cs</c> summary and the root directory as
  /// reserved; every other directory (named with a trailing <c>/</c>), file and symlink
  /// as its runs of fragments, pointer blocks included, in the order the file reads.
  /// </summary>
  public static List<Compression.Registry.DefragBlockInfo> Extents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    return new Volume(image).Extents();
  }

  /// <summary>
  /// Copies <paramref name="length"/> bytes from <paramref name="srcOffset"/> to
  /// <paramref name="dstOffset"/> after checking that both are fragment-aligned and that
  /// every destination fragment is free or part of the source; otherwise refuses with
  /// the image untouched. With <paramref name="zeroSource"/> the source fragments the
  /// destination does not cover are zeroed.
  /// </summary>
  public static void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource) {
    ArgumentNullException.ThrowIfNull(image);
    if (length <= 0 || srcOffset == dstOffset) return;
    var v = new Volume(image);
    v.RequireMovable(srcOffset, dstOffset, length);
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource) {
      // Only the part of the source the destination did not overwrite.
      var end = srcOffset + length;
      var (keepFrom, keepTo) = (dstOffset, dstOffset + length);
      if (srcOffset < keepFrom) Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, Math.Min(end, keepFrom) - srcOffset);
      if (end > keepTo) {
        var from = Math.Max(srcOffset, keepTo);
        Compression.Core.DiskImage.ExtentCopy.Zero(image, from, end - from);
      }
    }
    image.Flush();
  }

  /// <summary>
  /// Repoints the entry <paramref name="path"/> (a directory with a trailing <c>/</c>, as
  /// <see cref="Extents"/> names it) after its fragments in
  /// <c>[oldOffset, oldOffset + length)</c> were copied to <paramref name="newOffset"/>:
  /// direct and indirect pointers (pointer blocks are read where they now lie) and the
  /// free maps are updated. A whole block must land block-aligned and a fragment run
  /// inside one block; a run cut by the range, the root directory, or a destination in
  /// use by something else is refused with the image untouched.
  /// </summary>
  public static void Repoint(Stream image, string path, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    if (length <= 0 || oldOffset == newOffset) return;
    var v = new Volume(image);
    v.Repoint(path, oldOffset, newOffset, length);
    v.Commit();
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
    private long _plan = -1;
    private readonly int _sblkno, _dblkno, _cssize;

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
      _sblkno = I(8); _cblkno = I(12); _iblkno = I(16); _dblkno = I(20); _ncg = I(44); _cssize = I(156);
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
      if (_plan >= 0) {
        // Relocation into a run already found free: block after block, the tail last.
        var planned = _plan;
        SetFrags(planned, count, free: false);
        _plan += _frag;
        return planned;
      }
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
    // A pointer block is allocated ahead of the first data block it addresses, as FFS
    // lays them out, so a file placed into one free run reads front to back.
    private long Place(byte[] inode, ReadOnlySpan<byte> data) {
      long used = 0;
      var blocks = (data.Length + (long)BlockSize - 1) / BlockSize;
      var ptrs = PointersPerBlock;
      byte[]? single = null, dbl = null, leaf = null;
      long singleAt = 0, dblAt = 0, leafAt = 0;
      for (long b = 0; b < blocks; ++b) {
        var k = b - MaxDirectBlocks - ptrs;
        if (b == MaxDirectBlocks) {
          singleAt = AllocateBlock(); used += _frag; single = new byte[BlockSize]; SetIb(inode, 0, singleAt);
        } else if (k >= 0) {
          if (k == 0) { dblAt = AllocateBlock(); used += _frag; dbl = new byte[BlockSize]; SetIb(inode, 1, dblAt); }
          if (k % ptrs == 0) {
            if (leaf != null) Write(leafAt * FragSize, leaf);
            leafAt = AllocateBlock();
            used += _frag;
            leaf = new byte[BlockSize];
            SetPointer(dbl!.AsSpan((int)(k / ptrs) * _pointerSize), leafAt);
          }
        }
        var bytes = (int)Math.Min(BlockSize, data.Length - b * BlockSize);
        var count = b == blocks - 1 && blocks <= MaxDirectBlocks ? (bytes + FragSize - 1) / FragSize : _frag;
        var at = count == _frag ? AllocateBlock() : AllocateFrags(count);
        var chunk = new byte[count * FragSize];
        data.Slice((int)(b * BlockSize), bytes).CopyTo(chunk);
        Write(at * FragSize, chunk);
        used += count;
        if (b < MaxDirectBlocks) SetDb(inode, (int)b, at);
        else if (k < 0) SetPointer(single!.AsSpan((int)(b - MaxDirectBlocks) * _pointerSize), at);
        else SetPointer(leaf!.AsSpan((int)(k % ptrs) * _pointerSize), at);
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

    // ── layout of one inode ─────────────────────────────────────────────

    /// <summary>A run of fragments an inode holds: a data block (its logical number) or a pointer block (-1).</summary>
    private readonly record struct Run(long Frag, int Count, long Logical);

    /// <summary>
    /// Every run the inode holds in the order the file reads, pointer blocks ahead of the
    /// data they address. Pointer blocks are read at <paramref name="at"/>(their address),
    /// which a repoint uses to follow blocks already copied elsewhere. Holes are skipped.
    /// </summary>
    private List<Run> Layout(byte[] inode, Func<long, long>? at = null) {
      at ??= static f => f;
      var runs = new List<Run>();
      var type = BinaryPrimitives.ReadUInt16LittleEndian(inode) & IfMt;
      if (type is not (IfReg or IfDir or IfLnk) || (type == IfLnk && Blocks(inode) == 0)) return runs;
      if (Ib(inode, 2) != 0) throw new NotSupportedException("UFS: files reaching the triple-indirect tree are not relocated.");
      var size = Size(inode);
      var lblocks = (size + BlockSize - 1) / BlockSize;
      var ptrs = PointersPerBlock;
      byte[]? single = null, dbl = null, leaf = null;
      for (long b = 0; b < lblocks; ++b) {
        long data;
        var k = b - MaxDirectBlocks - ptrs;
        if (b < MaxDirectBlocks) {
          data = Db(inode, (int)b);
        } else if (k < 0) {
          if (b == MaxDirectBlocks && Ib(inode, 0) is var s and not 0) {
            runs.Add(new(s, _frag, -1));
            single = Read(at(s) * FragSize, BlockSize);
          }
          data = single == null ? 0 : Pointer(single.AsSpan((int)(b - MaxDirectBlocks) * _pointerSize));
        } else {
          if (k >= (long)ptrs * ptrs) throw new NotSupportedException("UFS: files reaching the triple-indirect tree are not relocated.");
          if (k == 0 && Ib(inode, 1) is var d and not 0) {
            runs.Add(new(d, _frag, -1));
            dbl = Read(at(d) * FragSize, BlockSize);
          }
          if (k % ptrs == 0) {
            leaf = null;
            var l = dbl == null ? 0 : Pointer(dbl.AsSpan((int)(k / ptrs) * _pointerSize));
            if (l != 0) {
              runs.Add(new(l, _frag, -1));
              leaf = Read(at(l) * FragSize, BlockSize);
            }
          }
          data = leaf == null ? 0 : Pointer(leaf.AsSpan((int)(k % ptrs) * _pointerSize));
        }
        if (data == 0) continue;
        var count = b == lblocks - 1 && lblocks <= MaxDirectBlocks
          ? (int)((size - b * BlockSize + FragSize - 1) / FragSize)
          : _frag;
        runs.Add(new(data, count, b));
      }
      return runs;
    }

    private byte[] Content(byte[] inode, List<Run> runs) {
      var size = Size(inode);
      var data = new byte[size];
      foreach (var r in runs) {
        if (r.Logical < 0) continue;
        var n = (int)Math.Min((long)r.Count * FragSize, size - r.Logical * BlockSize);
        Read(r.Frag * FragSize, n).CopyTo(data, r.Logical * BlockSize);
      }
      return data;
    }

    private static bool Sparse(byte[] inode, List<Run> runs, long size, int blockSize)
      => runs.Count(r => r.Logical >= 0) != (size + blockSize - 1) / blockSize;

    // ── the tree ────────────────────────────────────────────────────────

    /// <summary>Every entry below the root once (hard links by their first name), parents before children.</summary>
    public List<(long Ino, string Path, ushort Mode)> Walk() {
      var list = new List<(long, string, ushort)>();
      var seen = new HashSet<long> { RootIno };
      void Visit(long dir, string prefix) {
        var data = DirectoryData(dir, out _);
        foreach (var (_, ino, _, name) in Records(data)) {
          if (ino == 0 || name is "." or ".." || !seen.Add(ino)) continue;
          var mode = Mode(ino);
          var path = prefix + name;
          list.Add((ino, path, mode));
          if ((mode & IfMt) == IfDir) Visit(ino, path + "/");
        }
      }
      Visit(RootIno, "");
      return list;
    }

    /// <summary>The records of a directory: offset, inode, record length and name.</summary>
    private static IEnumerable<(int Offset, long Ino, int RecLen, string Name)> Records(byte[] data) {
      for (var start = 0; start + DirBlkSiz <= data.Length; start += DirBlkSiz)
        for (var off = 0; off + 8 <= DirBlkSiz;) {
          var at = start + off;
          var reclen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
          if (reclen < 8 || off + reclen > DirBlkSiz) break;
          var ino = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
          var namlen = Math.Min((int)data[at + 7], reclen - 8);
          yield return (at, ino, reclen, Encoding.UTF8.GetString(data, at + 8, namlen));
          off += reclen;
        }
    }

    private long Resolve(string path) {
      long ino = RootIno;
      foreach (var part in Split(path)) {
        if ((Mode(ino) & IfMt) != IfDir || Lookup(ino, part) is not { } found)
          throw new FileNotFoundException($"UFS: no entry '{path}'.");
        ino = found.Ino;
      }
      return ino;
    }

    // ── defragmentation ─────────────────────────────────────────────────

    public long FirstFragment(long ino) {
      var runs = Layout(Inode(ino));
      return runs.Count == 0 ? -1 : runs[0].Frag;
    }

    /// <summary>Moves one file into a single run of free blocks; false when it stays.</summary>
    public bool Relocate(long ino, bool atEnd) {
      var inode = Inode(ino);
      var runs = Layout(inode);
      if (runs.Count == 0 || Sparse(inode, runs, Size(inode), BlockSize)) return false;
      var contiguous = true;
      for (var i = 1; i < runs.Count && contiguous; ++i) contiguous = runs[i].Frag == runs[0].Frag + (long)i * _frag;
      var own = new HashSet<long>();
      foreach (var r in runs) for (var f = 0; f < r.Count; ++f) own.Add(r.Frag + f);
      var target = FindRun(runs.Count, runs[^1].Count, own, atEnd);
      if (target < 0 || (contiguous && (atEnd ? target <= runs[0].Frag : target >= runs[0].Frag))) return false;

      var data = Content(inode, runs);
      foreach (var r in runs) SetFrags(r.Frag, r.Count, free: true);
      for (var i = 0; i < MaxDirectBlocks; ++i) SetDb(inode, i, 0);
      for (var level = 0; level < 3; ++level) SetIb(inode, level, 0);
      _plan = target;
      var used = Place(inode, data);
      _plan = -1;
      SetBlocks(inode, used * (FragSize / 512));
      PutInode(ino, inode);
      return true;
    }

    /// <summary>
    /// The first (or last) block-aligned start of <paramref name="blocks"/> consecutive
    /// blocks in one cylinder group whose fragments are free or the file's own — all of
    /// them, except the last block, which needs only its first <paramref name="tail"/>.
    /// </summary>
    private long FindRun(int blocks, int tail, HashSet<long> own, bool atEnd) {
      for (var n = 0; n < _ncg; ++n) {
        var cg = atEnd ? _ncg - 1 - n : n;
        var group = Group(cg);
        var nblk = G(group, 20) / _frag;
        if (nblk < blocks) continue;
        var whole = new bool[nblk];
        var head = new bool[nblk];
        for (var b = 0; b < nblk; ++b) {
          var avail = 0;
          for (var f = 0; f < _frag; ++f) {
            var abs = (long)cg * _fpg + b * _frag + f;
            if (!own.Contains(abs) && !FragFree(cg, b * _frag + f)) break;
            ++avail;
          }
          whole[b] = avail == _frag;
          head[b] = avail >= tail;
        }
        bool Fits(int start) {
          if (!head[start + blocks - 1]) return false;
          for (var i = 0; i < blocks - 1; ++i) if (!whole[start + i]) return false;
          return true;
        }
        if (atEnd) {
          for (var s = nblk - blocks; s >= 0; --s) if (Fits(s)) return (long)cg * _fpg + (long)s * _frag;
        } else {
          for (var s = 0; s + blocks <= nblk; ++s) if (Fits(s)) return (long)cg * _fpg + (long)s * _frag;
        }
      }
      return -1;
    }

    // ── wiping ──────────────────────────────────────────────────────────

    /// <summary>Every run of fragments the free maps mark free.</summary>
    public List<(long Frag, int Count)> FreeRuns() {
      var runs = new List<(long, int)>();
      for (var cg = 0; cg < _ncg; ++cg) {
        var group = Group(cg);
        var ndblk = G(group, 20);
        var freeOff = G(group, 96);
        for (var f = 0; f < ndblk;) {
          if (!Bit(group, freeOff, f)) { ++f; continue; }
          var start = f;
          while (f < ndblk && Bit(group, freeOff, f)) ++f;
          runs.Add(((long)cg * _fpg + start, f - start));
        }
      }
      return runs;
    }

    /// <summary>Zeros the slack after each entry's last byte and inside directory records.</summary>
    public long ZeroSlack(bool tips, bool records) {
      long wiped = 0;
      var targets = Walk().Select(e => (e.Ino, e.Mode)).Prepend((RootIno, IfDir)).ToList();
      foreach (var (ino, mode) in targets) {
        var inode = Inode(ino);
        if (tips) {
          var runs = Layout(inode);
          var size = Size(inode);
          var lblocks = (size + BlockSize - 1) / BlockSize;
          if (runs.Count > 0 && runs[^1].Logical == lblocks - 1) {
            var last = runs[^1];
            var within = size - last.Logical * BlockSize;
            var held = (long)last.Count * FragSize;
            if (within < held) {
              Write(last.Frag * FragSize + within, new byte[held - within]);
              wiped += held - within;
            }
          }
        }
        if (records && (mode & IfMt) == IfDir) {
          var data = DirectoryData(ino, out var dirInode);
          var changed = false;
          foreach (var (at, entIno, reclen, _) in Records(data)) {
            var keep = Math.Min(reclen, entIno == 0 ? 6 : MinRecLen(data[at + 7]));
            var slack = data.AsSpan(at + keep, reclen - keep);
            if (slack.IndexOfAnyExcept((byte)0) < 0) continue;
            wiped += slack.Length;
            slack.Clear();
            changed = true;
          }
          if (changed)
            for (var chunk = 0; chunk * DirBlkSiz < data.Length; ++chunk)
              PutDirectoryChunk(dirInode, chunk, data.AsSpan(chunk * DirBlkSiz, DirBlkSiz));
        }
      }
      return wiped;
    }

    // ── extents and block moves ─────────────────────────────────────────

    public List<Compression.Registry.DefragBlockInfo> Extents() {
      var list = new List<Compression.Registry.DefragBlockInfo>();
      var length = _image.Length;
      void Add(long frag, long count, Compression.Registry.DefragBlockKind kind, string name, Compression.Registry.DefragBlockClass? cls = null) {
        var offset = frag * FragSize;
        var bytes = Math.Min(count * FragSize, length - offset);
        if (bytes > 0) list.Add(new(offset, bytes, kind, name, cls));
      }
      for (var cg = 0; cg < _ncg; ++cg) {
        var from = cg == 0 ? 0 : _sblkno;
        Add((long)cg * _fpg + from, _dblkno - from, Compression.Registry.DefragBlockKind.MetadataReserved, $"UFS cylinder-group metadata (CG {cg})");
      }
      Add(_csaddr, (_cssize + FragSize - 1) / FragSize, Compression.Registry.DefragBlockKind.MetadataReserved, "UFS cylinder-summary area");
      foreach (var r in Layout(Inode(RootIno)))
        Add(r.Frag, r.Count, Compression.Registry.DefragBlockKind.MetadataReserved, "UFS root directory");
      foreach (var (ino, path, mode) in Walk()) {
        var isDir = (mode & IfMt) == IfDir;
        var name = isDir ? path + "/" : path;
        var cls = isDir ? Compression.Registry.DefragBlockClass.Directory : (Compression.Registry.DefragBlockClass?)null;
        long start = -1, count = 0;
        foreach (var r in Layout(Inode(ino))) {
          if (start >= 0 && r.Frag == start + count) { count += r.Count; continue; }
          if (start >= 0) Add(start, count, Compression.Registry.DefragBlockKind.Used, name, cls);
          (start, count) = (r.Frag, r.Count);
        }
        if (start >= 0) Add(start, count, Compression.Registry.DefragBlockKind.Used, name, cls);
      }
      return list;
    }

    public void RequireMovable(long src, long dst, long length) {
      if (src % FragSize != 0 || dst % FragSize != 0)
        throw new NotSupportedException("UFS: block moves are fragment-aligned.");
      var n = (length + FragSize - 1) / FragSize;
      var (from, to) = (src / FragSize, dst / FragSize);
      if (dst + n * FragSize > _image.Length || src + n * FragSize > _image.Length)
        throw new ArgumentOutOfRangeException(nameof(length), "UFS: the move reaches past the image.");
      for (long i = 0; i < n; ++i) {
        var f = to + i;
        if (f >= from && f < from + n) continue;
        var cg = (int)(f / _fpg);
        if (cg >= _ncg || f % _fpg >= G(Group(cg), 20) || !FragFree(cg, (int)(f % _fpg)))
          throw new NotSupportedException($"UFS: destination fragment {f} is not free.");
      }
    }

    public void Repoint(string path, long oldOffset, long newOffset, long length) {
      if (oldOffset % FragSize != 0 || newOffset % FragSize != 0)
        throw new NotSupportedException("UFS: block moves are fragment-aligned.");
      var name = path.TrimEnd('/');
      if (name.Length == 0) throw new NotSupportedException("UFS: the root directory is not moved.");
      var ino = Resolve(name);
      var n = (length + FragSize - 1) / FragSize;
      var (from, to) = (oldOffset / FragSize, newOffset / FragSize);
      bool Moved(long f) => f != 0 && f >= from && f < from + n;
      long Map(long f) => Moved(f) ? to + (f - from) : f;

      var inode = Inode(ino);
      var runs = Layout(inode, Map);
      var moved = runs.Where(r => Moved(r.Frag) || Moved(r.Frag + r.Count - 1)).ToList();
      if (moved.Count == 0) throw new ArgumentException($"UFS: '{path}' holds nothing in that range.", nameof(path));
      foreach (var r in moved) {
        if (!Moved(r.Frag) || !Moved(r.Frag + r.Count - 1))
          throw new NotSupportedException("UFS: a run of fragments is cut by the moved range.");
        var target = Map(r.Frag);
        if (r.Count == _frag ? target % _frag != 0 : target % _frag + r.Count > _frag)
          throw new NotSupportedException("UFS: a whole block must stay block-aligned and a fragment run inside one block.");
      }
      var oldFrags = moved.SelectMany(r => Enumerable.Range(0, r.Count).Select(i => r.Frag + i)).ToHashSet();
      var newFrags = oldFrags.Select(Map).ToHashSet();
      foreach (var f in newFrags.Except(oldFrags))
        if (!FragFree((int)(f / _fpg), (int)(f % _fpg))) throw new NotSupportedException($"UFS: destination fragment {f} is in use.");

      for (var i = 0; i < MaxDirectBlocks; ++i) SetDb(inode, i, Map(Db(inode, i)));
      for (var level = 0; level < 3; ++level) {
        var root = Ib(inode, level);
        if (root == 0) continue;
        RepointTree(root, level + 1, Map);
        SetIb(inode, level, Map(root));
      }
      foreach (var f in oldFrags.Except(newFrags)) SetFrags(f, 1, free: true);
      foreach (var f in newFrags.Except(oldFrags)) SetFrags(f, 1, free: false);
      PutInode(ino, inode);
    }

    private void RepointTree(long block, int level, Func<long, long> map) {
      var at = map(block) * FragSize;
      var table = Read(at, BlockSize);
      for (var i = 0; i < PointersPerBlock; ++i) {
        var span = table.AsSpan(i * _pointerSize, _pointerSize);
        var p = Pointer(span);
        if (p == 0) continue;
        if (level > 1) RepointTree(p, level - 1, map);
        SetPointer(span, map(p));
      }
      Write(at, table);
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
