#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// True in-place R/W modifier for an OCFS2 (Oracle Cluster Filesystem 2) volume —
/// this package's own or one <c>mkfs.ocfs2</c> made. Performs <b>O(touched
/// blocks)</b> random-access I/O: the directories on the file's path, the
/// allocator groups and dinodes a change moves, the file's dinode and its data
/// clusters. Nothing else is read or rewritten.
///
/// <para>What it writes is what the kernel would: an inode is a bit of slot 0's
/// inode allocator (<see cref="Ocfs2Allocators"/>), data clusters come out of the
/// global bitmap with every group, chain record and used count kept in step, a
/// file small enough to fit its dinode keeps its bytes inline on an inline-data
/// volume, a missing directory on the path is made as an inline directory, and
/// the parent's mtime/ctime (and link count, for a new subdirectory) move with
/// its entries. The result mounts with the Linux driver and passes
/// <c>fsck.ocfs2 -fn</c>.</para>
///
/// <para><b>Scope:</b> regular files in a tree of inline and extent-backed directories.
/// An extent-backed directory is a leaf extent list in its dinode naming 4 KiB blocks of
/// entries; a full one takes one more cluster, a full inline one moves its entries into
/// its first block (the kernel's expansion) on a volume without indexed directories.
/// An indexed directory (or any directory on an indexed-dirs volume once it is not
/// inline, whose blocks end in trailers), a path through a file, a hard-linked
/// file or one carrying an xattr block, shared (reflinked) extents or an extent
/// tree is refused with <see cref="NotSupportedException"/> before anything is
/// written; a volume without room raises <see cref="IOException"/>, also before
/// any write.</para>
/// </summary>
public static class Ocfs2InPlaceModifier {

  private const int BlockSize = Ocfs2Writer.BlockSize;
  private const int ClusterSize = Ocfs2Writer.ClusterSize;
  private const int Id2Offset = 0xC0;

  // ocfs2_dinode field offsets (fs/ocfs2/ocfs2_fs.h).
  private const int OffGeneration = 0x08;     // i_generation (u32)
  private const int OffSuballocSlot = 0x0C;   // i_suballoc_slot (i16)
  private const int OffSuballocBit = 0x0E;    // i_suballoc_bit (u16)
  private const int OffClusters = 0x14;       // i_clusters (u32)
  private const int OffSize = 0x20;           // i_size (u64)
  private const int OffMode = 0x28;           // i_mode (u16)
  private const int OffLinks = 0x2A;          // i_links_count (u16)
  private const int OffFlags = 0x2C;          // i_flags (u32)
  private const int OffAtime = 0x30;          // i_atime (u64)
  private const int OffCtime = 0x38;          // i_ctime (u64)
  private const int OffMtime = 0x40;          // i_mtime (u64)
  private const int OffBlkno = 0x50;          // i_blkno (u64)
  private const int OffFsGeneration = 0x60;   // i_fs_generation (u32)
  private const int OffCtimeNsec = 0x68;      // i_ctime_nsec (u32)
  private const int OffMtimeNsec = 0x6C;      // i_mtime_nsec (u32)
  private const int OffDynFeatures = 0x76;    // i_dyn_features (u16)
  private const int OffXattrLoc = 0x78;       // i_xattr_loc (u64)
  private const int OffRefcountLoc = 0x90;    // i_refcount_loc (u64)
  private const int OffSuballocLoc = 0x98;    // i_suballoc_loc (u64)

  // ocfs2_inline_data header (id_count u16 + 6 reserved) and ocfs2_extent_list
  // header (16 bytes) — records / data start after these.
  private const int InlineHeaderLen = 8;
  private const int ListHeaderLen = 0x10;
  private const int MaxInline = Ocfs2Writer.MaxInline;
  private const int ExtentListCount = Ocfs2Writer.ExtentListCount;

  private const byte FtRegFile = 1;
  private const byte FtDir = 2;
  private const uint ModeDir = 0x4000 | 0x1ED; // drwxr-xr-x

  private const uint InodeValid = 0x00000001;
  private const ushort DynInlineData = 0x0001;
  private const ushort DynHasXattr = 0x0002;
  private const ushort DynHasRefcount = 0x0010;
  private const uint ModeFile = 0x8000 | 0x1A4; // -rw-r--r--

  private static readonly byte[] InodeSignature = "INODE01"u8.ToArray();

  // ── Public API ────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a regular file (missing directories on its path are made). Throws <see cref="IOException"/>
  /// if an entry with that name already exists.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) => Put(image, name, data, replace: false);

