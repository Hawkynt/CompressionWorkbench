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
/// <para>The metadata commit rebuilds the trees this implementation owns
/// (extents, inodes, dirents, subvolume/snapshot roots and the allocation
/// trees) but does not rewrite the image or any unaffected user-data extent. That
/// keeps mutation O(changed data + filesystem metadata), which is the distinction
/// <c>CanModify</c> is meant to expose for a filesystem.</para>
/// </remarks>
internal static class BcacheFsInPlaceModifier {

  private const int FirstMetadataBucket = BcacheFsWriter.FirstMetadataBucket;
  private const int MetadataBuckets = BcacheFsWriter.MetadataBuckets;

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
  private const ulong FirstDynamicInode = BcacheFsWriter.FirstDynamicInode;

  /// <summary>Trees a volume of this profile may carry; any other live tree is refused.</summary>
  private static readonly int[] OwnedTrees = BcacheFsMetadataCommit.TreeOrder;

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
    internal byte DirentType { get; init; } = DtReg;
    internal ArchiveEntryMetadata? Metadata { get; set; }
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
    internal required Dictionary<long, BcacheFsMetadataCommit.BucketState> BucketStates { get; init; }
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
      var metadata = input.Metadata ?? (input.InMemoryContent is null
        ? ArchiveInputInfo.FromFile(new FileInfo(input.FullPath), input.ArchiveName).Metadata
        : null);
      Func<Stream> open = input.InMemoryContent is { } bytes
        ? () => new MemoryStream(bytes, writable: false)
        : () => File.OpenRead(input.FullPath);

