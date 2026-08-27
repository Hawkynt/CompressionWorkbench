#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Genuine in-place add/replace/remove for the single-device bcachefs profile
/// emitted by <see cref="BcacheFsWriter"/>.
/// </summary>
/// <remarks>
/// <para>This is deliberately not an extract/re-create path. Unchanged file data
/// is never copied and its physical sectors remain byte-identical. New or replaced
/// payload is written into currently-free buckets, then the b-tree metadata is
/// committed inside the fixed 64-bucket metadata reservation. Removed/replaced
/// extents are zeroed after the new roots have been installed.</para>
/// <para>The metadata commit rebuilds the twelve trees this implementation owns
/// (extents, inodes, dirents, subvolume/snapshot roots and allocation/accounting
/// trees) but does not rewrite the image or any unaffected user-data extent. That
/// keeps mutation O(changed data + filesystem metadata), which is the distinction
/// <c>CanModify</c> is meant to expose for a filesystem.</para>
/// </remarks>
internal static class BcacheFsInPlaceModifier {

  private const int JournalFirstBucket = 33;
  private const int JournalBuckets = 16;
  private const int FirstMetadataBucket = JournalFirstBucket + JournalBuckets;
  private const int MetadataBuckets = 64;

  /// <summary>
  /// Whether a bucket belongs to the window <see cref="Commit"/> rewrites.
  /// </summary>
  /// <remarks>
  /// The commit publishes a whole metadata generation into this fixed run, and
  /// zeroes it first. Anything a placement pass leaves there is destroyed and
  /// then unaccounted for, so the run has to be off limits to file data for as
  /// long as the metadata is not being relocated somewhere else.
  /// </remarks>
  internal static bool IsMetadataReservation(long bucket)
    => bucket >= FirstMetadataBucket && bucket < FirstMetadataBucket + MetadataBuckets;
  private const int MaxExtentSectors = 128;
  private const ulong FirstDynamicInode = 2147483648UL;

  private static readonly int[] Btrees = [
    BtreeExtents, BtreeInodes, BtreeDirents,
    BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees, BtreeLoggedOps,
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeAccounting, BtreeBackpointers,
  ];

  private sealed class DirectoryState {
    internal required string Path { get; init; }
    internal required string Name { get; init; }
    internal required string ParentPath { get; init; }
    internal required ulong Inode { get; set; }
    internal Key? ExistingInode { get; init; }
    internal Key? ExistingDirent { get; init; }
    internal ulong DirentOffset { get; set; }
  }

  private sealed class FileState {
    internal required string Path { get; init; }
    internal required string Name { get; init; }
    internal required string ParentPath { get; init; }
    internal required ulong Inode { get; set; }
    internal required long Length { get; set; }
    internal Key? ExistingInode { get; init; }
    internal Key? ExistingDirent { get; init; }
    internal List<Key> ExistingExtents { get; } = [];
    internal List<Key> FinalExtents { get; } = [];
    internal PendingPayload? Pending { get; set; }
    internal ulong DirentOffset { get; set; }
  }

  private sealed class PendingPayload {
    internal required long Length { get; init; }
    internal required Func<Stream> Open { get; init; }
    internal List<long> Buckets { get; } = [];
  }

  private sealed class Model {
    internal required BcacheFsVolume Volume { get; init; }
    internal required Dictionary<string, DirectoryState> Directories { get; init; }
    internal required Dictionary<string, FileState> Files { get; init; }
    internal required Dictionary<int, List<Key>> PreservedTrees { get; init; }
    internal required HashSet<long> OccupiedBuckets { get; init; }
    internal required List<(long Offset, long Length)> FreedRanges { get; init; }
    internal required ulong NextInode { get; set; }
  }

  internal static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    EnsureWritable(image);

    var model = ReadModel(image);
    foreach (var input in inputs) {
      var path = NormalizePath(input.ArchiveName);
      if (path.Length == 0) continue;

      if (input.IsDirectory) {
        EnsureDirectory(model, path);
        continue;
      }

      var parent = Parent(path);
      if (parent.Length != 0) EnsureDirectory(model, parent);
      if (model.Directories.ContainsKey(path))
        throw new InvalidOperationException($"bcachefs: '{path}' is a directory.");

      var length = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      Func<Stream> open = input.InMemoryContent is { } bytes
        ? () => new MemoryStream(bytes, writable: false)
        : () => File.OpenRead(input.FullPath);

      if (model.Files.TryGetValue(path, out var existing)) {
        foreach (var key in existing.ExistingExtents)
          AddFreedRange(model.FreedRanges, key);
        existing.Length = length;
        existing.Pending = new PendingPayload { Length = length, Open = open };
        existing.FinalExtents.Clear();
        continue;
      }

      model.Files[path] = new FileState {
        Path = path,
        Name = Leaf(path),
        ParentPath = parent,
        Inode = model.NextInode++,
        Length = length,
        Pending = new PendingPayload { Length = length, Open = open },
      };
    }

