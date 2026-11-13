using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Adds and removes files in an existing single-device pool of version 28, the way a
/// transaction group of OpenZFS would (<c>docs/ZFS-ON-DISK.md</c>, "Editing a pool"): every
/// changed block is written anew elsewhere (copy-on-write) up to a new MOS, the blocks it
/// replaces go back to the space maps, the dataset, directory and user accounting follow,
/// and one new uberblock in every label commits it all.
/// </summary>
/// <remarks>
/// The edit is planned completely in memory before a byte is written, so any refusal leaves the
/// image untouched; new blocks are written first and the uberblocks last. Space freed by the edit
/// is not reused by the same edit, so the previous uberblocks still name intact trees. Refused:
/// feature-flag pools, mirrors, RAID-Z and several top-level vdevs, pool checkpoints, freeing a
/// block that a snapshot still holds, gang and deduplicated blocks, an unreplayed intent log,
/// normalised or case-insensitive filesystems, and datasets with a refreservation.
/// </remarks>
internal static class ZfsPoolEditor {
  /// <summary>One file (or, with <c>Data</c> null, one directory) to add or replace.</summary>
  internal sealed record Addition(string Path, byte[]? Data, DateTime? Modified);

  private const int Bp = ZfsConstants.BlockPointerSize;
  private const int Slot = ZfsConstants.DnodeSlotSize;
  private const int MaxDataBlock = 128 * 1024;
  private const int MaxAttempts = 64;
  private const int NewSpaceMapBlock = 4096;
  private const int SmRunMax = 1 << 15;
  private const byte OtObjectArray = 2, OtSpaceMapHeader = 7, OtSpaceMap = 8, OtDnode = 10, OtObjset = 11,
    OtZnode = 17, OtPlainFile = 19, OtDirectory = 20, OtSa = 44;
  private const byte FlagUsedBytes = 1, FlagUserUsedAccounted = 2, FlagSpill = 4;
  private const int UsedHead = 0, UsedChild = 2, UsedChildReserve = 3, UsedRefReservation = 4;
  private const int UserUsedAt = 0x400, GroupUsedAt = 0x600, ProjectUsedAt = 0x800;

  private static readonly string[] SaZnodeLayout = [
    "ZPL_MODE", "ZPL_SIZE", "ZPL_GEN", "ZPL_UID", "ZPL_GID", "ZPL_PARENT", "ZPL_FLAGS",
    "ZPL_ATIME", "ZPL_MTIME", "ZPL_CTIME", "ZPL_CRTIME", "ZPL_LINKS", "ZPL_DACL_COUNT", "ZPL_DACL_ACES",
  ];

  /// <summary>Applies <paramref name="additions"/> and <paramref name="removals"/> to the pool on <paramref name="device"/> in one new transaction group.</summary>
  /// <exception cref="NotSupportedException">The pool or the edit is of a kind this editor refuses; nothing was written.</exception>
  /// <exception cref="ArgumentException">A path is unusable, or a file and a directory would share it.</exception>
  public static void Edit(Stream device, IReadOnlyList<Addition> additions, IReadOnlyList<string> removals) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(additions);
    ArgumentNullException.ThrowIfNull(removals);
    if (!device.CanSeek || !device.CanWrite || !device.CanRead)
      throw new NotSupportedException("ZFS: editing needs a readable, writable, seekable device image.");
    if (additions.Count == 0 && removals.Count == 0) return;

    // the last addition of a path wins
    var adds = additions.GroupBy(a => Normalize(a.Path), StringComparer.Ordinal)
      .Select(g => g.Last() with { Path = g.Key }).Where(a => a.Path.Length > 0).ToList();
    var removes = removals.Select(Normalize).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    var now = DateTime.UtcNow;