  /// <summary>
  /// Adds a regular file, or swaps a file at that path for
  /// the new one: the new inode and data are written first and the entry is
  /// repointed last, so a refusal or a full volume leaves the old file in place.
  /// </summary>
  public static void AddOrReplaceFile(Stream image, string name, byte[] data) => Put(image, name, data, replace: true);

  private static void Put(Stream image, string name, byte[] data, bool replace) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var parts = SplitPath(name);
    var leaf = parts[^1];

    ValidateStream(image);
    var volume = Ocfs2Allocators.Open(image);
    var (chain, missing) = ResolveDirectories(volume, parts[..^1]);
    var parent = chain[^1];

    var exists = missing.Length == 0 && parent.Find(leaf, out _);
    if (exists && !replace)
      throw new IOException($"ocfs2: entry '{name}' already exists.");

    // Everything that can refuse is settled before the first write.
    DirSlot entry = default;
    var grow = false;
    (long Blkno, byte[] Dinode, Ocfs2Allocators.InodeSlot Slot)? old = null;
    if (exists) {
      parent.Find(leaf, out entry);
      if (parent.FileType(entry) != FtRegFile)
        throw new NotSupportedException($"ocfs2: '{name}' is not a regular file.");
      var oldBlk = parent.Inode(entry);
      var oldDinode = volume.ReadDinode(oldBlk);
      EnsureRemovable(oldBlk, oldDinode, name);
      old = (oldBlk, oldDinode, volume.SlotOf(oldBlk, oldDinode));
    } else {
      if (missing.Length > 0 && !volume.InlineData)
        throw new NotSupportedException("ocfs2: new directories are made inline, which this volume's features do not allow.");
      var first = missing.Length > 0 ? missing[0] : leaf;
      if (!parent.HasRoom(DirEntrySize(first))) {
        parent.EnsureGrowable(volume, first);
        grow = true;
      }
    }

    var inline = volume.InlineData && data.Length <= MaxInline;
    var dataClusters = inline ? 0 : (data.Length + ClusterSize - 1) / ClusterSize;

    // One inode per directory still to make, then the file's own.
    var slots = new List<Ocfs2Allocators.InodeSlot>();
    for (var i = 0; i <= missing.Length; ++i) {
      if (volume.AllocateInode() is { } s) { slots.Add(s); continue; }
      foreach (var taken in slots) volume.FreeInode(taken);
      throw new IOException("ocfs2: the inode allocator has no free bits left.");
    }
    var slot = slots[^1];
    List<(long Start, long Count)> runs = [];
    if (dataClusters > 0) {
      var got = volume.AllocateClusters(dataClusters, ExtentListCount);
      if (got == null) {
        foreach (var taken in slots) volume.FreeInode(taken);
        throw new IOException($"ocfs2: no room for the {dataClusters} clusters '{name}' needs.");
      }
      runs = got;
    }
    long? growCluster = null;
    if (grow) {
      var got = volume.AllocateClusters(1, 1);
      if (got == null) {
        foreach (var taken in slots) volume.FreeInode(taken);
        if (runs.Count > 0) volume.SetClusters(runs, used: false);
        throw new IOException($"ocfs2: no room for the cluster the directory holding '{name}' needs to grow.");
      }
      growCluster = got[0].Start;
    }

    // Data first, then the inode that names it, then the entry that names the inode.
    long written = 0;
    foreach (var (start, count) in runs)
      for (var i = 0L; i < count; ++i) {
        var blk = new byte[ClusterSize];
        var take = (int)Math.Min(ClusterSize, data.Length - written);
        if (take > 0) Array.Copy(data, written, blk, 0, take);
        volume.WriteBlock(start + i, blk);
        written += Math.Max(take, 0);
      }

    var now = DateTimeOffset.UtcNow;
    volume.WriteBlock(slot.Blkno, BuildFileDinode(slot, data, inline, runs, volume.Generation, now));

    if (old is { } previous) {
      parent.SetInode(entry, slot.Blkno);
      Touch(parent.Dinode, now);
      parent.Flush(volume);
      Release(volume, previous.Blkno, previous.Dinode, previous.Slot, wipeData: true);
      return;
    }

    // New directories, deepest first, each already naming its child; the one
    // write to the existing parent at the end is what makes them all reachable.
    var child = (Blkno: slot.Blkno, Name: leaf, Type: FtRegFile);
    for (var d = missing.Length - 1; d >= 0; --d) {
      var dirSlot = slots[d];
      var dirParent = d == 0 ? parent.Blkno : slots[d - 1].Blkno;
      var links = (ushort)(child.Type == FtDir ? 3 : 2);
      volume.WriteBlock(dirSlot.Blkno, BuildDirDinode(dirSlot, dirParent, child, links, volume.Generation, now));
      child = (dirSlot.Blkno, missing[d], FtDir);
    }