    Commit(image, model);
  }

  internal static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    entryNames ??= [];

    var model = ReadModel(image);
    if (entryNames.Length == 0) return;

    var requested = new HashSet<string>(
      entryNames.Select(NormalizePath).Where(n => n.Length != 0),
      StringComparer.Ordinal);

    var removeFiles = model.Files.Values
      .Where(file => requested.Contains(file.Path) || requested.Contains(file.Name)
        || requested.Any(r => r.Length != 0 && file.Path.StartsWith(r + "/", StringComparison.Ordinal)))
      .Select(file => file.Path)
      .ToList();

    foreach (var path in removeFiles) {
      var file = model.Files[path];
      foreach (var key in file.ExistingExtents)
        AddFreedRange(model.FreedRanges, key);
      model.Files.Remove(path);
    }

    // Explicit directory removal removes the subtree. Remove-all (the purge verb)
    // also drops every non-root directory, matching a freshly empty filesystem.
    var purge = model.Files.Count == 0;
    var removeDirs = model.Directories.Values
      .Where(d => d.Path.Length != 0 && (purge
        || requested.Contains(d.Path) || requested.Contains(d.Name)
        || requested.Any(r => r.Length != 0 && d.Path.StartsWith(r + "/", StringComparison.Ordinal))))
      .OrderByDescending(d => d.Path.Count(c => c == '/'))
      .Select(d => d.Path)
      .ToList();
    foreach (var path in removeDirs) model.Directories.Remove(path);

    Commit(image, model);
  }

  /// <summary>
  /// Rewrites only the metadata trees, preserving every current file extent.
  /// Useful after a layout operation wants accounting/backpointers normalized
  /// without touching payload bytes.
  /// </summary>
  internal static void NormalizeMetadata(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    Commit(image, ReadModel(image));
  }

  private static Model ReadModel(Stream image) {
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid)
      throw new InvalidDataException(volume.Status);
    if (volume.BucketSectorCount != BucketSectors)
      throw new NotSupportedException(
        $"bcachefs in-place mutation currently requires {BucketSectors}-sector buckets; volume uses {volume.BucketSectorCount}.");

    var unsupportedRoots = volume.Roots.Keys.Except(Btrees).ToArray();
    if (unsupportedRoots.Length != 0)
      throw new NotSupportedException(
        $"bcachefs in-place mutation refuses extra live b-trees ({string.Join(",", unsupportedRoots)}); "
        + "rewriting accounting without owning those trees would be corrupting them.");

    foreach (var required in Btrees)
      if (!volume.Roots.ContainsKey(required))
        throw new NotSupportedException($"bcachefs in-place mutation requires b-tree {required}.");

    var inodeKeys = ReadTree(volume, BtreeInodes)
      .Where(k => k.Type == KeyInodeV3)
      .ToDictionary(k => k.Position.Offset);
    if (!inodeKeys.ContainsKey(RootInode))
      throw new InvalidDataException("bcachefs: root inode is missing.");

    var extentKeys = ReadTree(volume, BtreeExtents);
    if (extentKeys.Any(k => k.Type != KeyExtent))
      throw new NotSupportedException(
        "bcachefs in-place mutation currently supports regular pointer extents; inline/reflink/other extent-key types are left read-only.");

    foreach (var extent in extentKeys)
      ValidateExtent(extent, volume.BucketSectorCount);

    var dirents = ReadTree(volume, BtreeDirents)
      .Where(k => k.Type == KeyDirent && k.Value.Length >= 9)
      .ToList();

    var directories = new Dictionary<string, DirectoryState>(StringComparer.Ordinal) {
      [""] = new DirectoryState {
        Path = "", Name = "", ParentPath = "", Inode = RootInode,
        ExistingInode = inodeKeys[RootInode], DirentOffset = 0,
      },
    };
    var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
    var children = dirents.GroupBy(k => k.Position.Inode).ToDictionary(g => g.Key, g => g.ToList());
    var pending = new Queue<(ulong Inode, string Path)>();
    pending.Enqueue((RootInode, ""));
    var seenDirs = new HashSet<ulong> { RootInode };

    while (pending.Count > 0) {
      var (parentInode, parentPath) = pending.Dequeue();
      if (!children.TryGetValue(parentInode, out var list)) continue;

      foreach (var dirent in list) {
        var target = BinaryPrimitives.ReadUInt64LittleEndian(dirent.Value);
        var type = (byte)(dirent.Value[8] & 0x1F);
        var name = ReadName(dirent.Value.AsSpan(9));
        if (name.Length == 0) continue;
        if (!inodeKeys.TryGetValue(target, out var inode))
          throw new InvalidDataException($"bcachefs: dirent '{name}' points at missing inode {target}.");

        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        if (type == DtDir) {
          if (!seenDirs.Add(target))
            throw new NotSupportedException("bcachefs in-place mutation does not rewrite directory hard links/cycles.");
          directories[path] = new DirectoryState {
            Path = path, Name = name, ParentPath = parentPath, Inode = target,
            ExistingInode = inode, ExistingDirent = dirent, DirentOffset = dirent.Position.Offset,
          };
          pending.Enqueue((target, path));
          continue;
        }

        if (type != DtReg)
          throw new NotSupportedException(
            $"bcachefs in-place mutation currently supports regular files/directories; '{path}' has dirent type {type}.");
        if (inode.Value.Length < 40)
          throw new InvalidDataException($"bcachefs: inode {target} is shorter than inode_v3.");

        files[path] = new FileState {
          Path = path, Name = name, ParentPath = parentPath, Inode = target,
          Length = (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.Value.AsSpan(32)),
          ExistingInode = inode, ExistingDirent = dirent, DirentOffset = dirent.Position.Offset,
        };
      }
    }

    foreach (var extent in extentKeys) {
      var file = files.Values.FirstOrDefault(f => f.Inode == extent.Position.Inode)
        ?? throw new NotSupportedException(
          $"bcachefs extent for inode {extent.Position.Inode} is not a reachable regular file.");
      file.ExistingExtents.Add(extent);
      file.FinalExtents.Add(extent);
    }
    foreach (var file in files.Values)
      file.ExistingExtents.Sort((a, b) => Compare(a.Position, b.Position));

    var occupied = new HashSet<long>();
    foreach (var key in ReadTree(volume, BtreeAlloc))
      if (key.Type == KeyAllocV4 && key.Value.Length > 14 && key.Value[14] != DataFree)
        occupied.Add((long)key.Position.Offset);

    // This modifier intentionally targets the profile emitted here: generation
    // zero everywhere. Reusing a native volume with nonzero bucket generations
    // requires carrying those generations into every new extent pointer.
    foreach (var key in ReadTree(volume, BtreeBucketGens))
      if (key.Type == KeyBucketGens && key.Value.Any(b => b != 0))
        throw new NotSupportedException(
          "bcachefs in-place mutation of volumes with reused/nonzero bucket generations is not implemented yet.");

    var preserved = new Dictionary<int, List<Key>> {
      [BtreeSubvolumes] = ReadTree(volume, BtreeSubvolumes),
      [BtreeSnapshots] = ReadTree(volume, BtreeSnapshots),
      [BtreeSnapshotTrees] = ReadTree(volume, BtreeSnapshotTrees),
      [BtreeLoggedOps] = ReadTree(volume, BtreeLoggedOps),
    };

    var maxInode = inodeKeys.Keys.DefaultIfEmpty(FirstDynamicInode - 1).Max();
    return new Model {
      Volume = volume,
      Directories = directories,
      Files = files,
      PreservedTrees = preserved,
      OccupiedBuckets = occupied,
      FreedRanges = [],
      NextInode = Math.Max(FirstDynamicInode, maxInode + 1),
    };
  }

  private static void EnsureDirectory(Model model, string path) {
    path = NormalizePath(path);
    if (path.Length == 0 || model.Directories.ContainsKey(path)) return;

    var parent = Parent(path);
    EnsureDirectory(model, parent);
    if (model.Files.ContainsKey(path))
      throw new InvalidOperationException($"bcachefs: '{path}' is a file.");

    model.Directories[path] = new DirectoryState {
      Path = path,
      Name = Leaf(path),
      ParentPath = parent,
      Inode = model.NextInode++,
      DirentOffset = 0,
    };
  }

  private static void Commit(Stream image, Model model) {
    AssignNewDirentOffsets(model);
    AllocatePendingData(model);
    WritePendingData(image, model);

    var trees = BuildLogicalTrees(model);
    var metadataBuckets = BuildAllocationTrees(model, trees);
    if (metadataBuckets.Count > MetadataBuckets)
      throw new NotSupportedException(
        $"bcachefs metadata needs {metadataBuckets.Count} buckets; an in-place commit budgets {MetadataBuckets}.");

    // All live source keys were materialized before this point, so the buckets
    // the new generation lands in can be rewritten without needing any old node.
    foreach (var bucket in metadataBuckets)
      ZeroRange(image, bucket * BucketBytes, BucketBytes);

    var roots = new List<(int Btree, int Level, Key Pointer)>(Btrees.Length);
    var slot = 0;
    foreach (var btree in Btrees) {
      var (level, pointer) = WriteTree(image, model.Volume.InternalMagic, btree, trees[btree],
        metadataBuckets, ref slot);
      roots.Add((btree, level, pointer));
    }

    if (slot != metadataBuckets.Count)
      throw new InvalidOperationException(
        $"bcachefs metadata plan drifted: expected {metadataBuckets.Count} buckets, wrote {slot}.");

    PatchSuperblocks(image, roots, metadataBuckets.Count);

    // Deleted/replaced user data becomes forensic free space only after the new
    // roots are live. New payload was deliberately allocated outside these runs,
    // so wiping cannot damage the replacement.
    foreach (var (offset, length) in model.FreedRanges)
      ZeroRange(image, offset, length);

    image.Flush();

    // Internal consistency witness available on every platform; external
    // bcachefs fsck tests remain the authority where the tool exists.
    image.Position = 0;
    var mover = new BcacheFsBlockMover();
    mover.Init(image);
    var discrepancies = mover.DescribeAllocationDiscrepancies(image);
    if (discrepancies.Count != 0)
      throw new InvalidDataException("bcachefs in-place commit left allocation discrepancies: "
        + string.Join("; ", discrepancies));
  }

  private static void AssignNewDirentOffsets(Model model) {
    var used = new Dictionary<ulong, HashSet<ulong>>();
    foreach (var dir in model.Directories.Values) {
      if (dir.Path.Length == 0 || dir.ExistingDirent == null) continue;
      if (!used.TryGetValue(dir.ExistingDirent.Value.Position.Inode, out var set))
        used[dir.ExistingDirent.Value.Position.Inode] = set = [];
      set.Add(dir.ExistingDirent.Value.Position.Offset);
    }
    foreach (var file in model.Files.Values) {
      if (file.ExistingDirent == null) continue;
      if (!used.TryGetValue(file.ExistingDirent.Value.Position.Inode, out var set))
        used[file.ExistingDirent.Value.Position.Inode] = set = [];
      set.Add(file.ExistingDirent.Value.Position.Offset);
    }

    foreach (var dir in model.Directories.Values
      .Where(d => d.Path.Length != 0 && d.ExistingDirent == null)
      .OrderBy(d => d.Path.Count(c => c == '/'))) {
      var parentInode = model.Directories[dir.ParentPath].Inode;
      dir.DirentOffset = ReserveDirentOffset(used, parentInode, dir.Name);
    }
    foreach (var file in model.Files.Values.Where(f => f.ExistingDirent == null)) {
      var parentInode = model.Directories[file.ParentPath].Inode;
      file.DirentOffset = ReserveDirentOffset(used, parentInode, file.Name);
    }
  }

  private static ulong ReserveDirentOffset(Dictionary<ulong, HashSet<ulong>> used, ulong parent, string name) {
    if (!used.TryGetValue(parent, out var set)) used[parent] = set = [];
    var offset = DirentHash(HashSeed(parent), name);
    while (!set.Add(offset)) ++offset;
    return offset;
  }

  private static void AllocatePendingData(Model model) {
    var firstDataBucket = (long)FirstMetadataBucket + MetadataBuckets;
    var firstTailSbBucket = (model.Volume.DeviceSectors - SbSlotSectors) / BucketSectors;
    var free = new Queue<long>();
    for (var bucket = firstDataBucket; bucket < firstTailSbBucket; ++bucket)
      if (!model.OccupiedBuckets.Contains(bucket)) free.Enqueue(bucket);

    foreach (var file in model.Files.Values.Where(f => f.Pending != null)) {
      var remaining = file.Pending!.Length;
      while (remaining > 0) {
        if (free.Count == 0)
          throw new IOException(
            $"bcachefs: not enough free buckets for in-place write of '{file.Path}'.");
        file.Pending.Buckets.Add(free.Dequeue());
        remaining -= Math.Min((long)BucketBytes, remaining);
      }
    }
  }

  private static void WritePendingData(Stream image, Model model) {
    var buffer = new byte[BucketBytes];
    foreach (var file in model.Files.Values.Where(f => f.Pending != null)) {
      file.FinalExtents.Clear();
      var pending = file.Pending!;
      using var source = pending.Open();
      var remaining = pending.Length;
      var logicalSector = 0L;
      var bucketIndex = 0;

      while (remaining > 0) {
        var want = (int)Math.Min(BucketBytes, remaining);
        var got = 0;
        while (got < want) {
          var n = source.Read(buffer, got, want - got);
          if (n <= 0)
            throw new EndOfStreamException(
              $"bcachefs input '{file.Path}' ended at {got} bytes of a {want}-byte extent.");
          got += n;
        }

        var sectors = (got + SectorSize - 1) / SectorSize;
        Array.Clear(buffer, got, sectors * SectorSize - got);
        var firstSector = pending.Buckets[bucketIndex++] * BucketSectors;
        image.Position = firstSector * SectorSize;
        image.Write(buffer, 0, sectors * SectorSize);

        var checksum = DataChecksum(buffer.AsSpan(0, sectors * SectorSize));
        var value = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32(sectors, checksum));
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), ExtentPointer(firstSector));
        file.FinalExtents.Add(new Key(KeyExtent,
          new Bpos(file.Inode, (ulong)(logicalSector + sectors), SnapshotIdMax),
          (uint)sectors, value));

        logicalSector += sectors;
        remaining -= got;
      }
    }
  }

  private static Dictionary<int, List<Key>> BuildLogicalTrees(Model model) {
    var trees = Btrees.ToDictionary(id => id, _ => new List<Key>());

    // Extents: unchanged files retain their exact keys/pointers/checksums; only
    // new/replaced files contribute freshly written keys.
    foreach (var file in model.Files.Values)
      trees[BtreeExtents].AddRange(file.Pending == null ? file.ExistingExtents : file.FinalExtents);

    var childDirectoryCounts = model.Directories.Values
      .Where(d => d.Path.Length != 0)
      .GroupBy(d => d.ParentPath)
      .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    foreach (var dir in model.Directories.Values.OrderBy(d => d.Path.Count(c => c == '/'))) {
      if (dir.Path.Length == 0) {
        trees[BtreeInodes].Add(dir.ExistingInode is { } root
          ? PatchDirectoryLinks(root, childDirectoryCounts.GetValueOrDefault("", 0))
          : InodeKey(RootInode, 0, 0, isDirectory: true, 0, 0,
            childDirectoryCounts.GetValueOrDefault("", 0), RootSubvolume));
        continue;
      }

      var parentInode = model.Directories[dir.ParentPath].Inode;
      trees[BtreeInodes].Add(dir.ExistingInode is { } existing
        ? PatchDirectoryLinks(existing, childDirectoryCounts.GetValueOrDefault(dir.Path, 0))
        : InodeKey(dir.Inode, parentInode, dir.DirentOffset, isDirectory: true,
          0, 0, childDirectoryCounts.GetValueOrDefault(dir.Path, 0)));
      trees[BtreeDirents].Add(dir.ExistingDirent
        ?? DirentKey(parentInode, dir.DirentOffset, dir.Name, dir.Inode, DtDir));
    }

    foreach (var file in model.Files.Values) {
      var parentInode = model.Directories[file.ParentPath].Inode;
      var sectors = (ulong)((file.Length + SectorSize - 1) / SectorSize);
      trees[BtreeInodes].Add(file.ExistingInode is { } existing
        ? PatchFileSize(existing, (ulong)file.Length, sectors)
        : InodeKey(file.Inode, parentInode, file.DirentOffset, isDirectory: false,
          (ulong)file.Length, sectors, 0));
      trees[BtreeDirents].Add(file.ExistingDirent
        ?? DirentKey(parentInode, file.DirentOffset, file.Name, file.Inode, DtReg));
    }

    trees[BtreeSubvolumes].AddRange(model.PreservedTrees[BtreeSubvolumes]);
    trees[BtreeSnapshots].AddRange(model.PreservedTrees[BtreeSnapshots]);
    trees[BtreeSnapshotTrees].AddRange(model.PreservedTrees[BtreeSnapshotTrees]);

    // The workbench profile keeps only the inode allocation cursor in logged_ops.
    // Keeping a stale cursor would hand out an inode we just created.
    var nonCursor = model.PreservedTrees[BtreeLoggedOps]
      .Where(k => k.Type != KeyInodeAllocCursor)
      .ToList();
    if (nonCursor.Count != 0)
      throw new NotSupportedException(
        "bcachefs in-place mutation refuses unknown logged_ops keys in this profile.");
    trees[BtreeLoggedOps].Add(InodeAllocCursorKey(model.NextInode));

    foreach (var list in trees.Values)
      list.Sort((a, b) => Compare(a.Position, b.Position));
    return trees;
  }

  private static List<long> BuildAllocationTrees(Model model, Dictionary<int, List<Key>> trees) {
    var extents = trees[BtreeExtents];
    var alloc = trees[BtreeAlloc];
    var bucketGens = trees[BtreeBucketGens];
    var freespace = trees[BtreeFreespace];
    var accounting = trees[BtreeAccounting];
    var backpointers = trees[BtreeBackpointers];

    var totalBuckets = model.Volume.DeviceSectors / BucketSectors;
    bucketGens.Clear();
    for (long b = 0; b < totalBuckets; b += BucketGensNr)
      bucketGens.Add(BucketGensKey(b));

    var userSectors = UserSectorsByBucket(extents);
    var btreeBuckets = (long)Btrees.Length;

    for (var attempt = 0; attempt < 12; ++attempt) {
      // the generation goes wherever there is room: a fixed run would collide
      // with whatever a placement pass has already put there
      var metadataBuckets = ChooseMetadataBuckets(btreeBuckets, model.Volume.DeviceSectors, userSectors);
      var metadataSet = metadataBuckets.ToHashSet();

      alloc.Clear();
      freespace.Clear();
      accounting.Clear();
      backpointers.Clear();

      var bucketsOf = new ulong[DataUser + 1];
      var sectorsOf = new ulong[DataUser + 1];
      var runStart = -1L;

      for (var bucket = 0L; bucket <= totalBuckets; ++bucket) {
        var free = bucket < totalBuckets;
        if (free) {
          var (type, sectors) = BucketContents(model.Volume.DeviceSectors,
            bucket, metadataSet, userSectors);
          free = type == DataFree;
          ++bucketsOf[type];
          sectorsOf[type] += sectors;
          if (!free) alloc.Add(AllocKey(bucket, type, sectors));
        }

        if (free) {
          if (runStart < 0) runStart = bucket;
          continue;
        }
        if (runStart >= 0) freespace.Add(FreespaceKey(runStart, bucket));
        runStart = -1;
      }

      accounting.Add(NrInodesKey((ulong)trees[BtreeInodes].Count));
      for (byte type = DataFree; type <= DataUser; ++type)
        if (bucketsOf[type] > 0)
          accounting.Add(DevDataTypeKey(type, bucketsOf[type], sectorsOf[type]));

      foreach (var extent in extents) {
        var sector = ExtentSector(extent);
        backpointers.Add(ExtentBackpointerKey(sector, (int)extent.Size, extent.Position));
      }

      var at = 0;
      foreach (var btree in Btrees) {
        var count = NodeCount(trees[btree]);
        for (var i = 0; i < count && at + i < metadataBuckets.Count; ++i) {
          var level = count > 1 && i == count - 1 ? 1 : 0;
          backpointers.Add(NodeBackpointerKey(metadataBuckets[at + i], btree, level, BucketSectors));
        }
        at += count;
      }

      ulong btreeSectors = 0;
      foreach (var btree in Btrees) {
        var count = (ulong)NodeCount(trees[btree]);
        btreeSectors += count * BucketSectors;
        accounting.Add(BtreeAccountingKey(btree, count));
      }

      foreach (var btree in new[] { BtreeExtents, BtreeInodes, BtreeDirents }) {
        var list = trees[btree];
        if (list.Count == 0) continue;
        accounting.Add(SnapshotAccountingKey(SnapshotIdMax, btree,
          (ulong)list.Count, (ulong)list.Sum(k => k.Bytes),
          btree == BtreeExtents ? sectorsOf[DataUser] : 0));
      }

      accounting.Add(ReplicasKey(DataBtree, btreeSectors));
      if (sectorsOf[DataUser] > 0)
        accounting.Add(ReplicasKey(DataUser, sectorsOf[DataUser]));

      foreach (var list in trees.Values)
        list.Sort((a, b) => Compare(a.Position, b.Position));

      var settled = Btrees.Sum(btree => NodeCount(trees[btree]));
      if (settled == btreeBuckets) return metadataBuckets;
      btreeBuckets = settled;
    }

    throw new InvalidOperationException("bcachefs metadata node count did not converge.");
  }

  private static (byte Type, uint Sectors) BucketContents(long deviceSectors, long bucket,
      IReadOnlySet<long> metadataBuckets, IReadOnlyDictionary<long, uint> userSectors) {
    var sbEndSector = PrimarySbSector + 2L * SbSlotSectors;
    if (bucket < sbEndSector / BucketSectors) return (DataSb, BucketSectors);
    if (bucket == sbEndSector / BucketSectors)
      return (DataSb, (uint)(sbEndSector % BucketSectors));

    var firstLastSbBucket = (deviceSectors - SbSlotSectors) / BucketSectors;
    if (bucket >= firstLastSbBucket) return (DataSb, BucketSectors);
    if (bucket < FirstMetadataBucket) return (DataJournal, BucketSectors);
    if (metadataBuckets.Contains(bucket)) return (DataBtree, BucketSectors);
    if (userSectors.TryGetValue(bucket, out var sectors)) return (DataUser, sectors);
    return (DataFree, 0);
  }

  /// <summary>The buckets one commit's metadata generation is written into.</summary>
  /// <remarks>
  /// Taken in order from the first bucket after the journal, skipping whatever
  /// holds file data. A placement pass is free to put extents anywhere, so the
  /// metadata has to be the thing that moves out of the way.
  /// </remarks>
  private static List<long> ChooseMetadataBuckets(long count, long deviceSectors,
      IReadOnlyDictionary<long, uint> userSectors) {
    var lastSbBucket = (deviceSectors - SbSlotSectors) / BucketSectors;
    var chosen = new List<long>((int)count);
    for (var bucket = (long)FirstMetadataBucket; chosen.Count < count && bucket < lastSbBucket; ++bucket)
      if (!userSectors.ContainsKey(bucket))
        chosen.Add(bucket);

    if (chosen.Count < count)
      throw new NotSupportedException(
        $"bcachefs metadata needs {count} free buckets; only {chosen.Count} are available.");
    return chosen;
  }

  private static Dictionary<long, uint> UserSectorsByBucket(IEnumerable<Key> extents) {
    var result = new Dictionary<long, uint>();
    foreach (var extent in extents) {
      var sector = ExtentSector(extent);
      var bucket = sector / BucketSectors;
      result.TryGetValue(bucket, out var current);
      var next = current + extent.Size;
      if (next > BucketSectors)
        throw new InvalidDataException($"bcachefs: bucket {bucket} is overcommitted ({next} sectors).");
      result[bucket] = next;
    }
    return result;
  }

  private static (int Level, Key Pointer) WriteTree(Stream image, ulong magic, int btree,
      List<Key> keys, List<long> buckets, ref int nextSlot) {
    Key Place(BcacheFsNodeBuilder node, ref int slot) {
      if (slot >= buckets.Count)
        throw new NotSupportedException("bcachefs metadata reservation exhausted.");
      var buffer = new byte[BucketBytes];
      var sectors = node.Write(buffer);
      var sector = buckets[slot] * BucketSectors;
      ++slot;
      image.Position = sector * SectorSize;
      image.Write(buffer, 0, sectors * SectorSize);
      return node.Pointer(sector, sectors);
    }

    var tree = new BcacheFsNodeBuilder {
      BtreeId = btree, Seq = NextSeq(), SuperblockMagic = magic,
    };
    foreach (var key in keys) tree.Add(key);

    if (tree.Bytes <= BucketBytes)
      return (0, Place(tree, ref nextSlot));

    var sorted = keys.OrderBy(k => k,
      Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position))).ToList();
    var leaves = new List<BcacheFsNodeBuilder>();
    var root = new BcacheFsNodeBuilder {
      BtreeId = btree, Seq = NextSeq(), SuperblockMagic = magic, Level = 1,
    };

    var index = 0;
    while (index < sorted.Count) {
      var leaf = new BcacheFsNodeBuilder {
        BtreeId = btree, Seq = NextSeq(), SuperblockMagic = magic,
        MinKey = leaves.Count == 0 ? Bpos.Min : Successor(sorted[index - 1].Position),
      };
      var bytes = BcacheFsNodeBuilder.KeysOffset;
      while (index < sorted.Count && bytes + sorted[index].Bytes <= BucketBytes) {
        bytes += sorted[index].Bytes;
        leaf.Add(sorted[index++]);
      }
      if (leaf.Count == 0)
        throw new NotSupportedException("bcachefs key is too large for one b-tree node.");
      leaves.Add(leaf);
    }

    for (var i = 0; i < leaves.Count; ++i) {
      var bounded = new BcacheFsNodeBuilder {
        BtreeId = btree,
        Seq = leaves[i].Seq,
        SuperblockMagic = magic,
        MinKey = leaves[i].MinKey,
        MaxKey = i == leaves.Count - 1 ? Bpos.Max : leaves[i].Keys[^1].Position,
      };
      foreach (var key in leaves[i].Keys) bounded.Add(key);
      root.Add(Place(bounded, ref nextSlot));
    }

    return (1, Place(root, ref nextSlot));
  }

  private static int NodeCount(IReadOnlyList<Key> keys) {
    var bytes = BcacheFsNodeBuilder.KeysOffset + keys.Sum(k => k.Bytes);
    if (bytes <= BucketBytes) return 1;

    var leaves = 0;
    bytes = BcacheFsNodeBuilder.KeysOffset;
    var any = false;
    foreach (var key in keys.OrderBy(k => k,
      Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position)))) {
      if (key.Bytes + BcacheFsNodeBuilder.KeysOffset > BucketBytes)
        throw new NotSupportedException("bcachefs key is too large for one b-tree node.");
      if (any && bytes + key.Bytes > BucketBytes) {
        ++leaves;
        bytes = BcacheFsNodeBuilder.KeysOffset;
      }
      bytes += key.Bytes;
      any = true;
    }
    if (any) ++leaves;
    return leaves + 1;
  }

  private static void PatchSuperblocks(Stream image,
      IReadOnlyList<(int Btree, int Level, Key Pointer)> roots, long btreeBuckets) {
    var deviceSectors = image.Length / SectorSize;
    long[] slots = [PrimarySbSector, PrimarySbSector + SbSlotSectors, deviceSectors - SbSlotSectors];

    image.Position = PrimarySbSector * SectorSize + 112;
    Span<byte> seqBytes = stackalloc byte[8];
    image.ReadExactly(seqBytes);
    var seq = BinaryPrimitives.ReadUInt64LittleEndian(seqBytes) + 1;

    foreach (var slot in slots) {
      var fixedPart = new byte[SbFixedBytes];
      image.Position = slot * SectorSize;
      image.ReadExactly(fixedPart);
      if (!fixedPart.AsSpan(24, 16).SequenceEqual(Magic))
        throw new InvalidDataException($"bcachefs: missing superblock copy at sector {slot}.");

      var u64s = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(124));
      var sb = new byte[SbFixedBytes + checked((int)u64s * 8)];
      image.Position = slot * SectorSize;
      image.ReadExactly(sb);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(112), seq);

      var seenRoots = 0;
      foreach (var (type, offset, length) in Sections(sb)) {
        if (type == FieldMembersV2)
          PatchMemberBitmap(sb.AsSpan(offset, length), btreeBuckets);
        if (type != FieldClean) continue;

        var cursor = offset + 24;
        var end = offset + length;
        while (cursor + 8 <= end) {
          var words = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(cursor));
          var entryType = sb[cursor + 4];
          var total = (words + 1) * 8;
          if (words == 0 || cursor + total > end) break;

          if (entryType == 1) {
            var btree = sb[cursor + 2];
            var replacement = roots.FirstOrDefault(r => r.Btree == btree);
            if (replacement.Pointer.Value != null) {
              if (replacement.Pointer.Bytes != words * 8)
                throw new InvalidDataException(
                  $"bcachefs root entry {btree} changed size unexpectedly.");
              sb[cursor + 3] = (byte)replacement.Level;
              WriteKey(sb.AsSpan(cursor + 8, replacement.Pointer.Bytes), replacement.Pointer);
              ++seenRoots;
            }
          }
          cursor += total;
        }
      }

      if (seenRoots != roots.Count)
        throw new InvalidDataException(
          $"bcachefs superblock copy at {slot} exposed {seenRoots}/{roots.Count} roots.");

      var checksum = MetadataChecksum(sb.AsSpan(16));
      BinaryPrimitives.WriteUInt64LittleEndian(sb, checksum);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(8), 0);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
      image.Position = slot * SectorSize;
      image.Write(sb);
    }
  }

  private static IEnumerable<(uint Type, int Offset, int Length)> Sections(byte[] sb) {
    var offset = SbFixedBytes;
    while (offset + 8 <= sb.Length) {
      var words = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset));
      if (words == 0) yield break;
      var length = checked((int)words * 8);
      if (offset + length > sb.Length) yield break;
      yield return (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset + 4)), offset, length);
      offset += length;
    }
  }

  private static void PatchMemberBitmap(Span<byte> section, long btreeBuckets) {
    if (section.Length < 16 + 136) return;
    var member = section[16..];
    var btreeEndSector = (FirstMetadataBucket + btreeBuckets) * BucketSectors;
    var shift = 0;
    while ((64L << shift) < btreeEndSector) ++shift;
    var bitmap = 0UL;
    for (var b = (long)FirstMetadataBucket; b < FirstMetadataBucket + btreeBuckets; ++b) {
      var first = b * BucketSectors >> shift;
      var last = ((b + 1) * BucketSectors - 1) >> shift;
      for (var bit = first; bit <= last; ++bit) bitmap |= 1UL << (int)bit;
    }
    member[28] = (byte)shift;
    BinaryPrimitives.WriteUInt64LittleEndian(member[128..], bitmap);
  }

  private static Key PatchFileSize(Key inode, ulong size, ulong sectors) {
    if (inode.Type != KeyInodeV3 || inode.Value.Length < 40)
      throw new InvalidDataException("bcachefs: expected inode_v3 for file update.");
    var value = (byte[])inode.Value.Clone();
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), sectors);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), size);
    return inode with { Value = value };
  }

  private static Key PatchDirectoryLinks(Key inode, int childDirectories) {
    if (inode.Type != KeyInodeV3 || inode.Value.Length < 48)
      return inode;

    var flags = BinaryPrimitives.ReadUInt64LittleEndian(inode.Value.AsSpan(16));
    var nrFields = (int)((flags >> 24) & 0x7F);
    var fieldsStart = (int)((flags >> 31) & 0x1F) * 8;
    if (nrFields <= 6 || fieldsStart < 48 || fieldsStart > inode.Value.Length)
      return inode;

    var slices = new List<byte[]>(nrFields);
    var cursor = fieldsStart;
    for (var i = 0; i < nrFields; ++i) {
      if (cursor >= inode.Value.Length) return inode;
      var start = cursor;
      var consumed = ReadVarint(inode.Value.AsSpan(cursor), out _);
      if (consumed <= 0) return inode;
      cursor += consumed;
      if (i < 4) {
        if (cursor >= inode.Value.Length) return inode;
        ++cursor;
      }
      slices.Add(inode.Value[start..cursor]);
    }

    Span<byte> replacement = stackalloc byte[9];
    var replacementLength = WriteVarint(replacement, (ulong)childDirectories);
    slices[6] = replacement[..replacementLength].ToArray();

    var fieldBytes = slices.Sum(s => s.Length);
    var value = new byte[fieldsStart + fieldBytes];
    inode.Value.AsSpan(0, fieldsStart).CopyTo(value);
    cursor = fieldsStart;
    foreach (var slice in slices) {
      slice.CopyTo(value, cursor);
      cursor += slice.Length;
    }
    return inode with { Value = value };
  }

  private static Key InodeKey(ulong inode, ulong parent, ulong parentOffset,
      bool isDirectory, ulong size, ulong sectors, int links, uint subvolume = 0) {
    (ulong Value, bool Wide)[] fields = [
      (0, true), (0, true), (0, true), (0, true),
      (0, false), (0, false), ((ulong)links, false), (0, false), (0, false),
      (0, false), (0, false), (0, false), (0, false), (0, false), (0, false),
      (0, false), (0, false), (0, false), (0, false),
      (parent, false), (parentOffset, false), (subvolume, false),
    ];
    var present = fields.Length;
    while (present > 0 && fields[present - 1].Value == 0) --present;

    var packed = new byte[256];
    var cursor = 0;
    for (var i = 0; i < present; ++i) {
      cursor += WriteVarint(packed.AsSpan(cursor), fields[i].Value);
      if (fields[i].Wide) packed[cursor++] = 0;
    }

    var value = new byte[48 + cursor];
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), HashSeed(inode));
    var mode = (ulong)(isDirectory ? 0x41ED : 0x81A4);
    const int inodeStrHashSiphash = 3;
    var flags = ((ulong)inodeStrHashSiphash << 20)
      | ((ulong)present << 24) | (6UL << 31) | (mode << 36);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), flags);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), sectors);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), size);
    packed.AsSpan(0, cursor).CopyTo(value.AsSpan(48));
    return new Key(KeyInodeV3, new Bpos(0, inode, SnapshotIdMax), 0, value);
  }

  private static Key DirentKey(ulong parent, ulong offset, string name, ulong target, byte type) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var length = 9 + nameBytes.Length;
    var value = new byte[(length + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt64LittleEndian(value, target);
    value[8] = type;
    nameBytes.CopyTo(value.AsSpan(9));
    return new Key(KeyDirent, new Bpos(parent, offset, SnapshotIdMax), 0, value);
  }

  private static Key AllocKey(long bucket, byte dataType, uint dirtySectors) {
    var value = new byte[48];
    value[14] = dataType;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), dirtySectors);
    return new Key(KeyAllocV4, new Bpos(0, (ulong)bucket, 0), 0, value);
  }

  private static Key BucketGensKey(long first) =>
    new(KeyBucketGens, new Bpos(0, (ulong)(first / BucketGensNr), 0), 0, new byte[BucketGensNr]);

  private static Key FreespaceKey(long first, long end) =>
    new(KeySet, new Bpos(0, (ulong)end, 0), (uint)(end - first), []);

  private static Key InodeAllocCursorKey(ulong next) {
    var value = new byte[24];
    BinaryPrimitives.WriteUInt64LittleEndian(value, FirstDynamicInode);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), long.MaxValue);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), next);
    return new Key(KeyInodeAllocCursor, new Bpos(1, 1, 0), 0, value);
  }

  private static Key AccountingKey(ReadOnlySpan<byte> position, params ulong[] counters) {
    Span<byte> s = stackalloc byte[20];
    s.Clear();
    position.CopyTo(s);
    var inode = BinaryPrimitives.ReadUInt64BigEndian(s);
    var offset = BinaryPrimitives.ReadUInt64BigEndian(s[8..]);
    var snapshot = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);
    var value = new byte[8 * counters.Length];
    for (var i = 0; i < counters.Length; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(i * 8), counters[i]);
    return new Key(KeyAccounting, new Bpos(inode, offset, snapshot), 0, value);
  }

  private static Key NrInodesKey(ulong count) => AccountingKey([AccountingNrInodes], count);

  private static Key DevDataTypeKey(byte type, ulong buckets, ulong sectors) =>
    AccountingKey([AccountingDevDataType, 0, type], buckets, sectors,
      type == DataFree ? 0 : buckets * BucketSectors - sectors);

  private static Key ReplicasKey(byte type, ulong sectors) =>
    AccountingKey([AccountingReplicas, type, 1, 1, 0], sectors);

  private static Key BtreeAccountingKey(int btree, ulong nodes) {
    Span<byte> pos = stackalloc byte[5];
    pos[0] = AccountingBtree;
    BinaryPrimitives.WriteUInt32LittleEndian(pos[1..], (uint)btree);
    return AccountingKey(pos, nodes * BucketSectors, nodes, nodes > 1 ? 1UL : 0);
  }

  private static Key SnapshotAccountingKey(uint snapshot, int btree,
      ulong keys, ulong keyBytes, ulong externalSectors) {
    Span<byte> pos = stackalloc byte[9];
    pos[0] = AccountingSnapshot;
    BinaryPrimitives.WriteUInt32LittleEndian(pos[1..], snapshot);
    BinaryPrimitives.WriteUInt32LittleEndian(pos[5..], (uint)btree);
    return AccountingKey(pos, keys, keyBytes, externalSectors);
  }

  private static Key ExtentBackpointerKey(long sector, int sectors, Bpos position) {
    var value = new byte[32];
    value[0] = (byte)BtreeExtents;
    value[2] = DataUser;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), position);
    return new Key(KeyBackpointer, new Bpos(0, (ulong)sector << ExtentBpShift, 0), 0, value);
  }

  private static Key NodeBackpointerKey(long bucket, int btree, int level, int sectors) {
    var value = new byte[32];
    value[0] = (byte)btree;
    value[1] = (byte)level;
    value[2] = DataBtree;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), Bpos.Max);
    return new Key(KeyBackpointer,
      new Bpos(0, (ulong)(bucket * BucketSectors) << ExtentBpShift, 0), 0, value);
  }

  private static void ValidateExtent(Key extent, int bucketSectors) {
    if (extent.Value.Length < 8)
      throw new InvalidDataException("bcachefs extent has no pointer value.");
    var sector = ExtentSector(extent);
    var end = sector + extent.Size;
    if (extent.Size == 0 || sector / bucketSectors != (end - 1) / bucketSectors)
      throw new NotSupportedException(
        "bcachefs in-place mutation requires one-bucket regular extents, matching the writer/defragmenter profile.");

    for (var i = 0; i + 8 <= extent.Value.Length; i += 8) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(i));
      if (!IsPointer(word)) continue;
      var generation = (byte)(word >> 56);
      var device = (byte)((word >> 48) & 0xFF);
      if (device != 0 || generation != 0)
        throw new NotSupportedException(
          "bcachefs in-place mutation currently supports the single-device generation-zero profile.");
      return;
    }
    throw new InvalidDataException("bcachefs extent contains no physical pointer.");
  }

  private static long ExtentSector(Key extent) {
    for (var i = 0; i + 8 <= extent.Value.Length; i += 8) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(i));
      if (IsPointer(word)) return PointerSector(word);
    }
    throw new InvalidDataException("bcachefs extent contains no pointer.");
  }

  private static void AddFreedRange(List<(long Offset, long Length)> ranges, Key extent) {
    var sector = ExtentSector(extent);
    ranges.Add((sector * SectorSize, (long)extent.Size * SectorSize));
  }

  private static List<Key> ReadTree(BcacheFsVolume volume, int btree) =>
    volume.Keys(btree).Select(e => new Key(e.Type, e.Position, e.Size, e.Value)).ToList();

  private static ulong HashSeed(ulong inode) => inode * 0x9E3779B97F4A7C15UL | 1UL;

  private static string ReadName(ReadOnlySpan<byte> source) {
    var end = source.Length;
    while (end > 0 && source[end - 1] == 0) --end;
    return end == 0 ? string.Empty : Encoding.UTF8.GetString(source[..end]);
  }

  private static string NormalizePath(string? path)
    => string.Join('/', (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

  private static string Parent(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? "" : path[..slash];
  }

  private static string Leaf(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? path : path[(slash + 1)..];
  }

  private static ulong NextSeq() {
    Span<byte> bytes = stackalloc byte[8];
    RandomNumberGenerator.Fill(bytes);
    var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    return value == 0 ? 1UL : value;
  }

  private static void EnsureWritable(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException(
        "bcachefs in-place mutation needs a readable, writable, seekable stream.", nameof(image));
  }

  private static void ZeroRange(Stream stream, long offset, long length) {
    if (length <= 0) return;
    var zeros = new byte[64 * 1024];
    stream.Position = offset;
    while (length > 0) {
      var chunk = (int)Math.Min(zeros.Length, length);
      stream.Write(zeros, 0, chunk);
      length -= chunk;
    }
  }
}