      if (model.Files.TryGetValue(path, out var existing)) {
        if (existing.DirentType != DtReg)
          throw new InvalidOperationException($"bcachefs: '{path}' is a symbolic link, not a file to replace.");
        foreach (var key in existing.ExistingExtents)
          AddFreedRange(model.FreedRanges, key);
        existing.Length = length;
        existing.Metadata = metadata;
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
        Metadata = metadata,
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
  /// Useful after a layout operation wants usage totals/backpointers normalized
  /// without touching payload bytes.
  /// </summary>
  internal static void NormalizeMetadata(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    Commit(image, ReadModel(image));
  }

  /// <summary>
  /// Refuses a volume this package did not write, or one written to since.
  /// </summary>
  /// <remarks>
  /// <para>Every edit here re-publishes the whole metadata generation from the keys
  /// it read, so it may only run on a volume whose every key it can read: one bset
  /// per node with the fields unpacked, nothing waiting in the journal but the
  /// shutdown entry this package leaves, metadata version 1.3. A volume
  /// <c>bcachefs format</c> or a kernel wrote packs its keys and appends bsets, and
  /// an edit that saw only part of it would publish a volume missing the rest.</para>
  ///
  /// <para>So a foreign volume is turned away with the reason rather than rewritten.</para>
  /// </remarks>
  internal static void RequireWritableProfile(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs: the volume could not be recovered: " + string.Join("; ", core.Diagnostics));

    const string profile = "in-place edits are implemented for the version 1.3 single-device volumes this package writes";
    if (core.Superblock.Version != BcacheFsFormat.Version)
      throw new NotSupportedException(
        $"bcachefs: the volume is metadata version {core.Superblock.Version >> 10}.{core.Superblock.Version & 0x3FF}; {profile}.");
    if (!core.Clean)
      throw new NotSupportedException($"bcachefs: the volume was not cleanly shut down; {profile}.");
    if (core.Members.Count != 1)
      throw new NotSupportedException($"bcachefs: the volume has {core.Members.Count} member devices; {profile}.");
    if (core.Overlay.KeyUpdates.Count != 0 || core.Journal.Sequences.Any(s => s.Sequence != CleanJournalSeq))
      throw new NotSupportedException($"bcachefs: the journal holds entries this package did not write; {profile}.");

    image.Position = 0;
    var plain = BcacheFsVolume.Open(image);
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (core.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (!tree.Complete)
        throw new InvalidDataException($"bcachefs: the {id} tree is incomplete: {string.Join("; ", tree.Diagnostics)}");
      if (tree.Nodes.Any(n => n.Sets.Count(set => set.Visible) != 1)
          || tree.MaterializedLeafSlots.Any(k => k.IsPacked)
          || plain.Keys((int)id).Count() != tree.MaterializedLeafSlots.Count)
        throw new NotSupportedException(
          $"bcachefs: the {id} tree was written by another implementation (appended or packed bsets); {profile}.");
    }
    image.Position = 0;
  }

  private static Model ReadModel(Stream image) {
    RequireWritableProfile(image);
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid)
      throw new InvalidDataException(volume.Status);
    if (volume.BucketSectorCount != BucketSectors)
      throw new NotSupportedException(
        $"bcachefs in-place mutation currently requires {BucketSectors}-sector buckets; volume uses {volume.BucketSectorCount}.");

    var unsupportedRoots = volume.Roots.Keys.Except(OwnedTrees).ToArray();
    if (unsupportedRoots.Length != 0)
      throw new NotSupportedException(
        $"bcachefs in-place mutation refuses extra live b-trees ({string.Join(",", unsupportedRoots)}); "
        + "rewriting the allocation information without owning those trees would be corrupting them.");

    var inodeKeys = ReadTree(volume, BtreeInodes)
      .Where(k => k.Type == KeyInodeV3)
      .ToDictionary(k => k.Position.Offset);
    if (!inodeKeys.ContainsKey(RootInode))
      throw new InvalidDataException("bcachefs: root inode is missing.");

    var extentKeys = ReadTree(volume, BtreeExtents);
    if (extentKeys.Any(k => k.Type != KeyExtent))
      throw new NotSupportedException(
        "bcachefs in-place mutation currently supports regular pointer extents; inline/reflink/other extent-key types are left read-only.");

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

        if (type is not (DtReg or DtLnk))
          throw new NotSupportedException(
            $"bcachefs in-place mutation supports regular files, symbolic links and directories; '{path}' has dirent type {type}.");
        if (inode.Value.Length < 40)
          throw new InvalidDataException($"bcachefs: inode {target} is shorter than inode_v3.");

        files[path] = new FileState {
          Path = path, Name = name, ParentPath = parentPath, Inode = target,
          Length = (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.Value.AsSpan(32)),
          ExistingInode = inode, ExistingDirent = dirent, DirentOffset = dirent.Position.Offset,
          DirentType = type,
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

    var totalBuckets = volume.DeviceSectors / BucketSectors;
    var generationByBucket = new Dictionary<long, byte>();
    foreach (var key in ReadTree(volume, BtreeBucketGens)) {
      if (key.Type != KeyBucketGens || key.Position.Inode != 0 || key.Value.Length != BucketGensNr)
        throw new NotSupportedException(
          "bcachefs in-place mutation requires canonical single-device bucket_gens keys.");
      var first = checked((long)key.Position.Offset * BucketGensNr);
      for (var i = 0; i < key.Value.Length && first + i < totalBuckets; ++i)
        if (key.Value[i] != 0)
          generationByBucket[first + i] = key.Value[i];
    }

    var occupied = new HashSet<long>();
    var bucketStates = new Dictionary<long, BcacheFsMetadataCommit.BucketState>();
    var allocKeys = ReadTree(volume, BtreeAlloc);
    if (allocKeys.Any(k => k.Type != KeyAllocV4))
      throw new NotSupportedException("bcachefs in-place mutation requires alloc_v4 allocation keys.");

    foreach (var key in allocKeys) {
      if (key.Position.Inode != 0 || key.Position.Offset >= (ulong)totalBuckets || key.Value.Length < 20)
        throw new InvalidDataException("bcachefs: malformed alloc_v4 key in the single-device allocation tree.");
      var bucket = (long)key.Position.Offset;
      var generation = key.Value[12];
      var oldestGeneration = key.Value[13];
      var dataType = key.Value[14];
      var indexedGeneration = generationByBucket.GetValueOrDefault(bucket);
      if (indexedGeneration != generation)
        throw new InvalidDataException(
          $"bcachefs: bucket {bucket} has alloc generation {generation} but bucket_gens says {indexedGeneration}.");
      bucketStates[bucket] = new(generation, oldestGeneration, dataType);
      if (dataType != DataFree) occupied.Add(bucket);
    }

    foreach (var (bucket, generation) in generationByBucket)
      if (generation != 0 && !bucketStates.ContainsKey(bucket))
        throw new InvalidDataException(
          $"bcachefs: bucket_gens records generation {generation} for alloc-tree hole {bucket}.");

    // Only the shape of an extent is a precondition. Where the data sits is not:
    // a defragmentation moves the bytes and then asks for the allocation to be
    // rebuilt around them, so on that path the incoming alloc tree still
    // describes the buckets the runs came from. The extent-to-bucket agreement
    // is therefore checked on what the commit writes, not on what it reads.
    foreach (var extent in extentKeys)
      ValidateExtent(extent, volume.BucketSectorCount);

    var preserved = new Dictionary<int, List<Key>> {
      [BtreeSubvolumes] = ReadTree(volume, BtreeSubvolumes),
      [BtreeSnapshots] = ReadTree(volume, BtreeSnapshots),
      [BtreeSnapshotTrees] = ReadTree(volume, BtreeSnapshotTrees),
    };

    var maxInode = inodeKeys.Keys.DefaultIfEmpty(FirstDynamicInode - 1).Max();
    return new Model {
      Volume = volume,
      Directories = directories,
      Files = files,
      PreservedTrees = preserved,
      OccupiedBuckets = occupied,
      BucketStates = bucketStates,
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
    var userBuckets = trees[BtreeExtents]
      .Select(e => BcacheFsAllocationBuilder.ExtentDataSector(e) / BucketSectors)
      .ToHashSet();
    var geometry = BcacheFsSuperblockEditor.ReadGeometry(image);

    // All live source keys were materialized before this point, so the buckets
    // the new generation lands in can be rewritten without needing any old node.
    var published = BcacheFsMetadataCommit.Publish(image, model.Volume.InternalMagic, geometry, trees,
      model.BucketStates, shapes => ChooseMetadataBuckets(shapes, geometry, userBuckets));
    BcacheFsSuperblockEditor.PublishClean(image, published);

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
    var offset = DirentHash(BcacheFsWriter.HashSeed(parent), name);
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
        var bucket = pending.Buckets[bucketIndex++];
        var firstSector = bucket * BucketSectors;
        image.Position = firstSector * SectorSize;
        image.Write(buffer, 0, sectors * SectorSize);

        var checksum = DataChecksum(buffer.AsSpan(0, sectors * SectorSize));
        var value = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32(sectors, checksum));
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8),
          ExtentPointer(firstSector, generation: model.BucketStates.GetValueOrDefault(bucket).Generation));
        file.FinalExtents.Add(new Key(KeyExtent,
          new Bpos(file.Inode, (ulong)(logicalSector + sectors), SnapshotIdMax),
          (uint)sectors, value));