    if (growCluster is { } cluster) parent.Grow(cluster);
    parent.Insert(DirEntrySize(child.Name), child.Blkno, child.Name, child.Type);
    if (child.Type == FtDir)
      BinaryPrimitives.WriteUInt16LittleEndian(parent.Dinode.AsSpan(OffLinks, 2),
        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(parent.Dinode.AsSpan(OffLinks, 2)) + 1));
    Touch(parent.Dinode, now);
    parent.Flush(volume);
  }

  /// <summary>
  /// Removes a regular file: its entry is spliced out of its directory, its
  /// clusters and inode bit go back to their allocators, and (by default) its
  /// blocks are zeroed. Returns false if no such file exists.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    var parts = SplitPath(name);

    ValidateStream(image);
    var volume = Ocfs2Allocators.Open(image);
    var (chain, missing) = ResolveDirectories(volume, parts[..^1]);
    if (missing.Length > 0) return false;
    var parent = chain[^1];

    if (!parent.Find(parts[^1], out var entry)) return false;

    var inodeBlk = parent.Inode(entry);
    var fileType = parent.FileType(entry);
    if (fileType != FtRegFile)
      throw new NotSupportedException(
        $"ocfs2: refusing to remove non-regular-file entry '{name}' (file_type={fileType}).");

    var dinode = volume.ReadDinode(inodeBlk);
    EnsureRemovable(inodeBlk, dinode, name);
    var slot = volume.SlotOf(inodeBlk, dinode);

    parent.Remove(entry);
    Touch(parent.Dinode, DateTimeOffset.UtcNow);
    parent.Flush(volume);
    Release(volume, inodeBlk, dinode, slot, wipeData);
    return true;
  }

  /// <summary>A path's components; empty and "." / ".." components are refused.</summary>
  private static string[] SplitPath(string name) {
    if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new ArgumentException("name is empty", nameof(name));
    foreach (var p in parts) {
      if (p is "." or "..") throw new ArgumentException($"'{name}' walks out of its directory.", nameof(name));
      if (Encoding.UTF8.GetByteCount(p) > 255) throw new ArgumentException("OCFS2 names are at most 255 bytes.", nameof(name));
    }
    return parts;
  }

  /// <summary>
  /// Walks <paramref name="dirs"/> down from the root through inline and
  /// unindexed extent-backed directories. Returns the directories found (root
  /// first) and the trailing components that do not exist yet. A component that
  /// is not a directory, or a directory this editor does not keep, is refused.
  /// </summary>
  private static (List<Dir> Chain, string[] Missing) ResolveDirectories(Ocfs2Allocators volume, string[] dirs) {
    var chain = new List<Dir> { Dir.Open(volume, volume.RootDirBlkno) };
    for (var i = 0; i < dirs.Length; ++i) {
      var current = chain[^1];
      if (!current.Find(dirs[i], out var at))
        return (chain, dirs[i..]);
      if (current.FileType(at) != FtDir)
        throw new NotSupportedException($"ocfs2: '{dirs[i]}' is not a directory.");
      chain.Add(Dir.Open(volume, current.Inode(at)));
    }
    return (chain, []);
  }

  /// <summary>Where a directory entry sits: area −1 is an inline directory's dinode, area n its n-th block.</summary>
  private readonly record struct DirSlot(int Area, int Offset, int Length);

  /// <summary>
  /// A directory as the editor changes it: inline (its entries in the dinode's inline area) or
  /// extent-backed — a leaf extent list in the dinode naming the clusters whose 4 KiB blocks each
  /// hold a run of entries, the last one stretched to the block's end, and <c>i_size</c> counting
  /// the blocks in use. Indexed directories (<c>OCFS2_INDEXED_DIR_FL</c>), volumes whose directory
  /// blocks end in a trailer (<c>indexed-dirs</c>) and extent trees deeper than the dinode are
  /// refused before anything is written.
  /// </summary>
  private sealed class Dir {
    private const ushort DynIndexedDir = 0x0008;   // OCFS2_INDEXED_DIR_FL

    public long Blkno { get; }
    public byte[] Dinode { get; }
    public bool Inline { get; private set; }
    private readonly List<long> _blocks = [];
    private readonly Dictionary<int, byte[]> _areas = [];
    private readonly HashSet<int> _dirty = [];
    private readonly Ocfs2Allocators _volume;

    private Dir(Ocfs2Allocators volume, long blkno, byte[] dinode) {
      this._volume = volume;
      this.Blkno = blkno;
      this.Dinode = dinode;
    }

    public static Dir Open(Ocfs2Allocators volume, long blkno) {
      var dinode = volume.ReadDinode(blkno);
      var dir = new Dir(volume, blkno, dinode);
      var dyn = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(OffDynFeatures, 2));
      if ((dyn & DynInlineData) != 0) {
        dir.Inline = true;
        return dir;
      }
      if ((dyn & DynIndexedDir) != 0)
        throw new NotSupportedException($"ocfs2: directory inode {blkno} is indexed, which this editor does not keep.");
      if (volume.IndexedDirs)
        throw new NotSupportedException("ocfs2: the volume's directory blocks carry indexed-dirs trailers, which this editor does not keep.");
      if (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset, 2)) != 0)
        throw new NotSupportedException($"ocfs2: directory inode {blkno} has an extent tree deeper than its dinode, which this editor does not walk.");
      var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(OffSize, 8));
      if (size <= 0 || size % BlockSize != 0)
        throw new InvalidDataException($"ocfs2: directory inode {blkno} has a size of {size}, not whole blocks.");
      var cpos = 0L;
      foreach (var (recCpos, start, clusters) in ReadLeafRecords(dinode)) {
        if (recCpos != cpos)
          throw new NotSupportedException($"ocfs2: directory inode {blkno} has a hole in its extent list.");
        for (var c = 0; c < clusters; ++c) dir._blocks.Add(start + c);
        cpos += clusters;
      }
      var used = (int)(size / BlockSize);
      if (used > dir._blocks.Count)
        throw new InvalidDataException($"ocfs2: directory inode {blkno} is larger than the clusters it owns.");
      dir._blocks.RemoveRange(used, dir._blocks.Count - used);
      return dir;
    }

    private IEnumerable<int> Areas => this.Inline ? [-1] : Enumerable.Range(0, this._blocks.Count);

    private byte[] Area(int area) {
      if (area < 0) return this.Dinode;
      if (!this._areas.TryGetValue(area, out var block)) this._areas[area] = block = this._volume.ReadBlock(this._blocks[area]);
      return block;
    }

    private static (int Start, int Capacity) Window(int area) => area < 0 ? InlineWindow() : (0, BlockSize);

    public bool Find(string name, out DirSlot slot) {
      foreach (var area in this.Areas) {
        var (start, capacity) = Window(area);
        if (FindDirEntry(this.Area(area), start, capacity, name, out var offset, out var length)) {
          slot = new DirSlot(area, offset, length);
          return true;
        }
      }
      slot = default;
      return false;
    }

    public long Inode(DirSlot slot) => (long)BinaryPrimitives.ReadUInt64LittleEndian(this.Area(slot.Area).AsSpan(slot.Offset, 8));
    public byte FileType(DirSlot slot) => this.Area(slot.Area)[slot.Offset + 11];

    public void SetInode(DirSlot slot, long inode) {
      BinaryPrimitives.WriteUInt64LittleEndian(this.Area(slot.Area).AsSpan(slot.Offset, 8), (ulong)inode);
      this._dirty.Add(slot.Area);
    }

    public bool HasRoom(int recLen) => this.Areas.Any(area => {
      var (start, capacity) = Window(area);
      return FindSlack(this.Area(area), start, capacity, recLen) >= 0;
    });

    public void Insert(int recLen, long inode, string name, byte type) {
      foreach (var area in this.Areas) {
        var (start, capacity) = Window(area);
        if (FindSlack(this.Area(area), start, capacity, recLen) < 0) continue;
        InsertInlineDirEntry(this.Area(area), start, capacity, recLen, inode, name, type);
        this._dirty.Add(area);
        return;
      }
      throw new InvalidOperationException("ocfs2: the directory lost its room between check and insert.");
    }

    public void Remove(DirSlot slot) {
      var (start, capacity) = Window(slot.Area);
      RemoveInlineDirEntry(this.Area(slot.Area), start, capacity, slot.Offset, slot.Length);
      this._dirty.Add(slot.Area);
    }

    /// <summary>Refuses, before anything is allocated, a directory that cannot take one more block.</summary>
    public void EnsureGrowable(Ocfs2Allocators volume, string name) {
      if (this.Inline) {
        if (volume.IndexedDirs)
          throw new NotSupportedException($"ocfs2: the directory holding '{name}' is full, and on an indexed-dirs volume growing it means indexing it, which this editor does not do.");
        return;
      }
      var records = ReadLeafRecords(this.Dinode);
      if (records.Count >= ExtentListCount)
        throw new NotSupportedException($"ocfs2: the directory holding '{name}' has no extent record left to grow into.");
    }

    /// <summary>
    /// Takes one more cluster (one 4 KiB block): an inline directory moves its entries, repacked, into
    /// the block and becomes extent-backed, as the kernel expands one; an extent-backed directory
    /// appends the block holding one empty entry that spans it.
    /// </summary>
    public void Grow(long cluster) {
      var block = new byte[BlockSize];
      if (this.Inline) {
        var (start, capacity) = InlineWindow();
        var at = 0;
        var last = -1;
        for (var cursor = start; cursor + 12 <= start + capacity;) {
          var recLen = BinaryPrimitives.ReadUInt16LittleEndian(this.Dinode.AsSpan(cursor + 8, 2));
          if (recLen < 12) break;
          var inode = BinaryPrimitives.ReadUInt64LittleEndian(this.Dinode.AsSpan(cursor, 8));
          if (inode != 0) {
            var natural = (12 + this.Dinode[cursor + 10] + 3) & ~3;
            this.Dinode.AsSpan(cursor, natural).CopyTo(block.AsSpan(at));
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at + 8, 2), (ushort)natural);
            last = at;
            at += natural;
          }
          cursor += recLen;
        }
        if (last < 0) throw new InvalidDataException("ocfs2: an inline directory without entries cannot be expanded.");
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(last + 8, 2), (ushort)(BlockSize - last));
        this.Dinode.AsSpan(Id2Offset, BlockSize - Id2Offset).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(this.Dinode.AsSpan(OffDynFeatures, 2),
          (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(this.Dinode.AsSpan(OffDynFeatures, 2)) & ~DynInlineData));
        BinaryPrimitives.WriteUInt16LittleEndian(this.Dinode.AsSpan(Id2Offset + 2, 2), ExtentListCount);
        WriteLeafRecords(this.Dinode, [(0u, cluster, 1L)]);
        this.Inline = false;
      } else {
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), BlockSize);
        var records = ReadLeafRecords(this.Dinode).Select(static r => (r.Cpos, r.Start, (long)r.Clusters)).ToList();
        var (lastCpos, lastStart, lastCount) = records[^1];
        if (lastStart + lastCount == cluster && lastCount < ushort.MaxValue) records[^1] = (lastCpos, lastStart, lastCount + 1);
        else records.Add(((uint)(lastCpos + lastCount), cluster, 1L));
        WriteLeafRecords(this.Dinode, records);
      }
      this._blocks.Add(cluster);
      var area = this._blocks.Count - 1;
      this._areas[area] = block;
      this._dirty.Add(area);
      BinaryPrimitives.WriteUInt32LittleEndian(this.Dinode.AsSpan(OffClusters, 4),
        BinaryPrimitives.ReadUInt32LittleEndian(this.Dinode.AsSpan(OffClusters, 4)) + 1);
      BinaryPrimitives.WriteUInt64LittleEndian(this.Dinode.AsSpan(OffSize, 8), (ulong)this._blocks.Count * BlockSize);
    }

    /// <summary>Writes the changed blocks, then the dinode that names them.</summary>
    public void Flush(Ocfs2Allocators volume) {
      foreach (var area in this._dirty.Where(static a => a >= 0).Order()) volume.WriteBlock(this._blocks[area], this._areas[area]);
      volume.WriteBlock(this.Blkno, this.Dinode);
      this._dirty.Clear();
    }
  }

  /// <summary>
  /// Rewrites a file's contents inside the clusters it already owns; clusters the
  /// shorter payload no longer needs go back to the global bitmap. Returns false
  /// if the named file doesn't exist; throws <see cref="IOException"/> if the new
  /// payload needs more clusters than the file has.
  /// </summary>
  public static bool ReplaceFile(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);

    ValidateStream(image);
    var volume = Ocfs2Allocators.Open(image);
    var root = volume.ReadDinode(volume.RootDirBlkno);
    EnsureInlineDir(root);

    var (inlineStart, inlineCapacity) = InlineWindow();
    if (!FindDirEntry(root, inlineStart, inlineCapacity, name, out var entryOffset, out _))
      return false;

    var inodeBlk = (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(entryOffset, 8));
    if (root[entryOffset + 11] != FtRegFile)
      throw new NotSupportedException($"ocfs2: '{name}' is not a regular file.");

    var dinode = volume.ReadDinode(inodeBlk);
    EnsureRemovable(inodeBlk, dinode, name);
    var now = DateTimeOffset.UtcNow;

    if ((BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(OffDynFeatures, 2)) & DynInlineData) != 0) {
      if (newData.Length > MaxInline)
        throw new IOException($"ocfs2: '{name}' keeps its bytes in its inode, which holds at most {MaxInline}.");
      dinode.AsSpan(Id2Offset + InlineHeaderLen, MaxInline).Clear();
      newData.CopyTo(dinode, Id2Offset + InlineHeaderLen);
      BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(OffSize, 8), (ulong)newData.Length);
      Touch(dinode, now);
      volume.WriteBlock(inodeBlk, dinode);
      return true;
    }

    var records = ReadLeafRecords(dinode);
    var allocated = records.Sum(r => (long)r.Clusters);
    var needed = (newData.Length + (long)ClusterSize - 1) / ClusterSize;
    if (needed > allocated)
      throw new IOException(
        $"ocfs2: in-place replace of '{name}' needs {needed} clusters but only {allocated} allocated.");

    // Keep the first `needed` clusters in file order; the rest go back.
    var kept = new List<(uint Cpos, long Start, long Count)>();
    var freed = new List<(long Start, long Count)>();
    var left = needed;
    foreach (var r in records) {
      var keep = Math.Min(left, r.Clusters);
      if (keep > 0) kept.Add((r.Cpos, r.Start, keep));
      if (r.Clusters > keep) freed.Add((r.Start + keep, r.Clusters - keep));
      left -= keep;
    }

    long written = 0;
    foreach (var (_, start, count) in kept)
      for (var c = 0L; c < count; ++c) {
        var blk = new byte[ClusterSize];
        var take = (int)Math.Min(ClusterSize, newData.Length - written);
        if (take > 0) Array.Copy(newData, written, blk, 0, take);
        volume.WriteBlock(start + c, blk);
        written += Math.Max(take, 0);
      }

    WriteLeafRecords(dinode, kept);
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(OffClusters, 4), (uint)needed);
    BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(OffSize, 8), (ulong)newData.Length);
    Touch(dinode, now);
    volume.WriteBlock(inodeBlk, dinode);

    if (freed.Count > 0) {
      foreach (var (start, count) in freed)
        for (var c = 0L; c < count; ++c) volume.WriteBlock(start + c, new byte[ClusterSize]);
      volume.SetClusters(freed, used: false);
    }
    return true;
  }

  // ── release ───────────────────────────────────────────────────────────

  /// <summary>Returns a file's clusters and inode bit, zeroing what it occupied.</summary>
  private static void Release(Ocfs2Allocators volume, long inodeBlk, byte[] dinode, Ocfs2Allocators.InodeSlot slot, bool wipeData) {
    var runs = (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(OffDynFeatures, 2)) & DynInlineData) != 0
      ? []
      : ReadLeafRecords(dinode).Select(r => (r.Start, (long)r.Clusters)).ToList();
    if (wipeData)
      foreach (var (start, count) in runs)
        for (var c = 0L; c < count; ++c) volume.WriteBlock(start + c, new byte[ClusterSize]);
    if (runs.Count > 0) volume.SetClusters(runs, used: false);
    volume.WriteBlock(inodeBlk, new byte[BlockSize]);
    volume.FreeInode(slot);
  }

  /// <summary>
  /// Refuses a file this editor cannot free without leaving something behind or
  /// taking something another file still uses.
  /// </summary>
  private static void EnsureRemovable(long blkno, byte[] dinode, string name) {
    var dyn = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(OffDynFeatures, 2));
    if (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(OffLinks, 2)) > 1)
      throw new NotSupportedException($"ocfs2: '{name}' has other hard links.");
    if ((dyn & DynHasRefcount) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(OffRefcountLoc, 8)) != 0)
      throw new NotSupportedException($"ocfs2: '{name}' shares extents through a refcount tree.");
    if ((dyn & DynHasXattr) != 0 && BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(OffXattrLoc, 8)) != 0)
      throw new NotSupportedException($"ocfs2: '{name}' keeps extended attributes in a block of their own.");
    if ((dyn & DynInlineData) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset, 2)) != 0)
      throw new NotSupportedException($"ocfs2: '{name}' (inode {blkno}) has an extent tree, which this editor does not walk.");
  }

  // ── Image / dinode helpers ────────────────────────────────────────────

  private static void ValidateStream(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Ocfs2InPlaceModifier: stream must be readable, writable, and seekable.", nameof(image));
  }

  private static void EnsureInlineDir(byte[] dir) {
    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(OffDynFeatures, 2));
    if ((dynFeatures & DynInlineData) == 0)
      throw new NotSupportedException(
        "ocfs2: in-place modifier supports inline-data root directories only; "
        + "extent-backed root directories are refused.");
  }

  /// <summary>Moves a dinode's mtime and ctime to <paramref name="now"/>.</summary>
  private static void Touch(byte[] dinode, DateTimeOffset now) {
    var seconds = (ulong)now.ToUnixTimeSeconds();
    var nanos = (uint)(now.ToUnixTimeMilliseconds() % 1000 * 1_000_000);
    BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(OffCtime, 8), seconds);
    BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(OffMtime, 8), seconds);
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(OffCtimeNsec, 4), nanos);
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(OffMtimeNsec, 4), nanos);
  }

  /// <summary>
  /// The inline dirent area inside a directory dinode. OCFS2 inline directories
  /// fill the whole inline area — the final entry's rec_len stretched to its end.
  /// </summary>
  private static (int Start, int Capacity) InlineWindow() => (Id2Offset + InlineHeaderLen, MaxInline);

  private static List<(uint Cpos, long Start, int Clusters)> ReadLeafRecords(byte[] dinode) {
    var used = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset + 4, 2));
    var records = new List<(uint, long, int)>(used);
    for (var i = 0; i < used; i++) {
      var rec = Id2Offset + ListHeaderLen + i * 16;
      var clusters = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(rec + 4, 2));
      if (clusters == 0) continue;
      records.Add((BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec, 4)), (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(rec + 8, 8)), clusters));
    }
    return records;
  }

  private static void WriteLeafRecords(byte[] dinode, List<(uint Cpos, long Start, long Count)> records) {
    dinode.AsSpan(Id2Offset + ListHeaderLen, ExtentListCount * 16).Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(dinode.AsSpan(Id2Offset + 4, 2), (ushort)records.Count);
    for (var i = 0; i < records.Count; ++i) {
      var rec = Id2Offset + ListHeaderLen + i * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(rec, 4), records[i].Cpos);
      BinaryPrimitives.WriteUInt16LittleEndian(dinode.AsSpan(rec + 4, 2), (ushort)records[i].Count);
      BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(rec + 8, 8), (ulong)records[i].Start);
    }
  }

  // ── Directory entry helpers ───────────────────────────────────────────

  private static int DirEntrySize(string name) => (12 + Encoding.UTF8.GetByteCount(name) + 3) & ~3;

  /// <summary>
  /// Linear-scans the inline dirent area for a matching name. Returns the entry's
  /// absolute byte offset and its rec_len.
  /// </summary>
  private static bool FindDirEntry(byte[] block, int start, int capacity, string name,
                                   out int entryOffset, out int entryLen) {
    entryOffset = -1; entryLen = 0;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var cursor = start;
    var end = start + capacity;
    while (cursor + 12 <= end) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
      var nameLen = block[cursor + 10];
      if (recLen < 12 || cursor + recLen > end) return false;
      if (inode != 0 && nameLen == nameBytes.Length
          && block.AsSpan(cursor + 12, nameLen).SequenceEqual(nameBytes)) {
        entryOffset = cursor;
        entryLen = recLen;
        return true;
      }
      cursor += recLen;
    }
    return false;
  }

  /// <summary>Offset of the first entry with room for <paramref name="newRecLen"/> after its own name; -1 if none.</summary>
  private static int FindSlack(byte[] block, int start, int capacity, int newRecLen) {
    var end = start + capacity;
    for (var cursor = start; cursor + 12 <= end;) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
      if (recLen < 12 || cursor + recLen > end) return -1;
      var naturalLen = inode == 0 ? 0 : (12 + block[cursor + 10] + 3) & ~3;
      if (recLen - naturalLen >= newRecLen) return cursor;
      cursor += recLen;
    }
    return -1;
  }

  /// <summary>
  /// Inserts a new dirent by carving it from the slack of the first entry with
  /// room; the new entry takes all of that slack so the chain stays gap-free.
  /// The caller has checked with <see cref="FindSlack"/> that there is room.
  /// </summary>
  private static void InsertInlineDirEntry(byte[] block, int start, int capacity,
                                           int newRecLen, long inodeBlk, string name, byte fileType) {
    var cursor = FindSlack(block, start, capacity, newRecLen);
    if (cursor < 0) throw new InvalidOperationException("ocfs2: inline directory lost its slack between check and insert.");
    var inode = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor, 8));
    var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
    var naturalLen = inode == 0 ? 0 : (12 + block[cursor + 10] + 3) & ~3;
    var newEntryOff = cursor + naturalLen;
    var newEntryRecLen = recLen - naturalLen;
    if (inode != 0)
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(cursor + 8, 2), (ushort)naturalLen);

    var nameBytes = Encoding.UTF8.GetBytes(name);
    Array.Clear(block, newEntryOff, newEntryRecLen);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(newEntryOff, 8), (ulong)inodeBlk);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(newEntryOff + 8, 2), (ushort)newEntryRecLen);
    block[newEntryOff + 10] = (byte)nameBytes.Length;
    block[newEntryOff + 11] = fileType;
    nameBytes.CopyTo(block.AsSpan(newEntryOff + 12, nameBytes.Length));
  }

  /// <summary>
  /// Removes a dirent by absorbing its rec_len into the previous entry (so the
  /// chain stays contiguous). If the entry is the first in the area, it is marked
  /// unused (inode = 0) instead.
  /// </summary>
  private static void RemoveInlineDirEntry(byte[] block, int start, int capacity,
                                           int entryOffset, int entryLen) {
    var prev = -1;
    var cursor = start;
    while (cursor < entryOffset) {
      prev = cursor;
      cursor += BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
    }
    if (prev >= 0) {
      var prevLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(prev + 8, 2));
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(prev + 8, 2), (ushort)(prevLen + entryLen));
      Array.Clear(block, entryOffset, entryLen);
    } else {
      // No predecessor — blank the inode so it reads as an empty slot.
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(entryOffset, 8), 0);
      block[entryOffset + 10] = 0;
      Array.Clear(block, entryOffset + 12, Math.Max(0, entryLen - 12));
    }
  }

  // ── Directory dinode emission ─────────────────────────────────────────

  /// <summary>
  /// A new inline directory's dinode, as mkdir leaves one: mode 0755, root-owned,
  /// <c>.</c>, <c>..</c> and its one child filling the inline area (the last
  /// entry stretched to its end), <c>i_size</c> the whole inline area.
  /// </summary>
  private static byte[] BuildDirDinode(Ocfs2Allocators.InodeSlot slot, long parentBlkno,
      (long Blkno, string Name, byte Type) child, ushort links, uint fsGeneration, DateTimeOffset now) {
    var block = BuildFileDinode(slot, [], inline: true, [], fsGeneration, now);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffMode, 2), (ushort)ModeDir);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffLinks, 2), links);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffSize, 8), MaxInline);
    var start = Id2Offset + InlineHeaderLen;
    var at = start;
    foreach (var (inode, name, type) in new[] { (slot.Blkno, ".", FtDir), (parentBlkno, "..", FtDir), (child.Blkno, child.Name, child.Type) }) {
      var bytes = Encoding.UTF8.GetBytes(name);
      var recLen = name == child.Name && inode == child.Blkno ? start + MaxInline - at : DirEntrySize(name);
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at, 8), (ulong)inode);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at + 8, 2), (ushort)recLen);
      block[at + 10] = (byte)bytes.Length;
      block[at + 11] = type;
      bytes.CopyTo(block, at + 12);
      at += recLen;
    }
    return block;
  }

  // ── File dinode emission ──────────────────────────────────────────────

  /// <summary>
  /// A regular file's dinode as the kernel writes one: owned by slot 0 at the
  /// allocator bit it came from (<c>i_suballoc_loc</c> naming the group), mode
  /// 0644, root-owned, one link, stamped with the volume's generation and the
  /// current time; bytes inline after the 8-byte <c>ocfs2_inline_data</c> header,
  /// or a leaf extent list with one record per run.
  /// </summary>
  private static byte[] BuildFileDinode(Ocfs2Allocators.InodeSlot slot, byte[] data, bool inline,
      List<(long Start, long Count)> runs, uint fsGeneration, DateTimeOffset now) {
    var block = new byte[BlockSize];
    InodeSignature.CopyTo(block.AsSpan(0, InodeSignature.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffGeneration, 4), fsGeneration);
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(OffSuballocSlot, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffSuballocBit, 2), (ushort)slot.Bit);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffClusters, 4), (uint)runs.Sum(r => r.Count));
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffSize, 8), (ulong)data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffMode, 2), (ushort)ModeFile);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffLinks, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffFlags, 4), InodeValid);
    var seconds = (ulong)now.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffAtime, 8), seconds);
    Touch(block, now);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffBlkno, 8), (ulong)slot.Blkno);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffFsGeneration, 4), fsGeneration);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffSuballocLoc, 8), (ulong)slot.GroupBlkno);

    if (inline) {
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffDynFeatures, 2), DynInlineData);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(Id2Offset, 2), MaxInline); // id_count
      data.CopyTo(block, Id2Offset + InlineHeaderLen);
      return block;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(Id2Offset + 2, 2), ExtentListCount); // l_count
    long cpos = 0;
    WriteLeafRecords(block, runs.Select(r => { var rec = ((uint)cpos, r.Start, r.Count); cpos += r.Count; return rec; }).ToList());
    return block;
  }
}
