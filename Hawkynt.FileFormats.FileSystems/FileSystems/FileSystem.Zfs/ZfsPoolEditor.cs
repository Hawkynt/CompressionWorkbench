using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Adds and removes files in an existing pool of version 28 or 5000 (devices, mirrors, RAID-Z, striped), the way a
/// transaction group of OpenZFS would (<c>docs/ZFS-ON-DISK.md</c>, "Editing a pool"): every
/// changed block is written anew elsewhere (copy-on-write) up to a new MOS, the blocks it
/// replaces go back to the space maps, the dataset, directory and user accounting follow,
/// and one new uberblock in every label commits it all.
/// </summary>
/// <remarks>
/// The edit is planned completely in memory before a byte is written, so any refusal leaves the
/// image untouched; new blocks are written first and the uberblocks last. Space freed by the edit
/// is not reused by the same edit, so the previous uberblocks still name intact trees. A block a
/// snapshot still holds goes to the deadlist, a gang block frees its whole tree and a deduplicated
/// one gives up a reference in its dedup table. Refused: active features the editor does not keep
/// consistent (block cloning, device removal, …), degraded pools, pool checkpoints, an unreplayed
/// intent log, normalised or case-insensitive filesystems, and datasets with a refreservation.
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
  private const byte OtZapMetadata = 0xC4;                        // DMU_OTN_ZAP_METADATA
  private const byte FlagUsedBytes = 1, FlagUserUsedAccounted = 2, FlagSpill = 4, FlagUserObjUsedAccounted = 8;
  private const ulong ZfsProjectFlag = 0x0000800000000000UL;   // ZFS_PROJID: the znode carries a project id
  private const int UsedHead = 0, UsedSnap = 1, UsedChild = 2, UsedChildReserve = 3, UsedRefReservation = 4;
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
    Edit([device], additions, removals);
  }

  /// <summary>
  /// Applies the edit to a pool spread over <paramref name="devices"/>: a single device, every
  /// device of a mirror, or every device of a RAID-Z (in any order; none may be missing).
  /// </summary>
  public static void Edit(IReadOnlyList<Stream> devices, IReadOnlyList<Addition> additions, IReadOnlyList<string> removals) {
    ArgumentNullException.ThrowIfNull(devices);
    ArgumentNullException.ThrowIfNull(additions);
    ArgumentNullException.ThrowIfNull(removals);
    if (devices.Count == 0) throw new ArgumentException("ZFS: no device given.", nameof(devices));
    foreach (var device in devices) {
      ArgumentNullException.ThrowIfNull(device);
      if (!device.CanSeek || !device.CanWrite || !device.CanRead)
        throw new NotSupportedException("ZFS: editing needs readable, writable, seekable device images.");
    }
    if (additions.Count == 0 && removals.Count == 0) return;

    // the last addition of a path wins
    var adds = additions.GroupBy(a => Normalize(a.Path), StringComparer.Ordinal)
      .Select(g => g.Last() with { Path = g.Key }).Where(a => a.Path.Length > 0).ToList();
    var removes = removals.Select(Normalize).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    var now = DateTime.UtcNow;

    Commit commit;
    IReadOnlyList<Stream> children;
    foreach (var device in devices) device.Position = 0;
    using (var pool = new ZfsPool(devices, leaveOpen: true)) {
      var state = new PoolState(pool, devices);
      children = state.Devices;
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
    commit.WriteTo(children);
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

  /// <summary>One top-level vdev: its devices, layout, metaslabs and space maps.</summary>
  private sealed class TopVdev {
    public int Id;
    public required Stream[] Children;
    /// <summary>RAID-Z parity; 0 for a plain device or a mirror.</summary>
    public int Parity;
    public bool IsMirror;
    /// <summary>Charge of one allocated 512-byte unit (512 unless RAID-Z).</summary>
    public long DeflateRatio = 512;
    public int Ashift, MetaslabShift, MetaslabCount;
    /// <summary>Index of this vdev's first metaslab among all the pool's metaslabs.</summary>
    public int Base;
    public ulong MetaslabArray;
    public ulong[] SpaceMaps = [];
    public int[] SpaceMapBlockSize = [];
    /// <summary>log_spacemap: per metaslab the txg from which on the logs hold its changes (0 = none), and the object holding them.</summary>
    public ulong[] Unflushed = [];
    public ulong UnflushedObject;
    public required XdrNvList Tree;

    public int SlotSize => 1 << Math.Clamp(this.Ashift, ZfsConstants.MinUberblockShift, ZfsConstants.MaxUberblockShift);

    public long AllocatedSize(int size) => this.Parity > 0
      ? ZfsRaidzVdev.AllocatedSize(size, this.Children.Length, this.Parity, this.Ashift)
      : Align(size, 1L << this.Ashift);

    public long Charged(long allocated) => (allocated >> ZfsConstants.SectorShift) * this.DeflateRatio;
  }

  private sealed class PoolState {
    public readonly ZfsPool Pool;
    public readonly ulong NewTxg;
    public readonly List<TopVdev> Tops = [];
    /// <summary>Every device of every top-level vdev, in vdev order: the order commits address them in.</summary>
    public readonly Stream[] Devices;
    /// <summary>All metaslabs of the pool, numbered across the vdevs (vdev 0's first).</summary>
    public readonly ZfsRangeSet[] Allocated;
    public readonly int[] MetaslabVdev;
    public readonly int MetaslabTotal;
    public readonly byte[] Uberblock;
    public readonly byte[] MosRoot;
    public readonly List<(string Path, ulong Dir, ulong Head)> Datasets = [];

    public (TopVdev Top, int Index) Metaslab(int global) {
      var top = this.Tops[this.MetaslabVdev[global]];
      return (top, global - top.Base);
    }

    /// <summary>The smallest sector of the pool: what compressed blocks are rounded up to.</summary>
    public int MinAshift => this.Tops.Min(t => t.Ashift);

    // ── feature flags (version 5000) ─────────────────────────────
    /// <summary>Every enabled feature by guid: its reference count and whether it sits in the write list.</summary>
    public readonly Dictionary<string, (ulong Count, bool ForWrite)> Features = new(StringComparer.Ordinal);
    public ulong FeaturesForRead, FeaturesForWrite;
    public bool IsEnabled(string guid) => this.Features.ContainsKey(guid);
    public bool IsActive(string guid) => this.Features.TryGetValue(guid, out var f) && f.Count > 0;
    /// <summary>log_spacemap: the ZAP of log space maps (hex txg → object).</summary>
    public ulong LogSpacemapZap;
    public readonly SortedDictionary<ulong, ulong> LogSpacemaps = [];

    /// <summary>Features whose activity changes how blocks may be freed or space maps written; refused while active.</summary>
    private static readonly HashSet<string> RefusedWhenActive = new(StringComparer.Ordinal) {
      "com.fudosecurity:block_cloning", "com.delphix:device_removal", "com.delphix:obsolete_counts",
      "com.delphix:zpool_checkpoint", "org.openzfs:draid", "com.delphix:async_destroy",
      "org.zfsonlinux:allocation_classes", "com.delphix:livelist", "org.openzfs:raidz_expansion",
      "com.delphix:redacted_datasets",
    };

    /// <summary>Features that may be active: the editor keeps the pool consistent with each of them.</summary>
    private static readonly HashSet<string> Understood = new(StringComparer.Ordinal) {
      "com.delphix:enabled_txg", "com.delphix:hole_birth", "com.delphix:embedded_data", "org.illumos:lz4_compress",
      "com.delphix:spacemap_histogram", "com.delphix:spacemap_v2", "com.delphix:log_spacemap", "com.delphix:empty_bpobj",
      "com.delphix:extensible_dataset", "org.freebsd:zstd_compress", "org.zfsonlinux:large_dnode", "org.open-zfs:large_blocks",
      "org.illumos:sha512", "org.illumos:skein", "org.illumos:edonr", "org.openzfs:blake3",
      "org.zfsonlinux:userobj_accounting", "org.zfsonlinux:project_quota", "com.klarasystems:vdev_zaps_v2",
      "com.delphix:head_errlog", "com.delphix:bookmarks", "com.datto:bookmark_v2", "com.delphix:bookmark_written",
      "com.delphix:redaction_bookmarks", "org.openzfs:zilsaxattr", "com.joyent:filesystem_limits",
      "com.joyent:multi_vdev_crash_dump", "org.openzfs:device_rebuild", "com.datto:resilver_defer", "com.datto:encryption",
    };

    private void LoadFeatures() {
      var directory = this.Pool.Mos.Zap(1, "MOS object directory").ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
      this.FeaturesForRead = directory.GetValueOrDefault("features_for_read");
      this.FeaturesForWrite = directory.GetValueOrDefault("features_for_write");
      if (this.FeaturesForRead == 0 || this.FeaturesForWrite == 0)
        throw new InvalidDataException("ZFS: a version 5000 pool without its feature lists.");
      foreach (var (obj, forWrite) in new[] { (this.FeaturesForRead, false), (this.FeaturesForWrite, true) })
        foreach (var e in this.Pool.Mos.Zap(obj, "feature list"))
          this.Features[e.Name] = (e.Value, forWrite);
      foreach (var (guid, (count, _)) in this.Features) {
        if (count == 0) continue;
        if (RefusedWhenActive.Contains(guid))
          throw new NotSupportedException($"ZFS: feature {guid} is active; such pools are not edited.");
        if (!Understood.Contains(guid))
          throw new NotSupportedException($"ZFS: feature {guid} is active and not understood; the pool is not edited.");
      }
      if (this.IsActive("com.delphix:log_spacemap")) {
        this.LogSpacemapZap = directory.GetValueOrDefault("com.delphix:log_spacemap_zap");
        if (this.LogSpacemapZap == 0) throw new InvalidDataException("ZFS: log_spacemap is active without its ZAP.");
        foreach (var e in this.Pool.Mos.Zap(this.LogSpacemapZap, "log space maps"))
          this.LogSpacemaps[Convert.ToUInt64(e.Name, 16)] = e.Value;
      }
    }

    /// <summary>The per-metaslab txgs from which on the log space maps hold a metaslab's changes (0 = none).</summary>
    private void LoadUnflushed(TopVdev top) {
      top.Unflushed = new ulong[top.MetaslabCount];
      var topZap = top.Tree.UInt64("com.delphix:vdev_zap_top") ?? 0;
      if (topZap == 0) return;
      var entry = this.Pool.Mos.Zap(topZap, "top vdev ZAP").FirstOrDefault(e => e.Name == "com.delphix:ms_unflushed_phys_txgs");
      if (entry == null) return;
      top.UnflushedObject = entry.Value;
      var dnode = this.Pool.Mos.RequireDnode(top.UnflushedObject, "unflushed txgs");
      for (var m = 0; m < top.MetaslabCount; ++m) {
        var at = (long)m * 8;
        var blockId = (ulong)(at / dnode.DataBlockSize);
        if (blockId > dnode.MaxBlockId) break;
        top.Unflushed[m] = U64(this.Pool.Mos.ReadBlock(dnode, blockId), (int)(at % dnode.DataBlockSize));
      }
    }

    /// <summary>The pool's vdev tree as the MOS <c>config</c> object (a packed nvlist) describes it: the top-level vdevs.</summary>
    private XdrNvList[] MosConfigTops() {
      var mos = this.Pool.Mos;
      var configObject = mos.Zap(1, "MOS object directory").FirstOrDefault(e => e.Name == "config")?.Value
        ?? throw new InvalidDataException("ZFS: the MOS has no config.");
      var dnode = mos.RequireDnode(configObject, "MOS config");
      var size = (int)U64(dnode.Bonus, 0);
      var bytes = new byte[size];
      for (var at = 0; at < size; at += dnode.DataBlockSize) {
        var block = mos.ReadBlock(dnode, (ulong)(at / dnode.DataBlockSize));
        block.AsSpan(0, Math.Min(block.Length, size - at)).CopyTo(bytes.AsSpan(at));
      }
      return XdrNvList.Decode(bytes).List("vdev_tree")?.Lists("children") ?? [];
    }

    /// <summary>Applies the log space maps to the metaslabs whose changes they still hold, oldest first.</summary>
    private void ReplayLogs() {
      foreach (var (txg, obj) in this.LogSpacemaps)
        this.ReplayMap(obj, 0, long.MaxValue, ZfsConstants.SectorShift, (vdev, offset, run, alloc) => {
          if (vdev >= this.Tops.Count) throw new InvalidDataException("ZFS: a log space map entry names a vdev the pool does not have.");
          var top = this.Tops[vdev];
          var m = (int)(offset >> top.MetaslabShift);
          if (m < 0 || m >= top.MetaslabCount || (offset + run - 1) >> top.MetaslabShift != m)
            throw new InvalidDataException("ZFS: a log space map entry lies outside the metaslabs.");
          if (txg < top.Unflushed[m]) return;
          var set = this.Allocated[top.Base + m];
          if (alloc) set.Add(offset, offset + run);
          else set.Remove(offset, offset + run);
        });
    }

    /// <summary>
    /// Walks a space map's entries: one-word entries, two-word entries (spacemap_v2, naming a vdev),
    /// debug entries skipped; offsets and runs in units of <c>2^shift</c> from <paramref name="start"/>.
    /// Returns the bytes the map's header says are allocated.
    /// </summary>
    private long ReplayMap(ulong obj, long start, long limit, int shift, Action<int, long, long, bool> apply) {
      var mos = this.Pool.Mos;
      var map = mos.RequireDnode(obj, "space map");
      if (map.Bonus.Length < 24) throw new InvalidDataException("ZFS: space map header truncated.");
      var length = U64(map.Bonus, 8);
      var blockSize = (ulong)map.DataBlockSize;
      byte[]? block = null;
      var blockId = ulong.MaxValue;
      ulong Word(ulong at) {
        var id = at / blockSize;
        if (id != blockId) {
          block = mos.ReadBlock(map, id);
          blockId = id;
        }
        return U64(block!, (int)(at % blockSize));
      }
      for (ulong at = 0; at + 8 <= length; at += 8) {
        var word = Word(at);
        long offset, run;
        bool alloc;
        var vdev = 0;
        if (word >> 62 == 3) {
          if (at + 16 > length) throw new InvalidDataException("ZFS: a two-word space map entry is cut off.");
          var second = Word(at + 8);
          at += 8;
          vdev = (int)(word & 0xFFFFFF);                               // "none" in a metaslab's own map encodes as 0
          run = (long)(((word >> 24) & ((1UL << 36) - 1)) + 1) << shift;
          offset = start + ((long)(second & ((1UL << 63) - 1)) << shift);
          alloc = (second >> 63) == 0;
        } else if (word >> 63 != 0) {
          continue;                                                    // debug entry
        } else {
          offset = start + ((long)((word >> 16) & ((1UL << 47) - 1)) << shift);
          run = (long)((word & 0x7FFF) + 1) << shift;
          alloc = ((word >> 15) & 1) == 0;
        }
        if (offset < start || offset + run > limit)
          throw new InvalidDataException("ZFS: a space map entry lies outside its range.");
        apply(vdev, offset, run, alloc);
      }
      return (long)U64(map.Bonus, 16);
    }

    public PoolState(ZfsPool pool, IReadOnlyList<Stream> devices) {
      this.Pool = pool;
      if (pool.Version is not (28 or 5000))
        throw new NotSupportedException($"ZFS: pool versions 28 and 5000 are edited; this pool is version {pool.Version}.");
      if (pool.NewestUberblockTxg != pool.Txg)
        throw new NotSupportedException("ZFS: the newest uberblock does not lead to a readable pool; it is not edited.");
      var config = pool.Config;
      if (pool.Version < 5000 && (config.List("features_for_read")?.Pairs.Count ?? 0) > 0)
        throw new NotSupportedException("ZFS: a version 28 pool that names features is not edited.");
      this.NewTxg = pool.Txg + 1;

      // the top-level vdevs, from the MOS config (the labels describe only their own)
      var trees = this.MosConfigTops();
      if (trees.Length == 0 || trees.Length != pool.VdevChildren)
        throw new InvalidDataException("ZFS: the MOS config does not list the pool's top-level vdevs.");
      var byGuid = DevicesByGuid(devices);
      var used = 0;
      foreach (var tree in trees.OrderBy(t => t.UInt64("id") ?? 0)) {
        var top = BuildTop(tree, byGuid, this.Tops.Count);
        used += top.Children.Length;
        top.Base = this.Tops.Sum(t => t.MetaslabCount);
        this.Tops.Add(top);
      }
      if (used != devices.Count)
        throw new ArgumentException("ZFS: a device was given that the pool does not have.");
      this.Devices = this.Tops.SelectMany(t => t.Children).ToArray();

      // the active uberblock, as bytes, and the MOS it names
      var first = this.Tops[0];
      this.Uberblock = FindUberblock(first.Children[0], pool.Txg, first.SlotSize)
        ?? throw new InvalidDataException("ZFS: the active uberblock was not found again.");
      if (this.Uberblock.Length >= 0xD0 && U64(this.Uberblock, 0xC8) != 0)
        throw new NotSupportedException("ZFS: the pool has a checkpoint; it is not edited.");
      this.MosRoot = this.Uberblock.AsSpan(0x28, Bp).ToArray();
      if (pool.Version >= 5000) this.LoadFeatures();

      // every metaslab array and space map, replayed, then the log space maps on top
      this.MetaslabTotal = this.Tops.Sum(t => t.MetaslabCount);
      this.Allocated = new ZfsRangeSet[this.MetaslabTotal];
      this.MetaslabVdev = new int[this.MetaslabTotal];
      var mos = pool.Mos;
      foreach (var top in this.Tops) {
        this.LoadUnflushed(top);
        var array = mos.RequireDnode(top.MetaslabArray, "metaslab array");
        top.SpaceMaps = new ulong[top.MetaslabCount];
        top.SpaceMapBlockSize = new int[top.MetaslabCount];
        for (var m = 0; m < top.MetaslabCount; ++m) {
          var at = (long)m * 8;
          var block = mos.ReadBlock(array, (ulong)(at / array.DataBlockSize));
          top.SpaceMaps[m] = U64(block, (int)(at % array.DataBlockSize));
          this.Allocated[top.Base + m] = new ZfsRangeSet();
          this.MetaslabVdev[top.Base + m] = top.Id;
          if (top.SpaceMaps[m] != 0) this.Replay(top, m);
        }
      }
      this.ReplayLogs();

      // the filesystems, by the path the reader lists them under
      var directory = mos.Zap(1, "MOS object directory");
      var root = directory.FirstOrDefault(e => e.Name == "root_dataset")
        ?? throw new InvalidDataException("ZFS: the object directory has no root_dataset.");
      this.AddDatasets(root.Value, "", 0);
    }

    private static TopVdev BuildTop(XdrNvList tree, Dictionary<ulong, Stream> byGuid, int id) {
      if ((int)(tree.UInt64("id") ?? 0) != id)
        throw new InvalidDataException("ZFS: the top-level vdev ids are not 0, 1, 2, ….");
      var type = tree.String("type");
      Stream[] children;
      if (type is "disk" or "file") {
        children = [Device(byGuid, tree, $"device {id}")];
      } else if (type is "mirror" or "raidz") {
        var leaves = tree.Lists("children");
        if (leaves.Any(c => c.String("type") is not ("disk" or "file")))
          throw new NotSupportedException("ZFS: a mirror or RAID-Z with nested or replacing children is not edited.");
        children = leaves.Select((leaf, i) => Device(byGuid, leaf, $"device {i} of vdev {id} ({type})")).ToArray();
      } else {
        throw new NotSupportedException($"ZFS: a '{type}' top-level vdev is not edited; plain devices, mirrors and RAID-Z are.");
      }
      var top = new TopVdev {
        Id = id, Children = children, Tree = tree, IsMirror = type == "mirror",
        Ashift = (int)(tree.UInt64("ashift") ?? throw new InvalidDataException("ZFS: a vdev has no ashift.")),
        MetaslabShift = (int)(tree.UInt64("metaslab_shift") ?? throw new InvalidDataException("ZFS: a vdev has no metaslab_shift.")),
        MetaslabArray = tree.UInt64("metaslab_array") ?? throw new InvalidDataException("ZFS: a vdev has no metaslab array."),
      };
      if (type == "raidz") {
        top.Parity = (int)(tree.UInt64("nparity") ?? 1);
        top.DeflateRatio = ZfsRaidzVdev.DeflateRatio(children.Length, top.Parity, top.Ashift);
      }
      var asize = (long)(tree.UInt64("asize") ?? throw new InvalidDataException("ZFS: a vdev has no asize."));
      if (top.Ashift is < 9 or > 16 || top.MetaslabShift is < 16 or > 40)
        throw new InvalidDataException("ZFS: a vdev's geometry is implausible.");
      top.MetaslabCount = (int)(asize >> top.MetaslabShift);
      var perDevice = ((long)top.MetaslabCount << top.MetaslabShift) / (top.Parity > 0 ? children.Length : 1);
      if (ZfsConstants.DataStart + perDevice > children.Min(c => c.Length))
        throw new InvalidDataException("ZFS: the metaslabs run past the devices.");
      return top;
    }

    private static Stream Device(Dictionary<ulong, Stream> byGuid, XdrNvList leaf, string what) =>
      byGuid.TryGetValue(leaf.UInt64("guid") ?? 0, out var s) ? s
        : throw new NotSupportedException($"ZFS: {what} is missing; a degraded or partial pool is not edited.");

    /// <summary>The supplied devices by the <c>guid</c> in each device's own label.</summary>
    private static Dictionary<ulong, Stream> DevicesByGuid(IReadOnlyList<Stream> devices) {
      var byGuid = new Dictionary<ulong, Stream>();
      foreach (var device in devices) {
        var label = new byte[ZfsConstants.NvListSize];
        device.Position = ZfsConstants.NvListOffset;
        device.ReadExactly(label);
        var guid = XdrNvList.Decode(label).UInt64("guid") ?? throw new InvalidDataException("ZFS: a device label has no guid.");
        if (!byGuid.TryAdd(guid, device)) throw new ArgumentException("ZFS: the same device was given twice.");
      }
      return byGuid;
    }

    private void Replay(TopVdev top, int m) {
      var map = this.Pool.Mos.RequireDnode(top.SpaceMaps[m], "space map");
      if (map.Bonus.Length is not (24 or 320))
        throw new InvalidDataException("ZFS: a space map header of unexpected size.");
      top.SpaceMapBlockSize[m] = map.DataBlockSize;
      var start = (long)m << top.MetaslabShift;
      var set = this.Allocated[top.Base + m];
      var expected = this.ReplayMap(top.SpaceMaps[m], start, start + (1L << top.MetaslabShift), top.Ashift, (_, offset, run, alloc) => {
        if (alloc) set.Add(offset, offset + run);
        else set.Remove(offset, offset + run);
      });
      if (set.Total != expected)
        throw new InvalidDataException($"ZFS: metaslab {m} of vdev {top.Id} replays to {set.Total} allocated bytes, its space map says {expected}.");
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
    public int Vdev;
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
    /// <summary>Blocks the previous snapshot still holds that the edit no longer references: they go to the deadlist.</summary>
    public readonly List<byte[]> Held = [];
    public long HeldUsed, HeldCompressed, HeldUncompressed;
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
    public bool ObjectAccounting;
    public bool ProjectAccounting;
    public int ZstdLevel = 3;
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
    public ulong Uid, Gid, Project;
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

  /// <summary>The bytes to write, by device: blocks first, then the uberblock in every label.</summary>
  private sealed class Commit {
    public readonly List<(int Device, long At, byte[] Data)> Blocks = [];
    public readonly List<(int Device, long At, byte[] Data)> Uberblocks = [];

    public void WriteTo(IReadOnlyList<Stream> devices) {
      foreach (var (device, at, data) in this.Blocks) {
        devices[device].Position = at;
        devices[device].Write(data);
      }
      foreach (var device in devices) device.Flush();
      foreach (var (device, at, data) in this.Uberblocks) {
        devices[device].Position = at;
        devices[device].Write(data);
      }
      foreach (var device in devices) device.Flush();
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
    private readonly Dictionary<int, ulong[]> _newArrays = [];
    private readonly Dictionary<int, NewBlock[]> _arrayBlocks = [];

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

    private int _rotor;

    /// <summary>
    /// Space for a block of <paramref name="size"/> physical bytes: the top-level vdevs take turns
    /// (so a striped pool's blocks spread over them), within one the first metaslab with room, those
    /// that already have a space map first.
    /// </summary>
    private (int Vdev, long Offset, long Allocated) Allocate(int size) {
      var tops = this._s.Tops;
      for (var k = 0; k < tops.Count; ++k) {
        var top = tops[(this._rotor + k) % tops.Count];
        var allocated = top.AllocatedSize(size);
        foreach (var m in Enumerable.Range(0, top.MetaslabCount).OrderBy(m => top.SpaceMaps[m] == 0 ? 1 : 0).ThenBy(m => m)) {
          var g = top.Base + m;
          var start = (long)m << top.MetaslabShift;
          var gap = this._busy[g].FindGap(start, start + (1L << top.MetaslabShift), allocated, 1L << top.Ashift);
          if (gap is not { } at) continue;
          this._busy[g].Add(at, at + allocated);
          this._allocated[g].Add(at, at + allocated);
          this._touched.Add(g);
          this._rotor = (top.Id + 1) % tops.Count;
          return (top.Id, at, allocated);
        }
      }
      throw new IOException("ZFS: the pool has no free space left for this edit.");
    }

    private void Free(int vdev, long offset, long size) {
      if (vdev >= this._s.Tops.Count) throw new InvalidDataException("ZFS: a block names a vdev the pool does not have.");
      var top = this._s.Tops[vdev];
      var m = (int)(offset >> top.MetaslabShift);
      if (m < 0 || m >= top.MetaslabCount || (offset + size - 1) >> top.MetaslabShift != m)
        throw new InvalidDataException($"ZFS: a block at {vdev}:{offset:X} lies outside the metaslabs.");
      this._allocated[top.Base + m].Remove(offset, offset + size);
      this._touched.Add(top.Base + m);
    }

    private NewBlock AllocBlock(Os os, Dnode? d, int size, byte type, int level, int logicalSize = 0, byte compression = ZfsConstants.CompressOff) {
      var (vdev, offset, allocated) = this.Allocate(size);
      var charged = this._s.Tops[vdev].Charged(allocated);
      var logical = logicalSize == 0 ? size : logicalSize;
      var block = new NewBlock {
        Vdev = vdev, Offset = offset, Size = size, LogicalSize = logical, Compression = compression,
        Allocated = allocated, Type = type, Level = level,
      };
      this._blocks.Add(block);
      var metadata = level > 0 || type != OtPlainFile;
      os.Used += charged;
      os.Compressed += size;
      os.Uncompressed += metadata ? size : logical;
      if (d != null) d.Used += charged;
      return block;
    }

    /// <summary>What a block is charged: allocated (deflated) bytes of all its DVAs, physical bytes, and logical bytes for data.</summary>
    private (long Used, long Compressed, long Uncompressed) ChargeOf(ZfsBlockPointer bp) {
      if (bp.IsEmbedded) return (0, 0, IsMetadata(bp) ? 0 : bp.LogicalSize);
      long used = 0;
      foreach (var dva in bp.Dvas)
        if (!dva.IsEmpty) used += this._s.Tops[(int)dva.Vdev].Charged((long)dva.AllocatedSectors << ZfsConstants.SectorShift);
      return (used, bp.PhysicalSize, IsMetadata(bp) ? bp.PhysicalSize : bp.LogicalSize);
    }

    /// <summary>
    /// A block the edit no longer references: freed, or — when born at or before the previous
    /// snapshot, which still holds it — kept and put on the dataset's deadlist.
    /// </summary>
    private void FreePointer(Os os, Dnode? d, byte[] raw) {
      if (IsHole(raw)) return;
      var bp = ZfsBlockPointer.Read(raw);
      var (used, compressed, uncompressed) = this.ChargeOf(bp);
      if (d != null) d.Used -= used;
      if (bp.LogicalBirth <= os.PrevSnapTxg) {
        os.Held.Add(raw);
        os.HeldUsed += used;
        os.HeldCompressed += compressed;
        os.HeldUncompressed += uncompressed;
        return;
      }
      os.Used -= used;
      os.Compressed -= compressed;
      os.Uncompressed -= uncompressed;
      if (bp.IsEmbedded) return;                                    // nothing allocated
      if (((U64(raw, 0x30) >> 62) & 1) != 0) {
        // the dedup table owns the space: one reference fewer, freed when none is left
        this._dedupFrees.Add(raw);
        return;
      }
      this.FreeSpace(bp, 0);
    }

    /// <summary>
    /// Returns a block's space to the metaslabs: every DVA, and for a gang DVA its header and,
    /// through it, every child block (themselves possibly gang blocks).
    /// </summary>
    private void FreeSpace(ZfsBlockPointer bp, int depth) {
      if (depth > 16) throw new InvalidDataException("ZFS: gang blocks nest too deeply.");
      byte[]? header = null;
      foreach (var dva in bp.Dvas) {
        if (dva.IsEmpty) continue;
        var top = this._s.Tops[(int)dva.Vdev];
        if (!dva.Gang) {
          this.Free((int)dva.Vdev, dva.Offset, (long)dva.AllocatedSectors << ZfsConstants.SectorShift);
          continue;
        }
        // a gang DVA's size counts header and children; the metaslab gave each copy of the header alone
        this.Free((int)dva.Vdev, dva.Offset, top.AllocatedSize(ZfsConstants.GangHeaderSize));
        header ??= this._s.Pool.TopLevelVdevs[dva.Vdev].Read(dva.Offset, ZfsConstants.GangHeaderSize);
      }
      if (header == null) return;
      // every copy of the header names the same children, which carry their own copies
      for (var i = 0; i < 3; ++i) {
        var child = ZfsBlockPointer.Read(header.AsSpan(i * Bp));
        if (!child.IsHole) this.FreeSpace(child, depth + 1);
      }
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
      if (IsHole(raw)) return;
      var bp = ZfsBlockPointer.Read(raw);
      if (!bp.IsEmbedded && bp.Level > 0) {
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
    private void WriteContents(Os os, Dnode d, IReadOnlyList<byte[]?> contents, int blockSize, byte compression = ZfsConstants.CompressOff, int zstdLevel = 3) {
      d.DataBlockSize = blockSize;
      var changes = new Dictionary<ulong, NewBlock?>();
      for (var i = 0; i < contents.Count; ++i) {
        if (contents[i] is not { } content) continue;
        var logical = new byte[blockSize];
        content.AsSpan(0, Math.Min(content.Length, blockSize)).CopyTo(logical);
        var (algorithm, physical, _) = ZfsCompression.CompressBlock(compression, logical, this._s.MinAshift, zstdLevel);
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
      W64(p, 0x00, (ulong)b.Vdev << 32 | (ulong)(b.Allocated >> ZfsConstants.SectorShift));   // DVA 0: vdev, asize
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
      // userobj_accounting counts objects ("obj-<id>"), project_quota charges by project as well
      fs.ObjectAccounting = fs.Accounting && this._s.IsEnabled("org.zfsonlinux:userobj_accounting");
      fs.ProjectAccounting = fs.Accounting && this._s.IsEnabled("org.zfsonlinux:project_quota")
        && os.Block.Length >= ProjectUsedAt + Slot && os.Block[ProjectUsedAt] != 0;
      (fs.Compression, fs.ZstdLevel) = this.EffectiveCompression(dirObject, headObject);
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
      if (bp.Dvas[0].Gang) return true;
      if (!this._s.Pool.TopLevelVdevs.TryGetValue(bp.Dvas[0].Vdev, out var vdev)) return true;
      var data = vdev.Read(bp.Dvas[0].Offset, bp.PhysicalSize);
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
        if ((Value("ZPL_FLAGS") & ZfsProjectFlag) != 0) z.Project = Value("ZPL_PROJID");
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
        this.WriteContents(fs.Os, d, blocks, blockSize, fs.Compression, fs.ZstdLevel);
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
      var projects = new Dictionary<string, long>(StringComparer.Ordinal);
      void Charge(Dictionary<string, long> into, string key, long delta) => into[key] = into.GetValueOrDefault(key) + delta;
      void ChargeAll(Znode z, long bytes, long objects, bool space, bool count) {
        if (space) {
          Charge(users, z.Uid.ToString("x"), bytes);
          Charge(groups, z.Gid.ToString("x"), bytes);
          if (fs.ProjectAccounting) Charge(projects, z.Project.ToString("x"), bytes);
        }
        if (count) {
          Charge(users, "obj-" + z.Uid.ToString("x"), objects);
          Charge(groups, "obj-" + z.Gid.ToString("x"), objects);
          if (fs.ProjectAccounting) Charge(projects, "obj-" + z.Project.ToString("x"), objects);
        }
      }
      foreach (var z in fs.Znodes.Values) {
        var d = z.Dnode;
        var synced = z.Freed || os.Dirty.Contains(z.Object);
        if (!synced) continue;
        if (!d.IsNew)
          ChargeAll(z, -(Slot + d.OriginalUsed), -1,
            (d.OriginalFlags & FlagUserUsedAccounted) != 0, (d.OriginalFlags & FlagUserObjUsedAccounted) != 0);
        if (!z.Freed) {
          d.Flags |= FlagUserUsedAccounted;
          if (fs.ObjectAccounting) d.Flags |= FlagUserObjUsedAccounted;
          ChargeAll(z, Slot + d.Used, 1, true, fs.ObjectAccounting);
        }
      }
      this.UpdateUsedZap(os, UserUsedAt, users);
      this.UpdateUsedZap(os, GroupUsedAt, groups);
      if (fs.ProjectAccounting) this.UpdateUsedZap(os, ProjectUsedAt, projects);
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
        if (fs.Os.Held.Count > 0) this.PlanDeadlist(fs);
      }
      if (this._dedupFrees.Count > 0) this.PlanDedup();
      if (this._emptyBpobjReleased > 0) this.ReleaseEmptyBpobj();
      this.TouchDirChain(this.MosDirectory());

      // space maps: every metaslab the guess names is rewritten whole
      foreach (var (g, count) in this._guess.OrderBy(g => g.Key)) {
        var (top, m) = this._s.Metaslab(g);
        Dnode d;
        ulong obj;
        if (top.SpaceMaps[m] != 0) {
          obj = top.SpaceMaps[m];
          d = this.Load(mos, obj);
          mos.Dirty.Add(obj);
          this.FreeTree(mos, d, spill: false);
        } else {
          d = this.NewSpaceMap(NewSpaceMapBlock);
          obj = this.NewObject(mos, d);
          if (!this._newArrays.TryGetValue(top.Id, out var array))
            this._newArrays[top.Id] = array = (ulong[])top.SpaceMaps.Clone();
          array[m] = obj;
        }
        var blocks = new NewBlock[count];
        var changes = new Dictionary<ulong, NewBlock?>();
        for (var i = 0; i < count; ++i)
          changes[(ulong)i] = blocks[i] = this.AllocBlock(mos, d, d.DataBlockSize, OtSpaceMap, 0);
        this.SetBlocks(mos, d, changes);
        this._maps[g] = new MapPlan { Object = obj, Dnode = d, Blocks = blocks };
      }

      foreach (var (vdev, _) in this._newArrays.OrderBy(a => a.Key)) {
        var top = this._s.Tops[vdev];
        var d = this.Load(mos, top.MetaslabArray);
        mos.Dirty.Add(top.MetaslabArray);
        this.FreeTree(mos, d, spill: false);
        var blockSize = d.DataBlockSize;
        var count = (int)((top.MetaslabCount * 8L + blockSize - 1) / blockSize);
        var arrayBlocks = new NewBlock[count];
        var changes = new Dictionary<ulong, NewBlock?>();
        for (var i = 0; i < count; ++i)
          changes[(ulong)i] = arrayBlocks[i] = this.AllocBlock(mos, d, blockSize, OtObjectArray, 0);
        this.SetBlocks(mos, d, changes);
        this._arrayBlocks[vdev] = arrayBlocks;
      }

      if (this._s.LogSpacemapZap != 0) this.PlanLogSpacemap();
      if (this._newMaps > 0) {
        // spacemap_histogram counts every space map object with a histogram
        this.RewriteZap(mos, this._s.FeaturesForWrite, entries => {
          var at = entries.FindIndex(e => e.Name == "com.delphix:spacemap_histogram");
          if (at < 0) throw new InvalidDataException("ZFS: spacemap_histogram is not in the write list.");
          entries[at] = (entries[at].Name, entries[at].Value + (ulong)this._newMaps);
        });
      }

      this.FlushZapEdits();
      this.CommitObjset(mos);
    }

    private int _newMaps;
    private int _emptyBpobjReleased;
    private const byte OtBpobj = 5, OtBpobjHeader = 6;
    private const int BpobjBlock = 128 * 1024;

    /// <summary>
    /// The blocks a snapshot still holds go on the head dataset's deadlist: each into the bpobj of the
    /// deadlist entry with the largest minimum txg below its birth (an entry still on the shared empty
    /// bpobj gets a bpobj of its own). The previous snapshot's unique bytes grow by what only it
    /// references now (<c>docs/ZFS-ON-DISK.md</c>, "Snapshots and deadlists").
    /// </summary>
    private void PlanDeadlist(Fs fs) {
      var mos = this._mos;
      var head = this.Load(mos, fs.HeadObject).Bonus;
      var deadlistObject = U64(head, 64);
      var prevObject = U64(head, 8);
      var entries = mos.Old.Zap(deadlistObject, "deadlist").Select(e => (MinTxg: Convert.ToUInt64(e.Name, 16), Bpobj: e.Value))
        .OrderBy(e => e.MinTxg).ToList();
      var emptyBpobj = mos.Old.Zap(1, "MOS object directory").FirstOrDefault(e => e.Name == "empty_bpobj")?.Value ?? 0;

      var prev = this.Load(mos, prevObject);
      mos.Dirty.Add(prevObject);
      var prevBonus = prev.Bonus;
      var prevChildOfHead = U64(prevBonus, 24) == fs.HeadObject;
      var prevPrevTxg = U64(prevBonus, 16);

      var groups = new SortedDictionary<int, List<byte[]>>();
      foreach (var raw in fs.Os.Held) {
        var birth = ZfsBlockPointer.Read(raw).LogicalBirth;
        var index = entries.FindLastIndex(e => e.MinTxg < birth);
        if (index < 0) throw new InvalidDataException("ZFS: a held block is older than every deadlist entry.");
        if (!groups.TryGetValue(index, out var list)) groups[index] = list = [];
        list.Add(raw);
        if (prevChildOfHead && birth > prevPrevTxg) Add64(prevBonus, 96, this.ChargeOf(ZfsBlockPointer.Read(raw)).Used);
      }

      var replaced = new Dictionary<ulong, ulong>();
      long used = 0, compressed = 0, uncompressed = 0;
      foreach (var (index, list) in groups) {
        var bpobj = entries[index].Bpobj;
        Dnode d;
        if (bpobj == emptyBpobj) {
          d = Dnode.Create(OtBpobj, OtBpobjHeader, 48, BpobjBlock, 17, 1, FlagUsedBytes);
          var fresh = this.NewObject(mos, d);
          replaced[entries[index].MinTxg] = fresh;
          ++this._emptyBpobjReleased;
          bpobj = fresh;
        } else {
          d = this.Load(mos, bpobj);
          mos.Dirty.Add(bpobj);
        }
        var bonus = d.Bonus;
        var count = U64(bonus, 0);
        var blockSize = d.DataBlockSize;
        var perBlock = (ulong)(blockSize / Bp);
        var changes = new Dictionary<ulong, byte[]>();
        foreach (var raw in list) {
          var blockId = count / perBlock;
          if (!changes.TryGetValue(blockId, out var data)) {
            data = new byte[blockSize];
            if (!d.IsNew && blockId <= mos.Old.RequireDnode(bpobj, "bpobj").MaxBlockId && count % perBlock != 0)
              mos.Old.ReadBlock(mos.Old.RequireDnode(bpobj, "bpobj"), blockId).AsSpan(0, blockSize).CopyTo(data);
            changes[blockId] = data;
          }
          StoredPointer(raw).CopyTo(data.AsSpan((int)(count % perBlock) * Bp));
          ++count;
          var (u, c, x) = this.ChargeOf(ZfsBlockPointer.Read(raw));
          Add64(bonus, 8, u);
          Add64(bonus, 16, c);
          Add64(bonus, 24, x);
          used += u;
          compressed += c;
          uncompressed += x;
        }
        W64(bonus, 0, count);
        var blocks = new Dictionary<ulong, NewBlock?>();
        foreach (var (blockId, data) in changes) {
          var block = this.AllocBlock(mos, d, blockSize, OtBpobj, 0);
          block.Data = data;
          blocks[blockId] = block;
        }
        this.SetBlocks(mos, d, blocks);
      }

      // the deadlist's header totals, and entries moved off the empty bpobj
      var deadlist = this.Load(mos, deadlistObject);
      mos.Dirty.Add(deadlistObject);
      Add64(deadlist.Bonus, 0, used);
      Add64(deadlist.Bonus, 8, compressed);
      Add64(deadlist.Bonus, 16, uncompressed);
      if (replaced.Count > 0)
        this.RewriteZap(mos, deadlistObject, list => {
          for (var i = 0; i < list.Count; ++i)
            if (replaced.TryGetValue(Convert.ToUInt64(list[i].Name, 16), out var obj)) list[i] = (list[i].Name, obj);
        });
    }

    /// <summary>A block pointer as a bpobj stores it: no fill (free flag clear), no checksum unless deduplicated, an embedded one without its payload.</summary>
    private static byte[] StoredPointer(byte[] raw) {
      var stored = raw.ToArray();
      if (IsEmbedded(raw)) {
        stored.AsSpan().Clear();
        W64(stored, 0x30, U64(raw, 0x30));
        W64(stored, 0x50, U64(raw, 0x50));
        return stored;
      }
      if (((U64(raw, 0x30) >> 62) & 1) == 0) stored.AsSpan(0x60, 32).Clear();
      W64(stored, 0x58, 0);
      return stored;
    }

    /// <summary>empty_bpobj counts the deadlist entries sharing the empty bpobj; at 0 that object goes.</summary>
    private void ReleaseEmptyBpobj() {
      var mos = this._mos;
      var count = mos.Old.Zap(this._s.FeaturesForWrite, "features_for_write").FirstOrDefault(e => e.Name == "com.delphix:empty_bpobj")
        ?? throw new InvalidDataException("ZFS: empty_bpobj is not in the write list.");
      var remaining = count.Value - (ulong)this._emptyBpobjReleased;
      this.RewriteZap(mos, this._s.FeaturesForWrite, entries => {
        var at = entries.FindIndex(e => e.Name == "com.delphix:empty_bpobj");
        entries[at] = (entries[at].Name, remaining);
      });
      if (remaining != 0) return;
      var emptyBpobj = mos.Old.Zap(1, "MOS object directory").First(e => e.Name == "empty_bpobj").Value;
      this.RewriteZap(mos, 1, entries => entries.RemoveAll(e => e.Name == "empty_bpobj"));
      this.FreeObject(mos, emptyBpobj);
    }

    // ── dedup tables ───────────────────────────────────────────────────────

    private readonly List<byte[]> _dedupFrees = [];

    /// <summary>zio_checksum names by checksum id, as the dedup tables' object names spell them.</summary>
    private static readonly string[] ChecksumNames = [
      "inherit", "on", "off", "label", "gang_header", "zilog", "fletcher2", "fletcher4", "sha256",
      "zilog2", "noparity", "sha512", "skein", "edonr", "blake3",
    ];
    private static readonly string[] DdtClasses = ["ditto", "duplicate", "unique"];
    private const int DdtClassDuplicate = 1, DdtClassUnique = 2;
    private const int DdtPhysSize = 64, DdtPhysTypes = 4, DdtPhysBytes = DdtPhysSize * DdtPhysTypes;
    private const int DdtStatWords = 8, DdtHistogramWords = 64 * DdtStatWords;
    private const byte DdtLittleEndian = 0x80, DdtFunctionMask = 0x7F;

    private sealed class DdtEntry {
      public required ulong[] Key;
      public required byte[] Phys;
      public int Class;
      public required int OldClass;
      public ulong[]? OldStat;
      public byte[]? Sample;
    }

    /// <summary>
    /// Deduplicated blocks the edit drops: each takes one reference off its dedup-table entry, which
    /// moves to the unique class at one reference, frees its space and leaves the table at none.
    /// Each class's histogram follows its entries, and a checksum's tables go when they are empty
    /// (<c>docs/ZFS-ON-DISK.md</c>, "Dedup tables").
    /// </summary>
    private void PlanDedup() {
      var mos = this._mos;
      var directory = mos.Old.Zap(1, "MOS object directory").ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
      if (!directory.TryGetValue("DDT-statistics", out var statisticsObject))
        throw new InvalidDataException("ZFS: a deduplicated block, but the pool keeps no dedup statistics.");
      var statistics = mos.Old.Zap(statisticsObject, "DDT statistics").ToDictionary(e => e.Name, e => e.Values, StringComparer.Ordinal);
      var directoryRemovals = new HashSet<string>(StringComparer.Ordinal);
      var directoryAdditions = new List<(string Name, ulong Value)>();
      var statisticsUpdates = new Dictionary<string, ulong[]?>(StringComparer.Ordinal);

      foreach (var group in this._dedupFrees.GroupBy(raw => (int)((U64(raw, 0x30) >> 40) & 0xFF))) {
        if (group.Key >= ChecksumNames.Length) throw new InvalidDataException($"ZFS: a deduplicated block with checksum {group.Key}.");
        var names = DdtClasses.Select(c => $"DDT-{ChecksumNames[group.Key]}-zap-{c}").ToArray();
        var objects = names.Select(n => directory.GetValueOrDefault(n)).ToArray();
        var table = new Dictionary<string, DdtEntry>(StringComparer.Ordinal);
        ulong flags = 0;
        byte type = 0;
        for (var c = 0; c < DdtClasses.Length; ++c) {
          if (objects[c] == 0) continue;
          var dnode = mos.Old.RequireDnode(objects[c], "dedup table");
          flags = ZfsZap.FlagsOf(mos.Old.ReadBlock(dnode, 0));
          type = dnode.Type;
          foreach (var e in mos.Old.Zap(dnode))
            table[e.Name] = new DdtEntry {
              Key = e.Key ?? throw new InvalidDataException("ZFS: a dedup table not keyed by integers."),
              Phys = DecodeDdtPhys(e), Class = c, OldClass = c,
            };
        }

        foreach (var raw in group) {
          var key = DdtKey(raw);
          if (!table.TryGetValue(string.Join(':', key.Select(k => k.ToString("x16"))), out var entry))
            throw new InvalidDataException("ZFS: a deduplicated block is missing from its dedup table.");
          entry.OldStat ??= this.DdtStat(entry);
          entry.Sample = raw;
          var p = SelectDdtPhys(entry.Phys, raw);
          var refcnt = U64(entry.Phys, p * DdtPhysSize + 48);
          if (refcnt == 0) throw new InvalidDataException("ZFS: a dedup table entry has fewer references than blocks point at it.");
          W64(entry.Phys, p * DdtPhysSize + 48, refcnt - 1);
        }

        // as a sync would leave the touched entries: unreferenced copies freed, the class following the count
        var histograms = names.Select(n => statistics.TryGetValue(n, out var h) && h.Length == DdtHistogramWords ? h.ToArray() : new ulong[DdtHistogramWords]).ToArray();
        var changed = new HashSet<int>();
        foreach (var entry in table.Values.Where(e => e.OldStat != null)) {
          ulong total = 0;
          for (var p = 0; p < DdtPhysTypes; ++p) {
            var phys = entry.Phys.AsSpan(p * DdtPhysSize, DdtPhysSize);
            if (U64(phys, 56) == 0) continue;
            // a ditto copy (no longer made) goes whenever its entry is synced
            if (p == 0 || U64(phys, 48) == 0) {
              this.FreeDdtPhys(entry.Sample!, phys);
              phys.Clear();
              continue;
            }
            total += U64(phys, 48);
          }
          AddDdtStat(histograms[entry.OldClass], entry.OldStat!, negate: true);
          changed.Add(entry.OldClass);
          entry.Class = total == 0 ? -1 : total > 1 ? DdtClassDuplicate : DdtClassUnique;
          if (entry.Class < 0) continue;
          AddDdtStat(histograms[entry.Class], this.DdtStat(entry), negate: false);
          changed.Add(entry.Class);
        }

        var anyLeft = table.Values.Any(e => e.Class >= 0);
        for (var c = 0; c < DdtClasses.Length; ++c) {
          if (!anyLeft) {
            // the last entry of this checksum gone: its tables and their histograms go
            if (objects[c] == 0) continue;
            directoryRemovals.Add(names[c]);
            statisticsUpdates[names[c]] = null;
            this.FreeObject(mos, objects[c]);
            continue;
          }
          if (!changed.Contains(c)) continue;
          var members = table.Values.Where(e => e.Class == c).Select(DdtZapEntry).ToList();
          if (objects[c] != 0) {
            statisticsUpdates[names[c]] = histograms[c];
            this.EditZap(mos, objects[c], list => {
              list.Clear();
              list.AddRange(members);
            });
          } else if (members.Count > 0) {
            var d = Dnode.Create(type, 0, 0, ZfsWriter.FatZapBlock, 15, 3, FlagUsedBytes);
            var obj = this.NewObject(mos, d);
            var (blockSize, blocks) = ZfsWriter.BuildZap(members, fat: true, flags);
            this.WriteContents(mos, d, blocks, blockSize);
            directoryAdditions.Add((names[c], obj));
            statisticsUpdates[names[c]] = histograms[c];
          }
        }
      }

      if (directoryRemovals.Count > 0 || directoryAdditions.Count > 0)
        this.RewriteZap(mos, 1, entries => {
          entries.RemoveAll(e => directoryRemovals.Contains(e.Name));
          entries.AddRange(directoryAdditions);
        });
      if (statisticsUpdates.Count > 0)
        this.EditZap(mos, statisticsObject, entries => {
          entries.RemoveAll(e => statisticsUpdates.ContainsKey(e.Name));
          foreach (var (name, histogram) in statisticsUpdates)
            if (histogram != null) entries.Add(new ZfsZapEntry(name, 8, histogram));
        });
    }

    /// <summary>A dedup-table key: the block's checksum, then its sizes and compression (the crypt bit stays clear).</summary>
    private static ulong[] DdtKey(byte[] raw) =>
      [U64(raw, 0x60), U64(raw, 0x68), U64(raw, 0x70), U64(raw, 0x78), U64(raw, 0x30) & ((1UL << 39) - 1)];

    /// <summary>The copy (single, double, triple or ditto) that holds this block: same first DVA, same physical birth.</summary>
    private static int SelectDdtPhys(byte[] phys, byte[] raw) {
      var birth = U64(raw, 0x48) != 0 ? U64(raw, 0x48) : U64(raw, 0x50);
      for (var p = 0; p < DdtPhysTypes; ++p) {
        var at = p * DdtPhysSize;
        if (U64(phys, at) == U64(raw, 0) && U64(phys, at + 8) == U64(raw, 8) && U64(phys, at + 56) == birth) return p;
      }
      throw new InvalidDataException("ZFS: no copy in its dedup table entry matches a deduplicated block.");
    }

    /// <summary>A dedup table value: a version byte (function, byte order), then the four copies, zero-length encoded when that is shorter.</summary>
    private static byte[] DecodeDdtPhys(ZfsZapEntry e) {
      if (e.IntegerLength != 1 || e.Values.Length < 1) throw new InvalidDataException("ZFS: a dedup table value is not a byte array.");
      var stored = e.Values.Select(v => (byte)v).ToArray();
      var phys = new byte[DdtPhysBytes];
      var function = stored[0] & DdtFunctionMask;
      if (function == ZfsConstants.CompressZle)
        Compression.Core.Transforms.ZleEncoding.Decode(stored.AsSpan(1), phys);
      else if (function is ZfsConstants.CompressOff or ZfsConstants.CompressInherit && stored.Length - 1 >= DdtPhysBytes)
        stored.AsSpan(1, DdtPhysBytes).CopyTo(phys);
      else
        throw new InvalidDataException($"ZFS: a dedup table value compressed with {function}.");
      if ((stored[0] & DdtLittleEndian) == 0)
        for (var i = 0; i < DdtPhysBytes; i += 8) phys.AsSpan(i, 8).Reverse();
      return phys;
    }

    private static ZfsZapEntry DdtZapEntry(DdtEntry entry) {
      var encoded = Compression.Core.Transforms.ZleEncoding.Encode(entry.Phys);
      byte[] stored = encoded.Length < DdtPhysBytes
        ? [(byte)(ZfsConstants.CompressZle | DdtLittleEndian), .. encoded]
        : [(byte)(ZfsConstants.CompressOff | DdtLittleEndian), .. entry.Phys];
      return new ZfsZapEntry(string.Join(':', entry.Key.Select(k => k.ToString("x16"))), 1, stored.Select(b => (ulong)b).ToArray()) { Key = entry.Key };
    }

    /// <summary>
    /// ddt_stat of one entry: per copy present, one block of the key's sizes and its deflated
    /// allocation, and as many referenced blocks as its count.
    /// </summary>
    private ulong[] DdtStat(DdtEntry entry) {
      var stat = new ulong[DdtStatWords];
      var lsize = ((entry.Key[4] & 0xFFFF) + 1) << ZfsConstants.SectorShift;
      var psize = (((entry.Key[4] >> 16) & 0xFFFF) + 1) << ZfsConstants.SectorShift;
      for (var p = 0; p < DdtPhysTypes; ++p) {
        var at = p * DdtPhysSize;
        if (U64(entry.Phys, at + 56) == 0) continue;
        ulong dsize = 0;
        for (var d = 0; d < 3; ++d) {
          var word = U64(entry.Phys, at + d * 16);
          var asize = (long)(word & 0xFFFFFF) << ZfsConstants.SectorShift;
          var vdev = (int)(word >> 32);
          if (asize != 0 && vdev < this._s.Tops.Count) dsize += (ulong)this._s.Tops[vdev].Charged(asize);
        }
        var refcnt = U64(entry.Phys, at + 48);
        stat[0] += 1;
        stat[1] += lsize;
        stat[2] += psize;
        stat[3] += dsize;
        stat[4] += refcnt;
        stat[5] += lsize * refcnt;
        stat[6] += psize * refcnt;
        stat[7] += dsize * refcnt;
      }
      return stat;
    }

    /// <summary>Adds (or takes away) an entry's stat in the histogram bucket of its referenced-block count's highest bit.</summary>
    private static void AddDdtStat(ulong[] histogram, ulong[] stat, bool negate) {
      if (stat[4] == 0) return;
      var bucket = 63 - BitOperations.LeadingZeroCount(stat[4]);
      for (var i = 0; i < DdtStatWords; ++i)
        histogram[bucket * DdtStatWords + i] = negate
          ? unchecked(histogram[bucket * DdtStatWords + i] - stat[i])
          : unchecked(histogram[bucket * DdtStatWords + i] + stat[i]);
    }

    /// <summary>Frees one copy's DVAs: <paramref name="sample"/> is a block pointer of the entry, standing in for everything but the DVAs.</summary>
    private void FreeDdtPhys(byte[] sample, ReadOnlySpan<byte> phys) {
      var raw = sample.ToArray();
      phys[..48].CopyTo(raw);
      this.FreeSpace(ZfsBlockPointer.Read(raw), 0);
    }

    /// <summary>A new space map object: with spacemap_histogram one block pointer and the 320-byte header, else three and 24 bytes.</summary>
    private Dnode NewSpaceMap(int blockSize) {
      var histogram = this._s.IsEnabled("com.delphix:spacemap_histogram");
      if (histogram) ++this._newMaps;
      return Dnode.Create(OtSpaceMap, OtSpaceMapHeader, histogram ? 320 : 24, blockSize, 14, histogram ? 1 : 3, FlagUsedBytes);
    }

    /// <summary>
    /// log_spacemap: the metaslabs whose maps this txg rewrites are flushed in it. Their unflushed
    /// txg becomes this txg, and an (empty) log space map for this txg is registered, since every
    /// unflushed txg must name one.
    /// </summary>
    private void PlanLogSpacemap() {
      var mos = this._mos;
      var log = this.NewSpaceMap(128 * 1024);
      this._logSpacemap = log;
      this._logSpacemapObject = this.NewObject(mos, log);
      this.RewriteZap(mos, this._s.LogSpacemapZap, entries => entries.Add((this.Txg.ToString("x"), this._logSpacemapObject)));

      foreach (var top in this._s.Tops) {
        if (!Enumerable.Range(top.Base, top.MetaslabCount).Any(this._guess.ContainsKey)) continue;
        if (top.UnflushedObject == 0)
          throw new NotSupportedException("ZFS: log_spacemap is active but a vdev keeps no unflushed txgs.");
        var d = this.Load(mos, top.UnflushedObject);
        mos.Dirty.Add(top.UnflushedObject);
        var blockSize = d.DataBlockSize;
        var count = (int)((top.MetaslabCount * 8L + blockSize - 1) / blockSize);
        var bytes = new byte[count * blockSize];
        for (var m = 0; m < top.MetaslabCount; ++m)
          W64(bytes, m * 8, this._guess.ContainsKey(top.Base + m) ? this.Txg : top.Unflushed[m]);
        this.FreeTree(mos, d, spill: false);
        this.WriteContents(mos, d, Enumerable.Range(0, count).Select(i => (byte[]?)bytes.AsSpan(i * blockSize, blockSize).ToArray()).ToList(), blockSize);
      }
    }

    private Dnode? _logSpacemap;
    private ulong _logSpacemapObject;

    /// <summary>
    /// Rewrites a MOS ZAP after <paramref name="change"/> edits its single-integer entries; entries
    /// holding other values (arrays, strings) are kept as they are.
    /// </summary>
    private void RewriteZap(Os os, ulong obj, Action<List<(string Name, ulong Value)>> change) =>
      this.EditZap(os, obj, all => {
        static bool Simple(ZfsZapEntry e) => e.IntegerLength == 8 && e.Values.Length == 1 && e.Key == null;
        var simple = all.Where(Simple).Select(e => (e.Name, e.Value)).ToList();
        var others = all.Where(e => !Simple(e)).ToList();
        change(simple);
        all.Clear();
        all.AddRange(others);
        all.AddRange(simple.Select(e => new ZfsZapEntry(e.Name, 8, [e.Value])));
      });

    private readonly SortedDictionary<ulong, List<Action<List<ZfsZapEntry>>>> _zapEdits = [];

    /// <summary>
    /// Queues a change to a MOS ZAP's entries. Every change to one object is applied together by
    /// <see cref="FlushZapEdits"/>, so two parts of the plan editing the same ZAP both land.
    /// </summary>
    private void EditZap(Os os, ulong obj, Action<List<ZfsZapEntry>> change) {
      if (os != this._mos) throw new InvalidOperationException("ZFS: only MOS ZAPs are queued.");
      if (!this._zapEdits.TryGetValue(obj, out var list)) this._zapEdits[obj] = list = [];
      list.Add(change);
    }

    /// <summary>Rewrites every queued ZAP whole; a fat ZAP stays fat and keeps its flags.</summary>
    private void FlushZapEdits() {
      var mos = this._mos;
      foreach (var (obj, changes) in this._zapEdits) {
        var dnode = mos.Old.RequireDnode(obj, "ZAP");
        var entries = mos.Old.Zap(dnode);
        var first = mos.Old.ReadBlock(dnode, 0);
        var fat = U64(first, 0) == ZfsConstants.ZbtHeader;
        foreach (var change in changes) change(entries);
        var d = this.Load(mos, obj);
        mos.Dirty.Add(obj);
        this.FreeTree(mos, d, spill: false);
        var (blockSize, blocks) = ZfsWriter.BuildZap(entries, fat, ZfsZap.FlagsOf(first));
        this.WriteContents(mos, d, blocks, blockSize);
      }
      this._zapEdits.Clear();
    }

    /// <summary>
    /// The <c>compression</c> property a filesystem's new data is written with: its own or the
    /// nearest ancestor's (0 = inherit), <c>on</c> meaning lzjb on version 28; off when unset or
    /// when it names an algorithm this pool version cannot hold.
    /// </summary>
    private (byte Algorithm, int ZstdLevel) EffectiveCompression(ulong dir, ulong headDataset) {
      var mos = this._mos.Old;
      var lz4 = this._s.IsEnabled("org.illumos:lz4_compress");
      for (var guard = 0; dir != 0 && guard < 64; ++guard) {
        var d = mos.RequireDnode(dir, "DSL directory");
        var props = U64(d.Bonus, 80);
        var value = props == 0 ? null : mos.Zap(props, "DSL properties").FirstOrDefault(p => p.Name == "compression");
        // the low 7 bits name the algorithm; zstd keeps its level above them
        var algorithm = (byte)((value?.Value ?? 0) & 0x7F);
        if (value != null && algorithm != ZfsConstants.CompressInherit)
          return algorithm switch {
            ZfsConstants.CompressOn => (lz4 ? ZfsConstants.CompressLz4 : ZfsConstants.CompressLzjb, 0),
            ZfsConstants.CompressLzjb => (ZfsConstants.CompressLzjb, 0),
            ZfsConstants.CompressLz4 when lz4 => (ZfsConstants.CompressLz4, 0),
            >= ZfsConstants.CompressGzip1 and <= ZfsConstants.CompressGzip9 => (algorithm, 0),
            ZfsConstants.CompressZle => (ZfsConstants.CompressZle, 0),
            // zstd only where the dataset already carries the feature: writing it must not activate it
            ZfsConstants.CompressZstd when this.ZstdActive(headDataset) =>
              (ZfsConstants.CompressZstd, (int)(value.Value >> 7) is >= 1 and <= 19 and var level ? level : 3),
            _ => (ZfsConstants.CompressOff, 0),
          };
        dir = U64(d.Bonus, 16);
      }
      return (ZfsConstants.CompressOff, 0);
    }

    /// <summary>Whether a dataset's object (turned into a ZAP) carries <c>org.freebsd:zstd_compress</c>.</summary>
    private bool ZstdActive(ulong headDataset) {
      var d = this._mos.Old.RequireDnode(headDataset, "DSL dataset");
      return d.Type == OtZapMetadata && this._mos.Old.Zap(d).Any(e => e.Name == "org.freebsd:zstd_compress");
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
      foreach (var g in this._touched.Concat(guess.Keys).Distinct().Order().ToList()) {
        var (top, m) = this._s.Metaslab(g);
        var blockSize = this._maps.TryGetValue(g, out var plan) ? plan.Dnode.DataBlockSize
          : top.SpaceMaps[m] != 0 ? top.SpaceMapBlockSize[m] : NewSpaceMapBlock;
        var words = this.Words(g).Count;
        var need = Math.Max(1, (int)((words * 8L + blockSize - 1) / blockSize));
        if (guess.TryGetValue(g, out var have) && have >= need) continue;
        guess[g] = Math.Max(need, have);
        settled = false;
      }
      return settled;
    }

    private List<ulong> Words(int g) {
      var (top, m) = this._s.Metaslab(g);
      var start = (long)m << top.MetaslabShift;
      var words = new List<ulong> { 2UL << 62 | 1UL << 50 | this.Txg };      // debug: ALLOC, pass 1, txg
      var maxRun = (long)SmRunMax << top.Ashift;
      foreach (var (from, to) in this._allocated[g].Ranges)
        for (var at = from; at < to;) {
          var run = Math.Min(to - at, maxRun);
          words.Add((ulong)((at - start) >> top.Ashift) << 16 | (ulong)((run >> top.Ashift) - 1));
          at += run;
        }
      return words;
    }

    public Commit Seal() {
      var mos = this._mos;
      foreach (var fs in this._filesystems) {
        var root = this.SealObjset(fs.Os);
        var head = this.Load(mos, fs.HeadObject).Bonus;
        // held blocks leave what the head references, not what it uniquely holds or its directory uses
        Add64(head, 72, fs.Os.Used - fs.Os.HeldUsed);
        Add64(head, 80, fs.Os.Compressed - fs.Os.HeldCompressed);
        Add64(head, 88, fs.Os.Uncompressed - fs.Os.HeldUncompressed);
        Add64(head, 96, fs.Os.Used);
        root.CopyTo(head[128..]);
        this.Transfer(fs.DirObject, fs.Os.Used, fs.Os.Compressed, fs.Os.Uncompressed, fs.Os.Used, UsedRefReservation, UsedHead);
        if (fs.Os.HeldUsed != 0) {
          // they are now charged to the directory's snapshots — those born after its origin (a
          // clone's older blocks were never charged to it)
          var dir = this.Load(mos, fs.DirObject).Bonus;
          var origin = U64(dir, 24);
          var originTxg = origin == 0 ? 0 : U64(mos.Old.RequireDnode(origin, "origin snapshot").Bonus, 56);
          var moved = fs.Os.Held.Select(raw => ZfsBlockPointer.Read(raw)).Where(bp => bp.LogicalBirth > originTxg).Sum(bp => this.ChargeOf(bp).Used);
          if ((U64(dir, 96) & 1) != 0 && moved != 0) {
            Add64(dir, 104 + UsedHead * 8, -moved);
            Add64(dir, 104 + UsedSnap * 8, moved);
          }
        }
      }

      foreach (var (g, plan) in this._maps) {
        var (top, m) = this._s.Metaslab(g);
        var words = this.Words(g);
        var blockSize = plan.Dnode.DataBlockSize;
        var bytes = new byte[plan.Blocks.Length * blockSize];
        if (words.Count * 8 > bytes.Length) throw new InvalidOperationException("ZFS: a space map outgrew its planned blocks.");
        for (var i = 0; i < words.Count; ++i) W64(bytes, i * 8, words[i]);
        for (var i = 0; i < plan.Blocks.Length; ++i)
          plan.Blocks[i].Data = bytes.AsSpan(i * blockSize, blockSize).ToArray();
        var bonus = plan.Dnode.Bonus;
        W64(bonus, 0, plan.Object);
        W64(bonus, 8, (ulong)(words.Count * 8));
        W64(bonus, 16, (ulong)this._allocated[g].Total);
        if (bonus.Length >= 320) {
          var start = (long)m << top.MetaslabShift;
          ZfsWriter.SpaceMapHistogram(bonus[64..], start, start + (1L << top.MetaslabShift), this._allocated[g].Ranges, top.Ashift);
        }
      }
      if (this._logSpacemap is { } log) {
        var bonus = log.Bonus;
        bonus.Clear();
        W64(bonus, 0, this._logSpacemapObject);                    // empty: nothing is unflushed in this txg
      }
      foreach (var (vdev, array) in this._newArrays) {
        var arrayBlocks = this._arrayBlocks[vdev];
        var blockSize = arrayBlocks[0].Size;
        var bytes = new byte[arrayBlocks.Length * blockSize];
        for (var m = 0; m < array.Length; ++m) W64(bytes, m * 8, array[m]);
        for (var i = 0; i < arrayBlocks.Length; ++i)
          arrayBlocks[i].Data = bytes.AsSpan(i * blockSize, blockSize).ToArray();
      }

      // the MOS's own blocks are charged to $MOS; its accounting is final once it is planned
      this.Transfer(this.MosDirectory(), mos.Used, mos.Compressed, mos.Uncompressed, mos.Used, UsedChildReserve, UsedHead);
      var mosRoot = this.SealObjset(mos);

      var commit = new Commit();
      // device numbers: every vdev's children in order, as PoolState.Devices lists them
      var first = new int[this._s.Tops.Count];
      for (var t = 1; t < first.Length; ++t) first[t] = first[t - 1] + this._s.Tops[t - 1].Children.Length;
      foreach (var block in this._blocks) {
        if (block.Pointer == null) throw new InvalidOperationException("ZFS: a block was planned but never placed in a tree.");
        var top = this._s.Tops[block.Vdev];
        if (top.Parity > 0) {
          // RAID-Z: the block's data and parity columns, each on its device
          var columns = ZfsRaidzVdev.Map(block.Offset, block.Size, top.Children.Length, top.Parity, top.Ashift);
          var encoded = ZfsRaidzVdev.Encode(block.Data!, columns, top.Parity);
          for (var c = 0; c < columns.Length; ++c)
            if (columns[c].Size > 0)
              commit.Blocks.Add((first[block.Vdev] + columns[c].Child, ZfsConstants.DataStart + columns[c].Offset, encoded[c]));
        } else {
          // a plain device, or every side of a mirror
          for (var d = 0; d < top.Children.Length; ++d)
            commit.Blocks.Add((first[block.Vdev] + d, ZfsConstants.DataStart + block.Offset, block.Data!));
        }
      }
      foreach (var top in this._s.Tops) {
        var slots = ZfsConstants.UberblockRingSize / top.SlotSize;
        for (var d = 0; d < top.Children.Length; ++d) {
          var aligned = top.Children[d].Length / ZfsConstants.LabelSize * ZfsConstants.LabelSize;
          foreach (var labelAt in PoolState.LabelOffsets(aligned)) {
            var ub = new byte[top.SlotSize];
            this._s.Uberblock.AsSpan(0, Math.Min(ub.Length, this._s.Uberblock.Length) - 40).CopyTo(ub);
            W64(ub, 0x10, this.Txg);
            W64(ub, 0x20, (ulong)new DateTimeOffset(this._now).ToUnixTimeSeconds());
            mosRoot.CopyTo(ub, 0x28);
            var at = labelAt + ZfsConstants.UberblockRingOffset + (long)(this.Txg % (ulong)slots) * top.SlotSize;
            ZfsWriter.Seal(ub, at);
            commit.Uberblocks.Add((first[top.Id] + d, at, ub));
          }
        }
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