        logicalSector += sectors;
        remaining -= got;
      }
    }
  }

  private static Dictionary<int, List<Key>> BuildLogicalTrees(Model model) {
    var trees = new Dictionary<int, List<Key>> {
      [BtreeExtents] = [], [BtreeInodes] = [], [BtreeDirents] = [],
    };

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
          : BcacheFsWriter.InodeKey(RootInode, 0, 0, BcacheFsWriter.ModeDirectory, 0, 0,
            childDirectoryCounts.GetValueOrDefault("", 0), RootSubvolume));
        continue;
      }

      var parentInode = model.Directories[dir.ParentPath].Inode;
      trees[BtreeInodes].Add(dir.ExistingInode is { } existing
        ? PatchDirectoryLinks(existing, childDirectoryCounts.GetValueOrDefault(dir.Path, 0))
        : BcacheFsWriter.InodeKey(dir.Inode, parentInode, dir.DirentOffset, BcacheFsWriter.ModeDirectory,
          0, 0, childDirectoryCounts.GetValueOrDefault(dir.Path, 0)));
      trees[BtreeDirents].Add(dir.ExistingDirent
        ?? BcacheFsWriter.DirentKey(parentInode, dir.DirentOffset, dir.Name, dir.Inode, DtDir));
    }

    foreach (var file in model.Files.Values) {
      var parentInode = model.Directories[file.ParentPath].Inode;
      var sectors = (ulong)((file.Length + SectorSize - 1) / SectorSize);
      trees[BtreeInodes].Add(file.ExistingInode is { } existing
        ? PatchInodeMetadata(PatchFileSize(existing, (ulong)file.Length, sectors), file.Metadata)
        : BcacheFsWriter.InodeKey(file.Inode, parentInode, file.DirentOffset, BcacheFsWriter.ModeFile,
          (ulong)file.Length, sectors, 0, metadata: file.Metadata));
      trees[BtreeDirents].Add(file.ExistingDirent
        ?? BcacheFsWriter.DirentKey(parentInode, file.DirentOffset, file.Name, file.Inode, DtReg));
    }

    trees[BtreeSubvolumes] = [.. model.PreservedTrees[BtreeSubvolumes]];
    trees[BtreeSnapshots] = [.. model.PreservedTrees[BtreeSnapshots]];
    trees[BtreeSnapshotTrees] = [.. model.PreservedTrees[BtreeSnapshotTrees]];

    foreach (var list in trees.Values)
      list.Sort((a, b) => Compare(a.Position, b.Position));
    return trees;
  }

  /// <summary>The buckets one commit's metadata generation is written into.</summary>
  /// <remarks>
  /// Taken in order from the first bucket after the journal, skipping whatever
  /// holds file data. A placement pass is free to put extents anywhere, so the
  /// metadata has to be the thing that moves out of the way.
  /// </remarks>
  private static IReadOnlyDictionary<int, long[]> ChooseMetadataBuckets(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes,
      BcacheFsAllocationBuilder.Geometry geometry,
      IReadOnlySet<long> userBuckets) {
    var count = shapes.Values.Sum(s => s.Count);
    if (count > MetadataBuckets)
      throw new NotSupportedException(
        $"bcachefs metadata needs {count} buckets; an in-place commit budgets {MetadataBuckets}.");

    var lastSbBucket = (geometry.DeviceSectors - SbSlotSectors) / BucketSectors;
    var free = new Queue<long>();
    for (var bucket = (long)FirstMetadataBucket; free.Count < count && bucket < lastSbBucket; ++bucket)
      if (!userBuckets.Contains(bucket))
        free.Enqueue(bucket);
    if (free.Count < count)
      throw new NotSupportedException(
        $"bcachefs metadata needs {count} free buckets; only {free.Count} are available.");

    var result = new Dictionary<int, long[]>();
    foreach (var id in BcacheFsMetadataCommit.TreeOrder)
      if (shapes.TryGetValue(id, out var treeShapes))
        result[id] = [.. Enumerable.Range(0, treeShapes.Count).Select(_ => free.Dequeue())];
    return result;
  }

  private static Key PatchFileSize(Key inode, ulong size, ulong sectors) {
    if (inode.Type != KeyInodeV3 || inode.Value.Length < 40)
      throw new InvalidDataException("bcachefs: expected inode_v3 for file update.");
    var value = (byte[])inode.Value.Clone();
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), sectors);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), size);
    return inode with { Value = value };
  }

  private static Key PatchInodeMetadata(Key inode, ArchiveEntryMetadata? metadata) {
    if (metadata == null || inode.Type != KeyInodeV3 || inode.Value.Length < 48)
      return inode;

    var value = inode.Value;
    var flags = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(16));
    var fieldCount = (int)((flags >> 24) & 0x7F);
    var fieldsStart = (int)((flags >> 31) & 0x1F) * 8;
    if (fieldsStart < 48 || fieldsStart > value.Length)
      throw new InvalidDataException("bcachefs: inode metadata field list lies outside its key.");

    var fields = new List<(ulong Low, ulong High)>(Math.Max(fieldCount, 6));
    var cursor = fieldsStart;
    for (var index = 0; index < fieldCount; ++index) {
      var lowBytes = ReadVarint(value.AsSpan(cursor), out var low);
      if (lowBytes <= 0 || cursor + lowBytes > value.Length)
        throw new InvalidDataException("bcachefs: truncated packed inode metadata field.");
      cursor += lowBytes;
      ulong high = 0;
      if (index < 4) {
        var highBytes = ReadVarint(value.AsSpan(cursor), out high);
        if (highBytes <= 0 || cursor + highBytes > value.Length)
          throw new InvalidDataException("bcachefs: truncated wide packed inode metadata field.");
        cursor += highBytes;
      }
      fields.Add((low, high));
    }

    while (fields.Count < 6) fields.Add((0, 0));
    if (metadata.LastAccessTimeUtc is { } atime)
      fields[0] = (BcacheFsWriter.ToBcacheTime(atime), 0);
    if (metadata.StatusChangeTimeUtc is { } ctime)
      fields[1] = (BcacheFsWriter.ToBcacheTime(ctime), 0);
    if (metadata.LastWriteTimeUtc is { } mtime) {
      fields[2] = (BcacheFsWriter.ToBcacheTime(mtime), 0);
    }
    if (metadata.CreationTimeUtc is { } otime)
      fields[3] = (BcacheFsWriter.ToBcacheTime(otime), 0);
    if (metadata.UnixUserId is { } uid) fields[4] = (uid, 0);
    if (metadata.UnixGroupId is { } gid) fields[5] = (gid, 0);

    var present = fields.Count;
    while (present > 0) {
      var last = fields[present - 1];
      if (last.Low != 0 || last.High != 0) break;
      --present;
    }
    var packed = new byte[Math.Max(1, present * 18)];
    cursor = 0;
    for (var index = 0; index < present; ++index) {
      cursor += WriteVarint(packed.AsSpan(cursor), fields[index].Low);
      if (index < 4) cursor += WriteVarint(packed.AsSpan(cursor), fields[index].High);
    }

    var updated = new byte[fieldsStart + cursor];
    value.AsSpan(0, fieldsStart).CopyTo(updated);
    packed.AsSpan(0, cursor).CopyTo(updated.AsSpan(fieldsStart));
    flags = (flags & ~(0x7FUL << 24)) | ((ulong)present << 24);
    if (metadata.UnixMode is { } permissions) {
      var inodeKind = (flags >> 36) & 0xF000;
      var mode = inodeKind | ((ulong)permissions & 0x1FF);
      flags = (flags & ~(0xFFFFUL << 36)) | (mode << 36);
    }
    BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(16), flags);
    return inode with { Value = updated };
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
      var device = (byte)((word >> 48) & 0xFF);
      if (device != 0)
        throw new NotSupportedException(
          "bcachefs in-place mutation currently supports the single-device pointer profile.");
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
