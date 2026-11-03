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
    internal required Dictionary<ulong, List<Key>> Xattrs { get; init; }
    internal required List<Key> OtherInodeKeys { get; init; }
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
  /// The newest metadata version whose trees an edit here re-publishes.
  /// </summary>
  /// <remarks>
  /// 1.4 (<c>member_seq</c>) is what a 6.8 kernel upgrades a 1.3 volume to the
  /// moment it mounts it, and it adds a field to the member, nothing to any tree.
  /// Anything newer (the accounting tree of 1.9, generation-carrying backpointers of
  /// 1.14, ...) holds facts this package does not rebuild.
  /// </remarks>
  internal const ushort MaxEditableVersion = (1 << 10) | 4;

  /// <summary>Trees derived from what occupies the device and rebuilt by every commit.</summary>
  private static readonly int[] RebuiltTrees = [
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeNeedDiscard, BtreeLru, BtreeBackpointers,
  ];

  /// <summary>Trees carried key by key.</summary>
  private static readonly int[] CarriedTrees = [
    BtreeExtents, BtreeInodes, BtreeDirents, BtreeXattrs, BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees,
  ];

  /// <summary>Key types that mark a slot as empty rather than holding anything.</summary>
  private static bool IsEmptySlot(byte type) => type is KeyDeleted or KeyWhiteout
    or (byte)BcacheFsKeyType.HashWhiteout or (byte)BcacheFsKeyType.ExtentWhiteout;

  /// <summary>
  /// Refuses a volume an edit here could not re-publish whole, and otherwise
  /// returns every live key of every tree, as the volume is after recovery.
  /// </summary>
  /// <remarks>
  /// <para>Every edit re-publishes the whole metadata generation from the keys it
  /// read, so it may only run on a volume whose every key it can read and whose
  /// every tree it either carries or rebuilds. The keys are taken from the recovered
  /// view - every visible bset of every node, packed or not, with the journal
  /// replayed - so a volume the kernel has written into is read as the kernel left
  /// it, not as this package last wrote it.</para>
  ///
  /// <para>What is refused, with the reason: a volume not cleanly shut down, of
  /// more than one member, newer than <see cref="MaxEditableVersion" />, laid out
  /// other than this package's writer lays a device out (bucket size and journal),
  /// or holding keys in a tree it neither carries nor rebuilds.</para>
  /// </remarks>
  internal static Dictionary<int, List<Key>> RequireWritableProfile(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs: the volume could not be recovered: " + string.Join("; ", core.Diagnostics));

    const string profile = "in-place edits are implemented for single-device volumes laid out by this package's writer, "
      + "at metadata version 1.3 or 1.4";
    var version = core.Superblock.Version;
    if (version < BcacheFsFormat.Version || version > MaxEditableVersion)
      throw new NotSupportedException(
        $"bcachefs: the volume is metadata version {version >> 10}.{version & 0x3FF}; {profile}.");
    if (!core.Clean)
      throw new NotSupportedException($"bcachefs: the volume was not cleanly shut down; {profile}.");
    if (core.Members.Count != 1)
      throw new NotSupportedException($"bcachefs: the volume has {core.Members.Count} member devices; {profile}.");
    if (core.Members[0].BucketSizeSectors != BucketSectors)
      throw new NotSupportedException(
        $"bcachefs: the volume's buckets are {core.Members[0].BucketSizeSectors} sectors, not {BucketSectors}; {profile}.");

    image.Position = 0;
    var geometry = BcacheFsSuperblockEditor.ReadGeometry(image);
    if (geometry.JournalBuckets.Count != 1
        || geometry.JournalBuckets[0] != (BcacheFsWriter.JournalFirstBucket, BcacheFsWriter.JournalBuckets))
      throw new NotSupportedException($"bcachefs: the journal is not where this package's writer puts it; {profile}.");

    var trees = new Dictionary<int, List<Key>>();
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (core.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (!tree.Complete)
        throw new InvalidDataException($"bcachefs: the {id} tree is incomplete: {string.Join("; ", tree.Diagnostics)}");
      var keys = tree.MaterializedLeafSlots
        .Where(k => !IsEmptySlot(k.RawType))
        .Select(k => new Key(k.RawType, k.Position, k.Size, k.Value))
        .ToList();
      if (keys.Count == 0) continue;
      if (!CarriedTrees.Contains((int)id) && !RebuiltTrees.Contains((int)id))
        throw new NotSupportedException(
          $"bcachefs: the {id} tree holds {keys.Count} keys, which an edit here neither carries nor rebuilds; {profile}.");
      trees[(int)id] = keys;
    }
    image.Position = 0;
    return trees;
  }

  private static Model ReadModel(Stream image) {
    var trees = RequireWritableProfile(image);
    image.Position = 0;
    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid)
      throw new InvalidDataException(volume.Status);

    List<Key> ReadTree(int btree) => trees.TryGetValue(btree, out var keys) ? [.. keys] : [];

    var inodeTree = ReadTree(BtreeInodes);
    if (inodeTree.Any(k => k.Type is (byte)BcacheFsKeyType.Inode or (byte)BcacheFsKeyType.InodeV2))
      throw new NotSupportedException("bcachefs in-place mutation reads inode_v3 inodes, not the older encodings.");
    var inodeKeys = inodeTree
      .Where(k => k.Type == KeyInodeV3)
      .ToDictionary(k => k.Position.Offset);
    // Anything else the inodes tree holds - the generation an emptied inode
    // number keeps - names no file and is carried as it is.
    var otherInodeKeys = inodeTree.Where(k => k.Type != KeyInodeV3).ToList();
    if (!inodeKeys.ContainsKey(RootInode))
      throw new InvalidDataException("bcachefs: root inode is missing.");

    var extentKeys = ReadTree(BtreeExtents);
    var foreignExtent = extentKeys.FirstOrDefault(k => k.Type is not (KeyExtent or KeyInlineData or KeyReservation));
    if (foreignExtent.Value != null)
      throw new NotSupportedException(
        $"bcachefs in-place mutation carries pointer extents, inline data and reservations; key type {foreignExtent.Type} "
        + "(reflink or another indirect extent) is left read-only.");

    var dirents = ReadTree(BtreeDirents)
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

    var reachable = directories.Values.Select(d => d.Inode).Concat(files.Values.Select(f => f.Inode)).ToHashSet();
    var orphan = inodeKeys.Keys.FirstOrDefault(i => !reachable.Contains(i));
    if (orphan != 0)
      throw new NotSupportedException(
        $"bcachefs: inode {orphan} is not reachable from the root (unlinked but still open?); an edit here would drop it.");

    var fileByInode = files.Values.ToDictionary(f => f.Inode);
    foreach (var extent in extentKeys) {
      if (!fileByInode.TryGetValue(extent.Position.Inode, out var file))
        throw new NotSupportedException(
          $"bcachefs extent for inode {extent.Position.Inode} is not a reachable regular file.");
      file.ExistingExtents.Add(extent);
      file.FinalExtents.Add(extent);
    }

    var xattrs = new Dictionary<ulong, List<Key>>();
    foreach (var key in ReadTree(BtreeXattrs)) {
      if (key.Type != KeyXattr)
        throw new NotSupportedException($"bcachefs: the xattrs tree holds key type {key.Type}.");
      if (!reachable.Contains(key.Position.Inode))
        throw new NotSupportedException($"bcachefs: an xattr belongs to inode {key.Position.Inode}, which is not reachable.");
      if (!xattrs.TryGetValue(key.Position.Inode, out var list))
        xattrs[key.Position.Inode] = list = [];
      list.Add(key);
    }
    foreach (var file in files.Values)
      file.ExistingExtents.Sort((a, b) => Compare(a.Position, b.Position));

    var totalBuckets = volume.DeviceSectors / BucketSectors;
    var generationByBucket = new Dictionary<long, byte>();
    foreach (var key in ReadTree(BtreeBucketGens)) {
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
    var allocKeys = ReadTree(BtreeAlloc);
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
      [BtreeSubvolumes] = ReadTree(BtreeSubvolumes),
      [BtreeSnapshots] = ReadTree(BtreeSnapshots),
      [BtreeSnapshotTrees] = ReadTree(BtreeSnapshotTrees),
    };

    var maxInode = inodeKeys.Keys.DefaultIfEmpty(FirstDynamicInode - 1).Max();
    return new Model {
      Volume = volume,
      Directories = directories,
      Files = files,
      PreservedTrees = preserved,
      Xattrs = xattrs,
      OtherInodeKeys = otherInodeKeys,
      OccupiedBuckets = occupied,
      BucketStates = bucketStates,
      FreedRanges = [],
      NextInode = Math.Max(FirstDynamicInode, maxInode + 1),
    };
  }

  /// <summary>
  /// Whether the volume is in the shape this package's writer leaves one in: every
  /// node one unpacked bset, nothing in the journal but the shutdown entry, and
  /// every pointer extent alone in a bucket past the metadata reservation, starting
  /// at the bucket's first sector and uncompressed, its checksummed region exactly
  /// its live data.
  /// </summary>
  internal static bool IsCanonical(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable || core.Overlay.KeyUpdates.Count != 0
        || core.Journal.Sequences.Any(s => s.Sequence != CleanJournalSeq))
      return false;
    image.Position = 0;
    var plain = BcacheFsVolume.Open(image);
    var extents = new List<Key>();
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (core.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (tree.Nodes.Any(n => n.Sets.Count(set => set.Visible) != 1)
          || tree.MaterializedLeafSlots.Any(k => k.IsPacked)
          || plain.Keys((int)id).Count() != tree.MaterializedLeafSlots.Count)
        return false;
      if (id == BcacheFsBtreeId.Extents)
        extents.AddRange(tree.MaterializedLeafSlots.Select(k => new Key(k.RawType, k.Position, k.Size, k.Value)));
    }
    image.Position = 0;
    return NonCanonicalExtents(extents).Count == 0;
  }

  /// <summary>The pointer extents that are not alone, aligned and plain in a bucket of their own.</summary>
  private static HashSet<Bpos> NonCanonicalExtents(IEnumerable<Key> extents) {
    var placed = extents
      .Select(e => (Key: e, At: BcacheFsAllocationBuilder.Footprint(e)))
      .Where(x => x.At.HasValue)
      .Select(x => (x.Key, At: x.At!.Value))
      .ToList();
    var perBucket = placed.GroupBy(x => x.At.Sector / BucketSectors).ToDictionary(g => g.Key, g => g.Count());
    return placed
      .Where(x => x.At.Sector % BucketSectors != 0
        || IsMetadataReservation(x.At.Sector / BucketSectors)
        || perBucket[x.At.Sector / BucketSectors] != 1
        || x.At.RegionOffset != 0
        || x.At.RegionSectors != x.Key.Size
        || x.At.Compressed
        || x.At.Encrypted)
      .Select(x => x.Key.Position)
      .ToHashSet();
  }

  /// <summary>
  /// Rewrites the volume into the shape <see cref="IsCanonical" /> describes: every
  /// tree re-published, and every extent not already alone at the start of a bucket
  /// copied into a fresh one with a new checksum over exactly its live data.
  /// </summary>
  /// <remarks>
  /// Holes stay holes and inline data stays inline. A compressed or encrypted
  /// extent is refused rather than rewritten: moving it would mean decoding it.
  /// </remarks>
  internal static void Canonicalize(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    var model = ReadModel(image);
    var moving = NonCanonicalExtents(model.Files.Values.SelectMany(f => f.FinalExtents));
    // Refused before a byte is written, so a refusal leaves the volume as it was.
    foreach (var file in model.Files.Values)
      foreach (var key in file.FinalExtents)
        if (moving.Contains(key.Position) && BcacheFsAllocationBuilder.Footprint(key) is { } coded
            && (coded.Compressed || coded.Encrypted))
          throw new NotSupportedException(
            $"bcachefs: '{file.Path}' has a compressed or encrypted extent, which an edit here does not move.");
    if (moving.Count > FreeDataBuckets(model).Count)
      throw new IOException("bcachefs: not enough free buckets to bring the volume into the writer's layout.");

    var free = FreeDataBuckets(model);
    var buffer = new byte[BucketBytes];

    foreach (var file in model.Files.Values) {
      for (var i = 0; i < file.FinalExtents.Count; ++i) {
        var key = file.FinalExtents[i];
        if (!moving.Contains(key.Position)) continue;
        var at = BcacheFsAllocationBuilder.Footprint(key)!.Value;
        if (at.Compressed || at.Encrypted)
          throw new NotSupportedException(
            $"bcachefs: '{file.Path}' has a compressed or encrypted extent, which an edit here does not move.");
        if (free.Count == 0)
          throw new IOException("bcachefs: not enough free buckets to bring the volume into the writer's layout.");

        var bytes = (int)key.Size * SectorSize;
        image.Position = (at.Sector + at.RegionOffset) * SectorSize;
        image.ReadExactly(buffer, 0, bytes);
        var bucket = free.Dequeue();
        model.OccupiedBuckets.Add(bucket);
        image.Position = bucket * (long)BucketBytes;
        image.Write(buffer, 0, bytes);

        var value = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32((int)key.Size, DataChecksum(buffer.AsSpan(0, bytes))));
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8),
          ExtentPointer(bucket * BucketSectors, generation: model.BucketStates.GetValueOrDefault(bucket).Generation));
        file.FinalExtents[i] = key with { Value = value };
        AddFreedRange(model.FreedRanges, key);
      }
    }

    Commit(image, model);
  }

  /// <summary>
  /// Free buckets past the metadata reservation and before the tail superblock,
  /// those still at generation zero first.
  /// </summary>
  /// <remarks>
  /// A 1.3 backpointer has no field for the generation of the bucket it points
  /// into, so a newer bcachefs upgrading the volume discards every backpointer into
  /// a bucket whose generation is not zero and rebuilds them; data laid into
  /// never-reused buckets leaves it nothing to rebuild.
  /// </remarks>
  private static Queue<long> FreeDataBuckets(Model model) {
    var firstDataBucket = (long)FirstMetadataBucket + MetadataBuckets;
    var firstTailSbBucket = (model.Volume.DeviceSectors - SbSlotSectors) / BucketSectors;
    var candidates = new List<long>();
    for (var bucket = firstDataBucket; bucket < firstTailSbBucket; ++bucket)
      if (!model.OccupiedBuckets.Contains(bucket)) candidates.Add(bucket);
    return new Queue<long>(candidates
      .OrderBy(b => model.BucketStates.GetValueOrDefault(b).Generation != 0 ? 1 : 0)
      .ThenBy(b => b));
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
      .Select(BcacheFsAllocationBuilder.Footprint)
      .Where(f => f.HasValue)
      .Select(f => f!.Value.Sector / BucketSectors)
      .ToHashSet();
    var geometry = BcacheFsSuperblockEditor.ReadGeometry(image);

    // All live source keys were materialized before this point, so the buckets
    // the new generation lands in can be rewritten without needing any old node.
    var published = BcacheFsMetadataCommit.Publish(image, model.Volume.InternalMagic, geometry, trees,
      model.BucketStates, shapes => ChooseMetadataBuckets(shapes, geometry, userBuckets, model.BucketStates));
    BcacheFsSuperblockEditor.PublishClean(image, published);

    // Deleted/replaced user data becomes forensic free space only after the new
    // roots are live. New payload was deliberately allocated outside these runs;
    // the new metadata generation may have landed in a bucket they emptied, and a
    // surviving key may share a region with a removed one, so both are spared.
    var spared = trees[BtreeExtents]
      .Select(BcacheFsAllocationBuilder.Footprint)
      .Where(f => f.HasValue)
      .Select(f => (Offset: f!.Value.Sector * SectorSize, Length: (long)f.Value.RegionSectors * SectorSize))
      .Concat(published.MetadataBuckets.Select(b => (Offset: b * (long)BucketBytes, Length: (long)BucketBytes)))
      .ToList();
    foreach (var (offset, length) in model.FreedRanges)
      foreach (var (from, to) in Subtract(offset, offset + length, spared))
        ZeroRange(image, from, to - from);

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
    var free = FreeDataBuckets(model);

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
      [BtreeExtents] = [], [BtreeInodes] = [.. model.OtherInodeKeys], [BtreeDirents] = [], [BtreeXattrs] = [],
    };

    // Attributes go with their inode: kept while it survives, a replaced file's
    // included, and dropped with it.
    foreach (var inode in model.Directories.Values.Select(d => d.Inode).Concat(model.Files.Values.Select(f => f.Inode)))
      if (model.Xattrs.TryGetValue(inode, out var attributes))
        trees[BtreeXattrs].AddRange(attributes);

    // Extents: unchanged files retain their exact keys/pointers/checksums; only
    // new/replaced files contribute freshly written keys.
    foreach (var file in model.Files.Values)
      trees[BtreeExtents].AddRange(file.FinalExtents);

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
      // A file whose bytes are untouched keeps its inode as it is: its sector count
      // is what its extents hold, which for a sparse file or inline data is not its
      // length rounded up.
      trees[BtreeInodes].Add(file.ExistingInode is { } existing
        ? file.Pending == null ? existing : PatchInodeMetadata(PatchFileSize(existing, (ulong)file.Length, sectors), file.Metadata)
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
      IReadOnlySet<long> userBuckets,
      IReadOnlyDictionary<long, BcacheFsMetadataCommit.BucketState> states) {
    var count = shapes.Values.Sum(s => s.Count);
    if (count > MetadataBuckets)
      throw new NotSupportedException(
        $"bcachefs metadata needs {count} buckets; an in-place commit budgets {MetadataBuckets}.");

    var lastSbBucket = (geometry.DeviceSectors - SbSlotSectors) / BucketSectors;
    // The reservation first, and within it the buckets still at generation zero:
    // a backpointer into a reused bucket is one a newer bcachefs throws away and
    // rebuilds when it upgrades the volume (see FreeDataBuckets).
    var candidates = new List<long>();
    for (var bucket = (long)FirstMetadataBucket; bucket < lastSbBucket; ++bucket) {
      if (userBuckets.Contains(bucket)) continue;
      candidates.Add(bucket);
      if (!IsMetadataReservation(bucket) && candidates.Count >= count + MetadataBuckets) break;
    }
    var free = new Queue<long>(candidates
      .OrderBy(b => IsMetadataReservation(b) ? 0 : 1)
      .ThenBy(b => states.GetValueOrDefault(b).Generation != 0 ? 1 : 0)
      .ThenBy(b => b)
      .Take(count));
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

  /// <summary>
  /// An extent's checksummed region must lie inside one bucket of device 0.
  /// </summary>
  private static void ValidateExtent(Key extent, int bucketSectors) {
    if (extent.Type != KeyExtent) return;
    if (BcacheFsAllocationBuilder.Footprint(extent) is not { } at)
      throw new InvalidDataException($"bcachefs extent at inode {extent.Position.Inode} carries no dirty pointer.");
    if (extent.Size == 0 || at.Sector / bucketSectors != (at.Sector + Math.Max(at.RegionSectors, at.DiskSectors) - 1) / bucketSectors)
      throw new NotSupportedException(
        $"bcachefs extent at inode {extent.Position.Inode} crosses a bucket boundary, which no writer produces.");
    for (var i = 0; i + 8 <= extent.Value.Length; i += 8) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(i));
      if (!IsPointer(word) || PointerSector(word) != at.Sector) continue;
      if (((word >> 48) & 0xFF) != 0)
        throw new NotSupportedException(
          "bcachefs in-place mutation currently supports the single-device pointer profile.");
      return;
    }
  }

  /// <summary>
  /// Records the device sectors a removed extent's data occupied: its live part,
  /// or a compressed region whole. Inline data and reservations occupy none.
  /// </summary>
  private static void AddFreedRange(List<(long Offset, long Length)> ranges, Key extent) {
    if (BcacheFsAllocationBuilder.Footprint(extent) is not { } at) return;
    ranges.Add(at.Compressed
      ? (at.Sector * SectorSize, (long)at.RegionSectors * SectorSize)
      : ((at.Sector + at.RegionOffset) * SectorSize, (long)extent.Size * SectorSize));
  }

  /// <summary>The parts of [from, to) no spared range overlaps.</summary>
  private static IEnumerable<(long From, long To)> Subtract(long from, long to, IReadOnlyList<(long Offset, long Length)> spared) {
    var cursor = from;
    foreach (var (offset, length) in spared.Where(r => r.Offset < to && r.Offset + r.Length > from).OrderBy(r => r.Offset)) {
      if (offset > cursor) yield return (cursor, offset);
      cursor = Math.Max(cursor, offset + length);
      if (cursor >= to) yield break;
    }
    if (cursor < to) yield return (cursor, to);
  }

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