    Commit commit;
    device.Position = 0;
    using (var pool = new ZfsPool([device], leaveOpen: true)) {
      var state = new PoolState(pool, device);
      var guess = new Dictionary<int, int>();
      for (var attempt = 0; ; ++attempt) {
        if (attempt == MaxAttempts)
          throw new InvalidOperationException("ZFS: the space maps of the edit did not settle.");
        var plan = new Plan(state, guess, now);
        plan.Apply(adds, removes);
        if (!plan.HasChanges) return;
        if (!plan.Settle(guess)) continue;
        commit = plan.Seal();
        break;
      }
    }
    commit.WriteTo(device);
  }

  private static string Normalize(string path) => (path ?? "").Replace('\\', '/').Trim('/');

  // ── bytes ─────────────────────────────────────────────────────────────────────────────

  private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
  private static void W64(Span<byte> b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[at..], v);
  private static void Add64(Span<byte> b, int at, long delta) => W64(b, at, unchecked(U64(b, at) + (ulong)delta));

  private static bool IsEmbedded(ReadOnlySpan<byte> bp) => ((U64(bp, 0x30) >> 39) & 1) != 0;
  private static bool IsHole(ReadOnlySpan<byte> bp) => !IsEmbedded(bp) && U64(bp, 0) == 0 && U64(bp, 8) == 0;
  private static ulong FillOf(ReadOnlySpan<byte> bp) => IsHole(bp) ? 0 : IsEmbedded(bp) ? 1 : U64(bp, 0x58);

  private static bool IsMetadata(ZfsBlockPointer bp) {
    if (bp.Level > 0) return true;
    if ((bp.Type & 0x80) != 0) return (bp.Type & 0x40) != 0;
    return bp.Type is not (19 or 23 or 25 or 26 or 49);
  }

  private static ulong Span(int epbs, int level) => epbs * level >= 63 ? ulong.MaxValue : 1UL << (epbs * level);

  private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

  // ── pool state (read once, shared by every planning attempt) ──────────────────────────

  private sealed class PoolState {
    public readonly ZfsPool Pool;
    public readonly ulong NewTxg;
    public readonly int Ashift, MetaslabShift, MetaslabCount;
    public readonly ulong MetaslabArray;
    public readonly ulong[] SpaceMaps;
    public readonly int[] SpaceMapBlockSize;
    public readonly ZfsRangeSet[] Allocated;
    public readonly byte[] Uberblock;
    public readonly int SlotSize;
    public readonly byte[] MosRoot;
    public readonly long DeviceLength;
    public readonly int[] AllocationOrder;
    public readonly List<(string Path, ulong Dir, ulong Head)> Datasets = [];

    public PoolState(ZfsPool pool, Stream device) {
      this.Pool = pool;
      if (pool.Version != 28)
        throw new NotSupportedException($"ZFS: only pool version 28 is edited; this pool is version {pool.Version}.");
      if (pool.NewestUberblockTxg != pool.Txg)
        throw new NotSupportedException("ZFS: the newest uberblock does not lead to a readable pool; it is not edited.");
      if (pool.VdevChildren != 1 || pool.TopLevelVdevs.Count != 1)
        throw new NotSupportedException("ZFS: only single-device pools are edited.");
      var config = pool.Config;
      var tree = config.List("vdev_tree") ?? throw new InvalidDataException("ZFS: the label has no vdev tree.");
      if (tree.String("type") is not ("disk" or "file") || tree.Lists("children").Length > 0)
        throw new NotSupportedException($"ZFS: a '{tree.String("type")}' top-level vdev is not edited; only plain devices.");
      if ((config.List("features_for_read")?.Pairs.Count ?? 0) > 0)
        throw new NotSupportedException("ZFS: pools with features are not edited.");

      this.NewTxg = pool.Txg + 1;
      this.Ashift = (int)(tree.UInt64("ashift") ?? throw new InvalidDataException("ZFS: the vdev has no ashift."));
      this.MetaslabShift = (int)(tree.UInt64("metaslab_shift") ?? throw new InvalidDataException("ZFS: the vdev has no metaslab_shift."));
      this.MetaslabArray = tree.UInt64("metaslab_array") ?? throw new InvalidDataException("ZFS: the vdev has no metaslab array.");
      var asize = (long)(tree.UInt64("asize") ?? throw new InvalidDataException("ZFS: the vdev has no asize."));
      if (this.Ashift is < 9 or > 16 || this.MetaslabShift is < 16 or > 40)
        throw new InvalidDataException("ZFS: the vdev geometry is implausible.");
      this.MetaslabCount = (int)(asize >> this.MetaslabShift);
      this.DeviceLength = device.Length;
      if (ZfsConstants.DataStart + ((long)this.MetaslabCount << this.MetaslabShift) > this.DeviceLength)
        throw new InvalidDataException("ZFS: the metaslabs run past the device.");

      // the active uberblock, as bytes, and the MOS it names
      this.SlotSize = 1 << Math.Clamp(this.Ashift, ZfsConstants.MinUberblockShift, ZfsConstants.MaxUberblockShift);
      this.Uberblock = FindUberblock(device, pool.Txg, this.SlotSize)
        ?? throw new InvalidDataException("ZFS: the active uberblock was not found again.");
      if (this.Uberblock.Length >= 0xD0 && U64(this.Uberblock, 0xC8) != 0)
        throw new NotSupportedException("ZFS: the pool has a checkpoint; it is not edited.");
      this.MosRoot = this.Uberblock.AsSpan(0x28, Bp).ToArray();

      // the metaslab array and every space map, replayed
      var mos = pool.Mos;
      var array = mos.RequireDnode(this.MetaslabArray, "metaslab array");
      this.SpaceMaps = new ulong[this.MetaslabCount];
      this.SpaceMapBlockSize = new int[this.MetaslabCount];
      this.Allocated = new ZfsRangeSet[this.MetaslabCount];
      for (var m = 0; m < this.MetaslabCount; ++m) {
        var at = (long)m * 8;
        var block = mos.ReadBlock(array, (ulong)(at / array.DataBlockSize));
        this.SpaceMaps[m] = U64(block, (int)(at % array.DataBlockSize));
        this.Allocated[m] = new ZfsRangeSet();
        if (this.SpaceMaps[m] != 0) this.Replay(m);
      }
      this.AllocationOrder = Enumerable.Range(0, this.MetaslabCount).OrderBy(m => this.SpaceMaps[m] == 0 ? 1 : 0).ThenBy(m => m).ToArray();

      // the filesystems, by the path the reader lists them under
      var directory = mos.Zap(1, "MOS object directory");
      var root = directory.FirstOrDefault(e => e.Name == "root_dataset")
        ?? throw new InvalidDataException("ZFS: the object directory has no root_dataset.");
      this.AddDatasets(root.Value, "", 0);
    }

    private void Replay(int m) {
      var mos = this.Pool.Mos;
      var map = mos.RequireDnode(this.SpaceMaps[m], "space map");
      if (map.Bonus.Length != 24)
        throw new NotSupportedException("ZFS: a space map with a histogram belongs to a feature-flag pool; it is not edited.");
      var length = U64(map.Bonus, 8);
      var expected = (long)U64(map.Bonus, 16);
      this.SpaceMapBlockSize[m] = map.DataBlockSize;
      var start = (long)m << this.MetaslabShift;
      var end = start + (1L << this.MetaslabShift);
      var set = this.Allocated[m];
      byte[]? block = null;
      ulong blockId = ulong.MaxValue;
      for (ulong at = 0; at + 8 <= length; at += 8) {
        var id = at / (ulong)map.DataBlockSize;
        if (id != blockId) {
          block = mos.ReadBlock(map, id);
          blockId = id;
        }
        var word = U64(block!, (int)(at % (ulong)map.DataBlockSize));
        if (word >> 63 != 0) {
          if (word >> 62 == 3)
            throw new NotSupportedException("ZFS: two-word space map entries belong to a feature-flag pool.");
          continue;                                                  // debug entry
        }
        var offset = start + (long)((word >> 16) & ((1UL << 47) - 1)) * (1L << this.Ashift);
        var run = (long)((word & 0x7FFF) + 1) << this.Ashift;
        if (offset < start || offset + run > end)
          throw new InvalidDataException($"ZFS: a space map entry of metaslab {m} lies outside it.");
        if (((word >> 15) & 1) == 0) set.Add(offset, offset + run);
        else set.Remove(offset, offset + run);
      }
      if (set.Total != expected)
        throw new InvalidDataException($"ZFS: metaslab {m} replays to {set.Total} allocated bytes, its space map says {expected}.");
    }

    private void AddDatasets(ulong dirObject, string path, int depth) {
      if (depth > 64) throw new InvalidDataException("ZFS: dataset tree too deep.");
      var mos = this.Pool.Mos;
      var dir = mos.RequireDnode(dirObject, "DSL directory");
      var head = U64(dir.Bonus, 8);
      var children = U64(dir.Bonus, 32);
      if (head != 0) this.Datasets.Add((path, dirObject, head));
      if (children == 0) return;
      foreach (var child in mos.Zap(children, "DSL child map")) {
        if (child.Name.StartsWith('$') || child.Name.Contains('%')) continue;
        this.AddDatasets(child.Value, path.Length == 0 ? child.Name : path + "/" + child.Name, depth + 1);
      }
    }

    private static byte[]? FindUberblock(Stream device, ulong txg, int slotSize) {
      var aligned = device.Length / ZfsConstants.LabelSize * ZfsConstants.LabelSize;
      foreach (var labelAt in LabelOffsets(aligned)) {
        for (var at = 0; at + slotSize <= ZfsConstants.UberblockRingSize; at += slotSize) {
          var offset = labelAt + ZfsConstants.UberblockRingOffset + at;
          var slot = new byte[slotSize];
          device.Position = offset;
          device.ReadExactly(slot);
          if (U64(slot, 0) == ZfsConstants.UberblockMagic && U64(slot, 0x10) == txg && ZfsChecksum.VerifyEmbedded(slot, offset))
            return slot;
        }
      }
      return null;
    }

    public static long[] LabelOffsets(long aligned) => [
      0, ZfsConstants.LabelSize, aligned - 2L * ZfsConstants.LabelSize, aligned - ZfsConstants.LabelSize,
    ];
  }

  // ── blocks and trees ──────────────────────────────────────────────────────────────────

  /// <summary>A block this edit writes: where, how big, and (once sealed) its bytes and pointer.</summary>
  private sealed class NewBlock {
    public long Offset;
    public int Size;
    public int LogicalSize;
    public byte Compression = ZfsConstants.CompressOff;
    public long Allocated;
    public byte Type;
    public int Level;
    public ulong Fill = 1;
    public byte[]? Data;
    public TreeNode[]? Children;
    public byte[]? Pointer;
  }

  /// <summary>A block-pointer slot: kept as it was, a block of this edit, or an indirect block still to be placed.</summary>
  private sealed class TreeNode {
    public byte[]? Raw;
    public NewBlock? Block;
    public TreeNode[]? Pending;

    public static TreeNode Hole() => new() { Raw = new byte[Bp] };
    public bool IsHole => this.Block == null && this.Pending == null && ZfsPoolEditor.IsHole(this.Raw!);
  }

  private static TreeNode[] Holes(int n) {
    var nodes = new TreeNode[n];
    for (var i = 0; i < n; ++i) nodes[i] = TreeNode.Hole();
    return nodes;
  }

  /// <summary>A dnode being edited: its raw bytes (bonus included) and its block tree.</summary>
  private sealed class Dnode {
    public byte[] Raw = [];
    public TreeNode[] Tops = [];
    public int Levels;
    public ulong MaxBlkId;
    public long Used;
    public long OriginalUsed;
    public byte OriginalFlags;
    public bool IsNew;

    public byte Type => this.Raw[0];
    public int IndShift => this.Raw[1];
    public int NBlkPtr => this.Raw[3];
    public byte BonusType => this.Raw[4];
    public byte Flags { get => this.Raw[7]; set => this.Raw[7] = value; }
    public int DataBlockSize {
      get => BinaryPrimitives.ReadUInt16LittleEndian(this.Raw.AsSpan(8)) << ZfsConstants.SectorShift;
      set => BinaryPrimitives.WriteUInt16LittleEndian(this.Raw.AsSpan(8), (ushort)(value >> ZfsConstants.SectorShift));
    }
    public int BonusLength => BinaryPrimitives.ReadUInt16LittleEndian(this.Raw.AsSpan(10));
    public Span<byte> Bonus => this.Raw.AsSpan(0x40 + this.NBlkPtr * Bp, this.BonusLength);

    public static Dnode From(byte[] raw) {
      var d = new Dnode { Raw = raw, Levels = Math.Max((int)raw[2], 1), MaxBlkId = U64(raw, 0x10) };
      var used = (long)U64(raw, 0x18);
      d.Used = (raw[7] & FlagUsedBytes) != 0 ? used : used << ZfsConstants.SectorShift;
      d.OriginalUsed = d.Used;
      d.OriginalFlags = raw[7];
      d.Tops = new TreeNode[raw[3]];
      for (var i = 0; i < d.Tops.Length; ++i)
        d.Tops[i] = new TreeNode { Raw = raw.AsSpan(0x40 + i * Bp, Bp).ToArray() };
      return d;
    }

    public static Dnode Create(byte type, byte bonusType, int bonusLength, int blockSize, int indShift, int nblkptr, byte flags) {
      var raw = new byte[Slot];
      raw[0] = type;
      raw[1] = (byte)indShift;
      raw[2] = 1;
      raw[3] = (byte)nblkptr;
      raw[4] = bonusType;
      raw[7] = flags;
      BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(8), (ushort)(blockSize >> ZfsConstants.SectorShift));
      BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(10), (ushort)bonusLength);
      return new Dnode { Raw = raw, Tops = Holes(nblkptr), Levels = 1, IsNew = true, OriginalFlags = flags };
    }
  }

  /// <summary>One object set being edited.</summary>
  private sealed class Os {
    public required ZfsObjectSet Old;
    public required ZfsDnode OldMeta;
    public required byte[] Block;
    public required byte[] OldRoot;
    public ulong PrevSnapTxg;
    public required Dnode Meta;
    public readonly Dictionary<ulong, Dnode> Loaded = [];
    public readonly SortedSet<ulong> Dirty = [];
    public readonly SortedSet<ulong> Freed = [];
    public readonly Dictionary<int, Dnode> Special = [];
    public readonly Dictionary<ulong, NewBlock?> DnodeBlocks = [];
    public readonly Dictionary<ulong, bool[]> OldOccupancy = [];
    public long Used, Compressed, Uncompressed;
    public NewBlock Root = null!;
    public ulong NextScan = 1;
    public int PerBlock => this.OldMeta.DataBlockSize / Slot;
  }

  /// <summary>One filesystem being edited, and its ZPL state.</summary>
  private sealed class Fs {
    public required string Path;
    public required ulong DirObject;
    public required ulong HeadObject;
    public required Os Os;
    public ulong Root;
    public ulong Version;
    public ZfsSystemAttributes? Sa;
    public int NewLayout = -1;
    public bool Accounting;
    public byte Compression = ZfsConstants.CompressOff;
    public readonly Dictionary<ulong, Znode> Znodes = [];
  }

  private sealed class Znode {
    public ulong Object;
    public required Dnode Dnode;
    public bool IsDirectory;
    public ulong Links;
    public ulong Xattr;
    public ulong AclObject;
    public ulong ExtraZap;
    public ulong Uid, Gid;
    public SortedDictionary<string, ulong>? Entries;
    public bool EntriesChanged;
    public bool LinksChanged;
    public bool Freed;
  }

  private sealed class MapPlan {
    public ulong Object;
    public required Dnode Dnode;
    public required NewBlock[] Blocks;
  }

  /// <summary>The bytes to write: blocks first, then the uberblock in every label.</summary>
  private sealed class Commit {
    public readonly List<(long At, byte[] Data)> Blocks = [];
    public readonly List<(long At, byte[] Data)> Uberblocks = [];

    public void WriteTo(Stream device) {
      foreach (var (at, data) in this.Blocks) {
        device.Position = at;
        device.Write(data);
      }
      device.Flush();
      foreach (var (at, data) in this.Uberblocks) {
        device.Position = at;
        device.Write(data);
      }
      device.Flush();
    }
  }

  // ── one planning attempt ──────────────────────────────────────────────────────────────

  private sealed class Plan {
    private readonly PoolState _s;
    private readonly Dictionary<int, int> _guess;
    private readonly DateTime _now;
    private readonly ZfsRangeSet[] _allocated;
    private readonly ZfsRangeSet[] _busy;
    private readonly HashSet<int> _touched = [];
    private readonly List<NewBlock> _blocks = [];
    private readonly Os _mos;
    private readonly List<Fs> _filesystems = [];
    private readonly Dictionary<int, MapPlan> _maps = [];
    private ulong[]? _newArray;
    private NewBlock[]? _arrayBlocks;

    public Plan(PoolState state, Dictionary<int, int> guess, DateTime now) {
      this._s = state;
      this._guess = guess;
      this._now = now;
      this._allocated = state.Allocated.Select(r => r.Clone()).ToArray();
      this._busy = state.Allocated.Select(r => r.Clone()).ToArray();
      this._mos = this.OpenObjset(state.MosRoot, prevSnapTxg: 0);
    }

    private ulong Txg => this._s.NewTxg;

    // ── space ──────────────────────────────────────────────────────────────

    private long Allocate(long size) {
      foreach (var m in this._s.AllocationOrder) {
        var start = (long)m << this._s.MetaslabShift;
        var gap = this._busy[m].FindGap(start, start + (1L << this._s.MetaslabShift), size, 1L << this._s.Ashift);
        if (gap is not { } at) continue;
        this._busy[m].Add(at, at + size);
        this._allocated[m].Add(at, at + size);
        this._touched.Add(m);
        return at;
      }
      throw new IOException("ZFS: the pool has no free space left for this edit.");
    }

    private void Free(long offset, long size) {
      var m = (int)(offset >> this._s.MetaslabShift);
      if (m < 0 || m >= this._s.MetaslabCount || (offset + size - 1) >> this._s.MetaslabShift != m)
        throw new InvalidDataException($"ZFS: a block at {offset:X} lies outside the metaslabs.");
      this._allocated[m].Remove(offset, offset + size);
      this._touched.Add(m);
    }

    private NewBlock AllocBlock(Os os, Dnode? d, int size, byte type, int level, int logicalSize = 0, byte compression = ZfsConstants.CompressOff) {
      var allocated = Align(size, 1L << this._s.Ashift);
      var logical = logicalSize == 0 ? size : logicalSize;
      var block = new NewBlock {
        Offset = this.Allocate(allocated), Size = size, LogicalSize = logical, Compression = compression,
        Allocated = allocated, Type = type, Level = level,
      };
      this._blocks.Add(block);
      var metadata = level > 0 || type != OtPlainFile;
      os.Used += allocated;
      os.Compressed += size;
      os.Uncompressed += metadata ? size : logical;
      if (d != null) d.Used += allocated;
      return block;
    }

    private void FreePointer(Os os, Dnode? d, byte[] raw) {
      if (IsHole(raw) || IsEmbedded(raw)) return;
      var bp = ZfsBlockPointer.Read(raw);
      if (((U64(raw, 0x30) >> 62) & 1) != 0)
        throw new NotSupportedException("ZFS: freeing a deduplicated block is not supported.");
      if (bp.LogicalBirth <= os.PrevSnapTxg)
        throw new NotSupportedException("ZFS: a snapshot still holds a block the edit would free; it is not edited.");
      long total = 0;
      foreach (var dva in bp.Dvas) {
        if (dva.IsEmpty) continue;
        if (dva.Gang) throw new NotSupportedException("ZFS: freeing a gang block is not supported.");
        if (dva.Vdev != 0) throw new InvalidDataException("ZFS: a block names a vdev the pool does not have.");
        var size = (long)dva.AllocatedSectors << ZfsConstants.SectorShift;
        this.Free(dva.Offset, size);
        total += size;
      }
      os.Used -= total;
      os.Compressed -= bp.PhysicalSize;
      os.Uncompressed -= IsMetadata(bp) ? bp.PhysicalSize : bp.LogicalSize;
      if (d != null) d.Used -= total;
    }

    // ── trees ──────────────────────────────────────────────────────────────

    /// <summary>Frees every block of <paramref name="d"/> (and its spill block when asked) and leaves it empty.</summary>
    private void FreeTree(Os os, Dnode d, bool spill) {
      foreach (var top in d.Tops)
        this.FreeSubtree(os, d, top);
      d.Tops = Holes(d.NBlkPtr);
      d.Levels = 1;
      d.MaxBlkId = 0;
      if (spill && (d.Flags & FlagSpill) != 0) {
        this.FreePointer(os, d, d.Raw.AsSpan(d.Raw.Length - Bp, Bp).ToArray());
        d.Raw.AsSpan(d.Raw.Length - Bp, Bp).Clear();
        d.Flags &= unchecked((byte)~FlagSpill);
      }
    }

    private void FreeSubtree(Os os, Dnode d, TreeNode node) {
      if (node.Block != null || node.Pending != null)
        throw new InvalidOperationException("ZFS: an object was rewritten twice in one edit.");
      var raw = node.Raw!;
      if (IsHole(raw) || IsEmbedded(raw)) return;
      var bp = ZfsBlockPointer.Read(raw);
      if (bp.Level > 0) {
        if (bp.Dvas.Any(v => v.Gang)) throw new NotSupportedException("ZFS: freeing a gang block is not supported.");
        var data = this._s.Pool.ReadBlock(bp, cache: false);
        for (var at = 0; at + Bp <= data.Length; at += Bp)
          this.FreeSubtree(os, d, new TreeNode { Raw = data.AsSpan(at, Bp).ToArray() });
      }
      this.FreePointer(os, d, raw);
    }

    /// <summary>Copy-on-write of the level-0 blocks <paramref name="changes"/> names (null = hole) and of every indirect block above them.</summary>
    private void SetBlocks(Os os, Dnode d, IReadOnlyDictionary<ulong, NewBlock?> changes) {
      if (changes.Count == 0) return;
      var keys = changes.Keys.Order().ToArray();
      var epbs = d.IndShift - 7;
      if (epbs is <= 0 or > 12) throw new InvalidDataException($"ZFS: indirect block shift {d.IndShift} is invalid.");
      while (true) {
        var span = Span(epbs, d.Levels - 1);
        var capacity = span == ulong.MaxValue ? ulong.MaxValue : span * (ulong)d.NBlkPtr;
        if (keys[^1] < capacity) break;
        var children = Holes(1 << epbs);
        for (var i = 0; i < d.Tops.Length; ++i) children[i] = d.Tops[i];
        d.Tops = Holes(d.NBlkPtr);
        d.Tops[0] = new TreeNode { Pending = children };
        ++d.Levels;
      }
      var topSpan = Span(epbs, d.Levels - 1);
      for (var i = 0; i < d.Tops.Length; ++i)
        d.Tops[i] = this.Update(os, d, d.Tops[i], d.Levels - 1, (ulong)i * topSpan, epbs, keys, changes);
      foreach (var key in keys)
        if (changes[key] != null && key > d.MaxBlkId)
          d.MaxBlkId = key;
    }

    private static bool AnyIn(ulong[] keys, ulong first, ulong span) {
      int lo = 0, hi = keys.Length;
      while (lo < hi) {
        var mid = (lo + hi) >> 1;
        if (keys[mid] < first) lo = mid + 1;
        else hi = mid;
      }
      return lo < keys.Length && keys[lo] - first < span;
    }

    private TreeNode Update(Os os, Dnode d, TreeNode node, int level, ulong first, int epbs, ulong[] keys,
        IReadOnlyDictionary<ulong, NewBlock?> changes) {
      var span = Span(epbs, level);
      if (!AnyIn(keys, first, span) && node.Pending == null) return node;
      if (level == 0) {
        if (node.Block != null) throw new InvalidOperationException("ZFS: a block was rewritten twice in one edit.");
        this.FreePointer(os, d, node.Raw!);
        return changes[first] is { } replacement ? new TreeNode { Block = replacement } : TreeNode.Hole();
      }

      TreeNode[] children;
      if (node.Pending != null) {
        children = node.Pending;
      } else if (node.Block != null) {
        throw new InvalidOperationException("ZFS: an indirect block was rewritten twice in one edit.");
      } else if (!IsHole(node.Raw)) {
        var bp = ZfsBlockPointer.Read(node.Raw);
        var data = this._s.Pool.ReadBlock(bp, cache: false);
        if (data.Length != 1 << d.IndShift)
          throw new InvalidDataException("ZFS: an indirect block's size does not match its dnode.");
        children = new TreeNode[data.Length / Bp];
        for (var i = 0; i < children.Length; ++i)
          children[i] = new TreeNode { Raw = data.AsSpan(i * Bp, Bp).ToArray() };
        this.FreePointer(os, d, node.Raw!);
      } else {
        children = Holes(1 << epbs);
      }

      var childSpan = Span(epbs, level - 1);
      for (var i = 0; i < children.Length; ++i)
        children[i] = this.Update(os, d, children[i], level - 1, first + (ulong)i * childSpan, epbs, keys, changes);
      if (children.All(c => c.IsHole)) return TreeNode.Hole();
      var block = this.AllocBlock(os, d, 1 << d.IndShift, d.Type, level);
      block.Children = children;
      return new TreeNode { Block = block };
    }

    /// <summary>
    /// Allocates level-0 blocks holding <paramref name="contents"/> as blocks 0… of <paramref name="d"/>,
    /// compressed with <paramref name="compression"/> where that pays (a block of zeros stays a hole).
    /// </summary>
    private void WriteContents(Os os, Dnode d, IReadOnlyList<byte[]?> contents, int blockSize, byte compression = ZfsConstants.CompressOff) {
      d.DataBlockSize = blockSize;
      var changes = new Dictionary<ulong, NewBlock?>();
      for (var i = 0; i < contents.Count; ++i) {
        if (contents[i] is not { } content) continue;
        var logical = new byte[blockSize];
        content.AsSpan(0, Math.Min(content.Length, blockSize)).CopyTo(logical);
        var (algorithm, physical) = ZfsCompression.Compress(compression, logical, this._s.Ashift);
        if (physical == null) {
          changes[(ulong)i] = null;                                 // zeros: a hole, still within the object
          continue;
        }
        var block = this.AllocBlock(os, d, physical.Length, d.Type, 0, blockSize, algorithm);
        block.Data = physical;
        changes[(ulong)i] = block;
      }
      this.SetBlocks(os, d, changes);
      if (contents.Count > 0 && (ulong)contents.Count - 1 > d.MaxBlkId) d.MaxBlkId = (ulong)contents.Count - 1;
    }

    // ── object sets ────────────────────────────────────────────────────────

    private Os OpenObjset(byte[] rootRaw, ulong prevSnapTxg) {
      var bp = ZfsBlockPointer.Read(rootRaw);
      if (bp.IsEncrypted) throw new NotSupportedException("ZFS: encrypted datasets are not edited.");
      var block = this._s.Pool.ReadBlock(bp, cache: false).ToArray();
      if (block.Length < 1024) throw new InvalidDataException("ZFS: object set block too small.");
      var old = this._s.Pool.ReadObjectSet(bp);
      var oldMeta = ZfsDnode.Read(block) ?? throw new InvalidDataException("ZFS: object set has no meta-dnode.");
      if (oldMeta.DataBlockSize < Slot) throw new InvalidDataException("ZFS: meta-dnode block size invalid.");
      return new Os {
        Old = old, OldMeta = oldMeta, Block = block, OldRoot = rootRaw, PrevSnapTxg = prevSnapTxg,
        Meta = Dnode.From(block.AsSpan(0, Slot).ToArray()),
      };
    }

    private byte[] OldDnodeBlock(Os os, ulong blockId) => os.Old.ReadBlock(os.OldMeta, blockId);

    private bool[] Occupancy(Os os, ulong blockId) {
      if (os.OldOccupancy.TryGetValue(blockId, out var known)) return known;
      var occupied = new bool[os.PerBlock];
      if (blockId <= os.OldMeta.MaxBlockId) {
        var block = this.OldDnodeBlock(os, blockId);
        for (var i = 0; i < occupied.Length;) {
          if (block[i * Slot] == 0) {
            ++i;
            continue;
          }
          var slots = 1 + block[i * Slot + 0x0C];
          for (var j = 0; j < slots && i + j < occupied.Length; ++j) occupied[i + j] = true;
          i += slots;
        }
      }
      return os.OldOccupancy[blockId] = occupied;
    }

    private byte[]? OldRawDnode(Os os, ulong obj) {
      var blockId = obj / (ulong)os.PerBlock;
      if (blockId > os.OldMeta.MaxBlockId) return null;
      var block = this.OldDnodeBlock(os, blockId);
      var at = (int)(obj % (ulong)os.PerBlock) * Slot;
      if (block[at] == 0) return null;
      var size = (1 + block[at + 0x0C]) * Slot;
      if (at + size > block.Length) throw new InvalidDataException("ZFS: a large dnode runs past its block.");
      return block.AsSpan(at, size).ToArray();
    }

    private Dnode Load(Os os, ulong obj) {
      if (os.Loaded.TryGetValue(obj, out var d)) return d;
      if (os.Freed.Contains(obj)) throw new InvalidDataException($"ZFS: object {obj} is used after it was freed.");
      var raw = this.OldRawDnode(os, obj) ?? throw new InvalidDataException($"ZFS: object {obj} is free.");
      return os.Loaded[obj] = Dnode.From(raw);
    }

    private ulong NewObject(Os os, Dnode d) {
      for (var obj = os.NextScan; ; ++obj) {
        if (obj == 0 || os.Loaded.ContainsKey(obj) || os.Freed.Contains(obj)) continue;
        var occupied = this.Occupancy(os, obj / (ulong)os.PerBlock);
        if (occupied[(int)(obj % (ulong)os.PerBlock)]) continue;
        os.NextScan = obj + 1;
        os.Loaded[obj] = d;
        os.Dirty.Add(obj);
        return obj;
      }
    }

    private void FreeObject(Os os, ulong obj) {
      var d = this.Load(os, obj);
      this.FreeTree(os, d, spill: true);
      os.Dirty.Remove(obj);
      os.Freed.Add(obj);
    }

    /// <summary>Plans the dnode blocks the edited objects live in, the meta-dnode above them and the object set block.</summary>
    private void CommitObjset(Os os) {
      var perBlock = (ulong)os.PerBlock;
      var touched = os.Dirty.Concat(os.Freed).Select(o => o / perBlock).Distinct().Order().ToList();
      var changes = new Dictionary<ulong, NewBlock?>();
      foreach (var blockId in touched) {
        var old = blockId <= os.OldMeta.MaxBlockId ? this.OldDnodeBlock(os, blockId) : null;
        ulong count = 0;
        for (var i = 0; i < (int)perBlock;) {
          var obj = blockId * perBlock + (ulong)i;
          var oldSlots = old != null && old[i * Slot] != 0 ? 1 + old[i * Slot + 0x0C] : 1;
          if (os.Freed.Contains(obj)) {
            i += oldSlots;
            continue;
          }
          if (os.Dirty.Contains(obj)) {
            ++count;
            i += os.Loaded[obj].Raw.Length / Slot;
            continue;
          }
          if (old != null && old[i * Slot] != 0) ++count;
          i += oldSlots;
        }
        NewBlock? block = null;
        if (count > 0) {
          block = this.AllocBlock(os, os.Meta, os.OldMeta.DataBlockSize, OtDnode, 0);
          block.Fill = count;
        }
        changes[blockId] = block;
        os.DnodeBlocks[blockId] = block;
      }
      this.SetBlocks(os, os.Meta, changes);
      this.FreePointer(os, null, os.OldRoot);
      os.Root = this.AllocBlock(os, null, os.Block.Length, OtObjset, 0);
    }

    // ── sealing ────────────────────────────────────────────────────────────

    private byte[] SealNode(TreeNode node) {
      if (node.Pending != null) throw new InvalidOperationException("ZFS: an indirect block was never placed.");
      if (node.Block is not { } b) return node.Raw!;
      if (b.Pointer != null) return b.Pointer;
      var data = new byte[b.Size];
      if (b.Children != null) {
        ulong fill = 0;
        for (var i = 0; i < b.Children.Length; ++i) {
          var raw = this.SealNode(b.Children[i]);
          raw.CopyTo(data, i * Bp);
          fill += FillOf(raw);
        }
        b.Fill = fill;
      } else {
        var content = b.Data ?? throw new InvalidOperationException("ZFS: a block was never given its contents.");
        content.AsSpan(0, Math.Min(content.Length, data.Length)).CopyTo(data);
      }
      b.Data = data;
      var p = new byte[Bp];
      var sectors = (ulong)(b.Size >> ZfsConstants.SectorShift);
      var logical = (ulong)(b.LogicalSize >> ZfsConstants.SectorShift);
      W64(p, 0x00, (ulong)(b.Allocated >> ZfsConstants.SectorShift));         // DVA 0: asize, vdev 0
      W64(p, 0x08, (ulong)b.Offset >> ZfsConstants.SectorShift);              // offset, not ganged
      W64(p, 0x30, (logical - 1) | (sectors - 1) << 16
        | (ulong)b.Compression << 32 | (ulong)ZfsConstants.ChecksumFletcher4 << 40
        | (ulong)b.Type << 48 | (ulong)b.Level << 56 | 1UL << 63);
      W64(p, 0x48, this.Txg);
      W64(p, 0x50, this.Txg);
      W64(p, 0x58, b.Fill);
      ZfsChecksum.Fletcher4(data, p.AsSpan(0x60, 32));
      return b.Pointer = p;
    }

    private void SealDnode(Dnode d) {
      for (var i = 0; i < d.Tops.Length; ++i)
        this.SealNode(d.Tops[i]).CopyTo(d.Raw, 0x40 + i * Bp);
      d.Raw[2] = (byte)d.Levels;
      W64(d.Raw, 0x10, d.MaxBlkId);
      W64(d.Raw, 0x18, (ulong)((d.Flags & FlagUsedBytes) != 0 ? d.Used : d.Used >> ZfsConstants.SectorShift));
    }

    private byte[] SealObjset(Os os) {
      foreach (var obj in os.Dirty)
        this.SealDnode(os.Loaded[obj]);
      foreach (var (at, special) in os.Special) {
        this.SealDnode(special);
        special.Raw.CopyTo(os.Block, at);
      }
      var perBlock = (ulong)os.PerBlock;
      foreach (var (blockId, block) in os.DnodeBlocks) {
        if (block == null) continue;
        var data = blockId <= os.OldMeta.MaxBlockId ? this.OldDnodeBlock(os, blockId).ToArray() : new byte[os.OldMeta.DataBlockSize];
        for (var i = 0; i < (int)perBlock; ++i) {
          var obj = blockId * perBlock + (ulong)i;
          if (os.Freed.Contains(obj)) {
            var slots = data[i * Slot] != 0 ? 1 + data[i * Slot + 0x0C] : 1;
            data.AsSpan(i * Slot, Math.Min(slots * Slot, data.Length - i * Slot)).Clear();
          } else if (os.Dirty.Contains(obj)) {
            var raw = os.Loaded[obj].Raw;
            raw.CopyTo(data, i * Slot);
          }
        }
        block.Data = data;
      }
      this.SealDnode(os.Meta);
      os.Meta.Raw.CopyTo(os.Block, 0);
      ulong fill = 0;
      for (var i = 0; i < os.Meta.Tops.Length; ++i)
        fill += FillOf(os.Meta.Raw.AsSpan(0x40 + i * Bp, Bp));
      os.Root.Data = os.Block;
      os.Root.Fill = fill;
      return this.SealNode(new TreeNode { Block = os.Root });
    }

    // ── the edit ───────────────────────────────────────────────────────────

    public void Apply(IReadOnlyList<Addition> additions, IReadOnlyList<string> removals) {
      foreach (var path in removals) {
        var (fs, rel) = this.Locate(path);
        if (rel.Length == 0) throw new NotSupportedException($"ZFS: '{path}' is a dataset, not a file or directory; datasets are not removed.");
        this.Remove(fs, rel.Split('/'));
      }
      foreach (var addition in additions) {
        var (fs, rel) = this.Locate(addition.Path);
        if (rel.Length == 0) {
          if (addition.Data == null) continue;
          throw new ArgumentException($"ZFS: '{addition.Path}' is a dataset; a file cannot take its place.");
        }
        this.Add(fs, rel.Split('/'), addition);
      }
      foreach (var fs in this._filesystems)
        this.Flush(fs);
      this._filesystems.RemoveAll(fs => fs.Os.Dirty.Count == 0 && fs.Os.Freed.Count == 0 && fs.Os.Special.Count == 0);
      if (this._filesystems.Count == 0) return;
      this.HasChanges = true;
      this.PlanMos();
    }

    /// <summary>False when the edit changes nothing (every removal named something absent): nothing is written.</summary>
    public bool HasChanges { get; private set; }

    private (Fs Fs, string Relative) Locate(string path) {
      foreach (var (dsPath, dir, head) in this._s.Datasets.OrderByDescending(d => d.Path.Length)) {
        string rel;
        if (dsPath.Length == 0) rel = path;
        else if (path == dsPath) rel = "";
        else if (path.StartsWith(dsPath + "/", StringComparison.Ordinal)) rel = path[(dsPath.Length + 1)..];
        else continue;
        if (rel.Split('/')[0] == ".zfs")
          throw new NotSupportedException("ZFS: snapshots are read-only.");
        return (this.OpenFilesystem(dsPath, dir, head), rel);
      }
      throw new FileNotFoundException($"ZFS: no dataset holds '{path}'.", path);
    }

    private Fs OpenFilesystem(string path, ulong dirObject, ulong headObject) {
      var existing = this._filesystems.FirstOrDefault(f => f.HeadObject == headObject);
      if (existing != null) return existing;

      var mos = this._mos.Old;
      var head = mos.RequireDnode(headObject, "DSL dataset");
      var dir = mos.RequireDnode(dirObject, "DSL directory");
      var props = U64(dir.Bonus, 80);
      if (props != 0 && mos.Zap(props, "DSL properties").Any(p => p.Name == "refreservation" && p.Value != 0))
        throw new NotSupportedException("ZFS: datasets with a refreservation are not edited.");
      var root = head.Bonus.AsSpan(128, Bp).ToArray();
      if (IsHole(root)) throw new NotSupportedException($"ZFS: dataset '{path}' has no object set.");
      var os = this.OpenObjset(root, U64(head.Bonus, 16));
      if (os.Old.Type != ZfsConstants.OstZfs) throw new NotSupportedException($"ZFS: dataset '{path}' is not a filesystem.");
      // A clean export leaves the next log block allocated but unwritten; a written one holds records still to replay.
      if (U64(os.Block, 0x200) != 0 || this.LogBlockWritten(os.Block.AsSpan(0x210, Bp).ToArray()))
        throw new NotSupportedException($"ZFS: dataset '{path}' has an intent log that was never replayed.");
      if (os.Block.Length >= ProjectUsedAt + Slot && os.Block[ProjectUsedAt] != 0)
        throw new NotSupportedException($"ZFS: dataset '{path}' has a project-quota object set; it is not edited.");

      var fs = new Fs { Path = path, DirObject = dirObject, HeadObject = headObject, Os = os };
      var master = os.Old.Zap(1, "ZPL master node").ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
      fs.Root = master.TryGetValue("ROOT", out var rootObject) ? rootObject : throw new InvalidDataException("ZFS: the master node has no ROOT.");
      fs.Version = master.GetValueOrDefault("VERSION");
      if (fs.Version is < 3 or > 5)
        throw new NotSupportedException($"ZFS: ZPL version {fs.Version} is not edited.");
      if (master.GetValueOrDefault("normalization") != 0 || master.GetValueOrDefault("casesensitivity") != 0)
        throw new NotSupportedException("ZFS: normalised or case-insensitive filesystems are not edited.");
      if (master.TryGetValue("SA_ATTRS", out var saObject)) {
        fs.Sa = new ZfsSystemAttributes(os.Old, saObject);
        foreach (var (number, names) in fs.Sa.Layouts)
          if (names.SequenceEqual(SaZnodeLayout)) {
            fs.NewLayout = number;
            break;
          }
      }
      fs.Accounting = os.Block.Length >= 2048 && os.Block[UserUsedAt] != 0 && os.Block[GroupUsedAt] != 0;
      fs.Compression = this.EffectiveCompression(dirObject);
      this._filesystems.Add(fs);
      return fs;
    }

    /// <summary>
    /// Whether the first block of an intent-log chain was written: its embedded checksum holds
    /// (<c>docs/ZFS-ON-DISK.md</c>, "Intent log"). A block that fails it was only allocated.
    /// </summary>
    private bool LogBlockWritten(byte[] raw) {
      if (IsHole(raw)) return false;
      if (IsEmbedded(raw)) return true;
      var bp = ZfsBlockPointer.Read(raw);
      if (bp.Dvas[0].Gang || bp.Dvas[0].Vdev != 0) return true;
      var data = this._s.Pool.TopLevelVdevs[0].Read(bp.Dvas[0].Offset, bp.PhysicalSize);
      int eck, size;
      switch (bp.Checksum) {
        case ZfsConstants.ChecksumZilog2:
          eck = 144;
          if (U64(data, eck) != ZfsConstants.EmbeddedChecksumMagic) return false;
          var used = U64(data, 136);
          if (used > (ulong)data.Length) return false;
          size = (int)Align((long)used, 4096);
          if (size > data.Length) return false;
          break;
        case ZfsConstants.ChecksumZilog:
          eck = data.Length - 40;
          if (U64(data, eck) != ZfsConstants.EmbeddedChecksumMagic) return false;
          size = data.Length;
          break;
        default:
          return true;
      }
      var stored = data.AsSpan(eck + 8, 32).ToArray();
      raw.AsSpan(0x60, 32).CopyTo(data.AsSpan(eck + 8));            // the verifier is the pointer's checksum
      var actual = new byte[32];
      if (bp.Checksum == ZfsConstants.ChecksumZilog2) ZfsChecksum.Fletcher4(data.AsSpan(0, size), actual);
      else ZfsChecksum.Fletcher2(data.AsSpan(0, size), actual);
      return actual.AsSpan().SequenceEqual(stored);
    }

    // ── ZPL ────────────────────────────────────────────────────────────────

    private Znode GetZnode(Fs fs, ulong obj) {
      if (fs.Znodes.TryGetValue(obj, out var z)) return z;
      var d = this.Load(fs.Os, obj);
      z = new Znode { Object = obj, Dnode = d };
      ulong mode;
      if (d.BonusType == OtZnode && d.BonusLength >= ZfsConstants.OldZnodeSize) {
        var b = d.Bonus;
        mode = U64(b, 72);
        z.Links = U64(b, 96);
        z.Xattr = U64(b, 104);
        z.Uid = U64(b, 128);
        z.Gid = U64(b, 136);
        z.ExtraZap = U64(b, 144);
        z.AclObject = U64(b, 176);
      } else if (d.BonusType == OtSa && fs.Sa != null) {
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        fs.Sa.Parse(d.Bonus, values);
        if ((d.Flags & FlagSpill) != 0)
          fs.Sa.Parse(this._s.Pool.ReadBlock(ZfsBlockPointer.Read(d.Raw.AsSpan(d.Raw.Length - Bp))), values);
        ulong Value(string name) => values.TryGetValue(name, out var v) && v.Length >= 8 ? U64(v, 0) : 0;
        mode = Value("ZPL_MODE");
        z.Links = Value("ZPL_LINKS");
        z.Xattr = Value("ZPL_XATTR");
        z.Uid = Value("ZPL_UID");
        z.Gid = Value("ZPL_GID");
      } else {
        throw new InvalidDataException($"ZFS: object {obj} is not a file or directory.");
      }
      z.IsDirectory = (mode & ZfsConstants.ModeTypeMask) == ZfsConstants.ModeDirectory;
      return fs.Znodes[obj] = z;
    }

    private SortedDictionary<string, ulong> Entries(Fs fs, Znode dir) {
      if (dir.Entries != null) return dir.Entries;
      var dnode = fs.Os.Old.RequireDnode(dir.Object, "directory");
      var entries = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
      foreach (var e in fs.Os.Old.Zap(dnode))
        entries[e.Name] = e.Value;
      return dir.Entries = entries;
    }

    private static void CheckName(string name) {
      if (name.Length == 0 || name is "." or ".." || name.Contains('\0') || Encoding.UTF8.GetByteCount(name) > 255)
        throw new ArgumentException($"ZFS: '{name}' is not a usable file name.");
    }

    private void Add(Fs fs, string[] parts, Addition addition) {
      var dir = this.GetZnode(fs, fs.Root);
      for (var i = 0; i < parts.Length - 1; ++i) {
        CheckName(parts[i]);
        if (this.Entries(fs, dir).TryGetValue(parts[i], out var value)) {
          var child = this.GetZnode(fs, value & ZfsConstants.DirentObjectMask);
          if (!child.IsDirectory) throw new ArgumentException($"ZFS: '{addition.Path}' needs '{parts[i]}' to be a directory, but it is a file.");
          dir = child;
        } else {
          dir = this.CreateObject(fs, dir, parts[i], null, this._now);
        }
      }
      var name = parts[^1];
      CheckName(name);
      if (this.Entries(fs, dir).TryGetValue(name, out var existing)) {
        var child = this.GetZnode(fs, existing & ZfsConstants.DirentObjectMask);
        if (addition.Data == null) {
          if (child.IsDirectory) return;
          throw new ArgumentException($"ZFS: '{addition.Path}' is a file; a directory cannot take its place.");
        }
        if (child.IsDirectory) throw new ArgumentException($"ZFS: '{addition.Path}' is a directory; a file cannot take its place.");
        this.Unlink(fs, dir, name);
      }
      this.CreateObject(fs, dir, name, addition.Data, addition.Modified?.ToUniversalTime() ?? this._now);
    }

    /// <summary>Removes <paramref name="parts"/> if it exists; a name that is already absent is left so.</summary>
    private void Remove(Fs fs, string[] parts) {
      var dir = this.GetZnode(fs, fs.Root);
      for (var i = 0; i < parts.Length - 1; ++i) {
        if (!this.Entries(fs, dir).TryGetValue(parts[i], out var value)) return;
        dir = this.GetZnode(fs, value & ZfsConstants.DirentObjectMask);
        if (!dir.IsDirectory) return;
      }
      if (this.Entries(fs, dir).ContainsKey(parts[^1]))
        this.Unlink(fs, dir, parts[^1]);
    }

    private void Unlink(Fs fs, Znode dir, string name) {
      var entries = this.Entries(fs, dir);
      var value = entries[name];
      entries.Remove(name);
      dir.EntriesChanged = true;
      var child = this.GetZnode(fs, value & ZfsConstants.DirentObjectMask);
      if (child.IsDirectory) {
        this.RemoveTree(fs, child, 0);
        --dir.Links;
        dir.LinksChanged = true;
        return;
      }
      this.DropLink(fs, child);
    }

    private void DropLink(Fs fs, Znode file) {
      if (file.Links <= 1) {
        this.FreeZnode(fs, file);
        return;
      }
      --file.Links;
      file.LinksChanged = true;
    }

    private void RemoveTree(Fs fs, Znode dir, int depth) {
      if (depth > 1024) throw new InvalidDataException("ZFS: directory tree too deep.");
      foreach (var (_, value) in this.Entries(fs, dir).ToList()) {
        var child = this.GetZnode(fs, value & ZfsConstants.DirentObjectMask);
        if (child.IsDirectory) this.RemoveTree(fs, child, depth + 1);
        else this.DropLink(fs, child);
      }
      dir.Entries!.Clear();
      this.FreeZnode(fs, dir);
    }

    private void FreeZnode(Fs fs, Znode z) {
      if (z.Freed) return;
      z.Freed = true;
      if (z.ExtraZap != 0) throw new NotSupportedException("ZFS: objects with an extra attribute ZAP are not removed.");
      if (z.Xattr != 0) this.RemoveTree(fs, this.GetZnode(fs, z.Xattr), 0);
      if (z.AclObject != 0) this.FreeObject(fs.Os, z.AclObject);
      this.FreeObject(fs.Os, z.Object);
    }

    /// <summary>Creates a file (<paramref name="data"/> set) or a directory named <paramref name="name"/> in <paramref name="parent"/>.</summary>
    private Znode CreateObject(Fs fs, Znode parent, string name, byte[]? data, DateTime modified) {
      var isDirectory = data == null;
      var size = isDirectory ? 2UL : (ulong)data!.LongLength;
      var links = isDirectory ? 2UL : 1UL;
      var flags = (byte)(FlagUsedBytes | (fs.Accounting ? FlagUserUsedAccounted : 0));
      var type = isDirectory ? OtDirectory : OtPlainFile;
      var blockSize = isDirectory || data!.Length == 0 ? 512
        : data.Length > MaxDataBlock ? MaxDataBlock : (int)Align(data.Length, 512);
      Dnode d;
      if (fs.Version >= 5) {
        if (fs.Sa == null || fs.NewLayout < 0)
          throw new NotSupportedException("ZFS: the filesystem registers no system-attribute layout for new files.");
        d = Dnode.Create(type, OtSa, 320, blockSize, 17, 1, flags);
      } else {
        d = Dnode.Create(type, OtZnode, ZfsConstants.OldZnodeSize, blockSize, 17, 1, flags);
      }
      var obj = this.NewObject(fs.Os, d);
      if (fs.Version >= 5)
        this.WriteSaBonus(fs, d.Bonus, isDirectory, modified, parent.Object, size, links);
      else
        ZfsWriter.ZnodeBonus(isDirectory, modified, parent.Object, size, links, this.Txg).CopyTo(d.Bonus);

      var z = new Znode { Object = obj, Dnode = d, IsDirectory = isDirectory, Links = links };
      fs.Znodes[obj] = z;
      if (isDirectory) {
        z.Entries = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        z.EntriesChanged = true;
        ++parent.Links;
        parent.LinksChanged = true;
      } else if (data!.Length > 0) {
        var blocks = new List<byte[]?>();
        for (var at = 0; at < data.Length; at += blockSize)
          blocks.Add(data.AsSpan(at, Math.Min(blockSize, data.Length - at)).ToArray());
        this.WriteContents(fs.Os, d, blocks, blockSize, fs.Compression);
      }
      this.Entries(fs, parent)[name] = obj | (isDirectory ? 4UL : 8UL) << ZfsConstants.DirentTypeShift;
      parent.EntriesChanged = true;
      return z;
    }

    private void WriteSaBonus(Fs fs, Span<byte> bonus, bool isDirectory, DateTime modified, ulong parent, ulong size, ulong links) {
      var sa = fs.Sa!;
      for (var i = 0; i < SaZnodeLayout.Length; ++i) {
        var expected = SaZnodeLayout[i] switch {
          "ZPL_DACL_ACES" => 0,
          "ZPL_ATIME" or "ZPL_MTIME" or "ZPL_CTIME" or "ZPL_CRTIME" => 16,
          _ => 8,
        };
        if (sa.LengthOf(SaZnodeLayout[i]) != expected)
          throw new NotSupportedException($"ZFS: system attribute {SaZnodeLayout[i]} is registered with an unexpected length.");
      }
      bonus.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(bonus, (uint)ZfsConstants.SaMagic);
      BinaryPrimitives.WriteUInt16LittleEndian(bonus[4..], (ushort)(fs.NewLayout | 1 << 10));   // header of 8 bytes
      BinaryPrimitives.WriteUInt16LittleEndian(bonus[6..], 24);                                 // DACL_ACES length
      var seconds = (ulong)Math.Max(0, (long)(modified - DateTime.UnixEpoch).TotalSeconds);
      var nanos = (ulong)((modified - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerSecond * 100);
      W64(bonus, 8, isDirectory ? 0x41EDUL : 0x81A4UL);            // mode
      W64(bonus, 16, size);
      W64(bonus, 24, this.Txg);                                     // generation
      W64(bonus, 32, 0);                                            // uid
      W64(bonus, 40, 0);                                            // gid
      W64(bonus, 48, parent);
      W64(bonus, 56, ZfsWriter.ZnodeFlags(isDirectory));
      for (var t = 0; t < 4; ++t) {                                 // atime, mtime, ctime, crtime
        W64(bonus, 64 + t * 16, seconds);
        W64(bonus, 72 + t * 16, nanos);
      }
      W64(bonus, 128, links);
      W64(bonus, 136, 3);                                           // DACL_COUNT
      ZfsWriter.WriteTrivialAces(bonus[144..], isDirectory);
    }

    /// <summary>Patches size, links and times of an existing znode's bonus.</summary>
    private void PatchZnode(Fs fs, Znode z, ulong? size, bool touchTimes) {
      var d = z.Dnode;
      var seconds = (ulong)Math.Max(0, (long)(this._now - DateTime.UnixEpoch).TotalSeconds);
      var nanos = (ulong)((this._now - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerSecond * 100);
      if (d.BonusType == OtZnode) {
        var b = d.Bonus;
        if (size is { } s) W64(b, 80, s);
        W64(b, 96, z.Links);
        W64(b, 32, seconds);                                        // ctime
        W64(b, 40, nanos);
        if (touchTimes) {
          W64(b, 16, seconds);                                      // mtime
          W64(b, 24, nanos);
        }
        return;
      }
      var at = fs.Sa!.Locate(d.Bonus);
      (int Offset, int Length) Need(string name) =>
        at.TryGetValue(name, out var where) ? where : throw new NotSupportedException($"ZFS: {name} of object {z.Object} is kept in a spill block; it is not edited.");
      var bonus = d.Bonus;
      if (size is { } sz) W64(bonus, Need("ZPL_SIZE").Offset, sz);
      W64(bonus, Need("ZPL_LINKS").Offset, z.Links);
      var ctime = Need("ZPL_CTIME").Offset;
      W64(bonus, ctime, seconds);
      W64(bonus, ctime + 8, nanos);
      if (touchTimes) {
        var mtime = Need("ZPL_MTIME").Offset;
        W64(bonus, mtime, seconds);
        W64(bonus, mtime + 8, nanos);
      }
    }

    /// <summary>Rewrites changed directories, patches changed znodes and settles user and group accounting.</summary>
    private void Flush(Fs fs) {
      var os = fs.Os;
      foreach (var z in fs.Znodes.Values.OrderBy(z => z.Object)) {
        if (z.Freed || !(z.EntriesChanged || z.LinksChanged)) continue;
        os.Dirty.Add(z.Object);
        ulong? size = null;
        if (z.IsDirectory && z.EntriesChanged) {
          var d = z.Dnode;
          this.FreeTree(os, d, spill: false);
          var entries = z.Entries!.Select(e => (e.Key, e.Value)).ToList();
          var (blockSize, blocks) = ZfsWriter.BuildZap(entries);
          this.WriteContents(os, d, blocks, blockSize);
          size = (ulong)entries.Count + 2;
        }
        if (z.Dnode.IsNew) {
          if (size is { } s) SetNewSize(z, s);
          continue;
        }
        this.PatchZnode(fs, z, size, touchTimes: z.EntriesChanged);
      }

      if (!fs.Accounting) return;
      var users = new Dictionary<string, long>(StringComparer.Ordinal);
      var groups = new Dictionary<string, long>(StringComparer.Ordinal);
      void Charge(Dictionary<string, long> into, ulong id, long delta) {
        var key = id.ToString("x");
        into[key] = into.GetValueOrDefault(key) + delta;
      }
      foreach (var z in fs.Znodes.Values) {
        var d = z.Dnode;
        var synced = z.Freed || os.Dirty.Contains(z.Object);
        if (!synced) continue;
        if (!d.IsNew && (d.OriginalFlags & FlagUserUsedAccounted) != 0) {
          Charge(users, z.Uid, -(Slot + d.OriginalUsed));
          Charge(groups, z.Gid, -(Slot + d.OriginalUsed));
        }
        if (!z.Freed) {
          d.Flags |= FlagUserUsedAccounted;
          Charge(users, z.Uid, Slot + d.Used);
          Charge(groups, z.Gid, Slot + d.Used);
        }
      }
      this.UpdateUsedZap(os, UserUsedAt, users);
      this.UpdateUsedZap(os, GroupUsedAt, groups);
    }

    /// <summary>Size and links of a znode this edit created (laid out by <see cref="WriteSaBonus"/> or <see cref="ZfsWriter.ZnodeBonus"/>).</summary>
    private static void SetNewSize(Znode z, ulong size) {
      var b = z.Dnode.Bonus;
      var znode = z.Dnode.BonusType == OtZnode;
      W64(b, znode ? 80 : 16, size);
      W64(b, znode ? 96 : 128, z.Links);
    }

    private void UpdateUsedZap(Os os, int at, Dictionary<string, long> deltas) {
      if (deltas.Values.All(v => v == 0)) return;
      var oldDnode = ZfsDnode.Read(os.Block.AsSpan(at, Slot)) ?? throw new InvalidDataException("ZFS: accounting object missing.");
      var values = os.Old.Zap(oldDnode).ToDictionary(e => e.Name, e => (long)e.Value, StringComparer.Ordinal);
      foreach (var (key, delta) in deltas)
        values[key] = values.GetValueOrDefault(key) + delta;
      var entries = values.Where(v => v.Value > 0).OrderBy(v => v.Key, StringComparer.Ordinal)
        .Select(v => (v.Key, (ulong)v.Value)).ToList();
      var d = Dnode.From(os.Block.AsSpan(at, Slot).ToArray());
      this.FreeTree(os, d, spill: false);
      var (blockSize, blocks) = ZfsWriter.BuildZap(entries);
      this.WriteContents(os, d, blocks, blockSize);
      os.Special[at] = d;
    }

    // ── MOS ────────────────────────────────────────────────────────────────

    private void PlanMos() {
      var mos = this._mos;
      foreach (var fs in this._filesystems) {
        this.CommitObjset(fs.Os);
        this.Load(mos, fs.HeadObject);
        mos.Dirty.Add(fs.HeadObject);
        this.TouchDirChain(fs.DirObject);
      }
      this.TouchDirChain(this.MosDirectory());

      // space maps: every metaslab the guess names is rewritten whole
      foreach (var (m, count) in this._guess.OrderBy(g => g.Key)) {
        Dnode d;
        ulong obj;
        if (this._s.SpaceMaps[m] != 0) {
          obj = this._s.SpaceMaps[m];
          d = this.Load(mos, obj);
          mos.Dirty.Add(obj);
          this.FreeTree(mos, d, spill: false);
        } else {
          d = Dnode.Create(OtSpaceMap, OtSpaceMapHeader, 24, NewSpaceMapBlock, 14, 3, FlagUsedBytes);
          obj = this.NewObject(mos, d);
          this._newArray ??= (ulong[])this._s.SpaceMaps.Clone();
          this._newArray[m] = obj;
        }
        var blocks = new NewBlock[count];
        var changes = new Dictionary<ulong, NewBlock?>();
        for (var i = 0; i < count; ++i)
          changes[(ulong)i] = blocks[i] = this.AllocBlock(mos, d, d.DataBlockSize, OtSpaceMap, 0);
        this.SetBlocks(mos, d, changes);
        this._maps[m] = new MapPlan { Object = obj, Dnode = d, Blocks = blocks };
      }

      if (this._newArray != null) {
        var d = this.Load(mos, this._s.MetaslabArray);
        mos.Dirty.Add(this._s.MetaslabArray);
        this.FreeTree(mos, d, spill: false);
        var blockSize = d.DataBlockSize;
        var count = (int)((this._s.MetaslabCount * 8L + blockSize - 1) / blockSize);
        this._arrayBlocks = new NewBlock[count];
        var changes = new Dictionary<ulong, NewBlock?>();
        for (var i = 0; i < count; ++i)
          changes[(ulong)i] = this._arrayBlocks[i] = this.AllocBlock(mos, d, blockSize, OtObjectArray, 0);
        this.SetBlocks(mos, d, changes);
      }

      this.CommitObjset(mos);
    }

    /// <summary>
    /// The <c>compression</c> property a filesystem's new data is written with: its own or the
    /// nearest ancestor's (0 = inherit), <c>on</c> meaning lzjb on version 28; off when unset or
    /// when it names an algorithm this pool version cannot hold.
    /// </summary>
    private byte EffectiveCompression(ulong dir) {
      var mos = this._mos.Old;
      for (var guard = 0; dir != 0 && guard < 64; ++guard) {
        var d = mos.RequireDnode(dir, "DSL directory");
        var props = U64(d.Bonus, 80);
        var value = props == 0 ? null : mos.Zap(props, "DSL properties").FirstOrDefault(p => p.Name == "compression");
        if (value != null && value.Value != ZfsConstants.CompressInherit)
          return value.Value switch {
            ZfsConstants.CompressOn or ZfsConstants.CompressLzjb => ZfsConstants.CompressLzjb,
            >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9 => (byte)value.Value,
            ZfsConstants.CompressZle => ZfsConstants.CompressZle,
            _ => ZfsConstants.CompressOff,
          };
        dir = U64(d.Bonus, 16);
      }
      return ZfsConstants.CompressOff;
    }

    private ulong MosDirectory() {
      var root = this._mos.Old.Zap(1, "MOS object directory").First(e => e.Name == "root_dataset").Value;
      var rootDir = this._mos.Old.RequireDnode(root, "root DSL directory");
      var children = U64(rootDir.Bonus, 32);
      var mosDir = children == 0 ? null : this._mos.Old.Zap(children, "root child map").FirstOrDefault(e => e.Name == "$MOS");
      return mosDir?.Value ?? throw new NotSupportedException("ZFS: the pool has no $MOS directory.");
    }

    private void TouchDirChain(ulong dir) {
      for (var guard = 0; dir != 0; ++guard) {
        if (guard > 64) throw new InvalidDataException("ZFS: DSL directory chain loops.");
        var d = this.Load(this._mos, dir);
        this._mos.Dirty.Add(dir);
        dir = U64(d.Bonus, 16);
      }
    }

    /// <summary>
    /// Checks the guess of space-map sizes against what the plan allocated and freed; false (with
    /// the guess grown) when another attempt is needed.
    /// </summary>
    public bool Settle(Dictionary<int, int> guess) {
      var settled = true;
      foreach (var m in this._touched.Concat(guess.Keys).Distinct().Order().ToList()) {
        var blockSize = this._maps.TryGetValue(m, out var plan) ? plan.Dnode.DataBlockSize
          : this._s.SpaceMaps[m] != 0 ? this._s.SpaceMapBlockSize[m] : NewSpaceMapBlock;
        var words = this.Words(m).Count;
        var need = Math.Max(1, (int)((words * 8L + blockSize - 1) / blockSize));
        if (guess.TryGetValue(m, out var have) && have >= need) continue;
        guess[m] = Math.Max(need, have);
        settled = false;
      }
      return settled;
    }

    private List<ulong> Words(int m) {
      var start = (long)m << this._s.MetaslabShift;
      var words = new List<ulong> { 2UL << 62 | 1UL << 50 | this.Txg };      // debug: ALLOC, pass 1, txg
      var maxRun = (long)SmRunMax << this._s.Ashift;
      foreach (var (from, to) in this._allocated[m].Ranges)
        for (var at = from; at < to;) {
          var run = Math.Min(to - at, maxRun);
          words.Add((ulong)((at - start) >> this._s.Ashift) << 16 | (ulong)((run >> this._s.Ashift) - 1));
          at += run;
        }
      return words;
    }

    public Commit Seal() {
      var mos = this._mos;
      foreach (var fs in this._filesystems) {
        var root = this.SealObjset(fs.Os);
        var head = this.Load(mos, fs.HeadObject).Bonus;
        Add64(head, 72, fs.Os.Used);
        Add64(head, 80, fs.Os.Compressed);
        Add64(head, 88, fs.Os.Uncompressed);
        Add64(head, 96, fs.Os.Used);
        root.CopyTo(head[128..]);
        this.Transfer(fs.DirObject, fs.Os.Used, fs.Os.Compressed, fs.Os.Uncompressed, fs.Os.Used, UsedRefReservation, UsedHead);
      }

      foreach (var (m, plan) in this._maps) {
        var words = this.Words(m);
        var blockSize = plan.Dnode.DataBlockSize;
        var bytes = new byte[plan.Blocks.Length * blockSize];
        if (words.Count * 8 > bytes.Length) throw new InvalidOperationException("ZFS: a space map outgrew its planned blocks.");
        for (var i = 0; i < words.Count; ++i) W64(bytes, i * 8, words[i]);
        for (var i = 0; i < plan.Blocks.Length; ++i)
          plan.Blocks[i].Data = bytes.AsSpan(i * blockSize, blockSize).ToArray();
        var bonus = plan.Dnode.Bonus;
        W64(bonus, 0, plan.Object);
        W64(bonus, 8, (ulong)(words.Count * 8));
        W64(bonus, 16, (ulong)this._allocated[m].Total);
      }
      if (this._newArray != null) {
        var blockSize = this._arrayBlocks![0].Size;
        var bytes = new byte[this._arrayBlocks.Length * blockSize];
        for (var m = 0; m < this._newArray.Length; ++m) W64(bytes, m * 8, this._newArray[m]);
        for (var i = 0; i < this._arrayBlocks.Length; ++i)
          this._arrayBlocks[i].Data = bytes.AsSpan(i * blockSize, blockSize).ToArray();
      }

      // the MOS's own blocks are charged to $MOS; its accounting is final once it is planned
      this.Transfer(this.MosDirectory(), mos.Used, mos.Compressed, mos.Uncompressed, mos.Used, UsedChildReserve, UsedHead);
      var mosRoot = this.SealObjset(mos);

      var commit = new Commit();
      foreach (var block in this._blocks) {
        if (block.Pointer == null) throw new InvalidOperationException("ZFS: a block was planned but never placed in a tree.");
        commit.Blocks.Add((ZfsConstants.DataStart + block.Offset, block.Data!));
      }
      var aligned = this._s.DeviceLength / ZfsConstants.LabelSize * ZfsConstants.LabelSize;
      var slots = ZfsConstants.UberblockRingSize / this._s.SlotSize;
      foreach (var labelAt in PoolState.LabelOffsets(aligned)) {
        var ub = this._s.Uberblock.ToArray();
        W64(ub, 0x10, this.Txg);
        W64(ub, 0x20, (ulong)new DateTimeOffset(this._now).ToUnixTimeSeconds());
        mosRoot.CopyTo(ub, 0x28);
        var at = labelAt + ZfsConstants.UberblockRingOffset + (long)(this.Txg % (ulong)slots) * this._s.SlotSize;
        ZfsWriter.Seal(ub, at);
        commit.Uberblocks.Add((at, ub));
      }
      return commit;
    }

    /// <summary>
    /// Charges space to a DSL directory and, through the child-usage entries, to its parents —
    /// reservations included (<c>docs/ZFS-ON-DISK.md</c>, "Editing a pool").
    /// </summary>
    private void Transfer(ulong dir, long used, long compressed, long uncompressed, long toNew, int oldType, int newType) {
      if (dir == 0 || (used == 0 && compressed == 0 && uncompressed == 0 && toNew == 0)) return;
      var b = this.Load(this._mos, dir).Bonus;
      var current = (long)U64(b, 40);
      var reserved = (long)U64(b, 72);
      var accounted = Math.Max(current + used, reserved) - Math.Max(current, reserved);
      Add64(b, 40, used);
      Add64(b, 48, compressed);
      Add64(b, 56, uncompressed);
      if ((U64(b, 96) & 1) != 0) {
        Add64(b, 104 + oldType * 8, -(toNew - used));
        Add64(b, 104 + newType * 8, toNew);
      }
      this.Transfer(U64(b, 16), accounted, compressed, uncompressed, used, UsedChildReserve, UsedChild);
    }
  }
}
