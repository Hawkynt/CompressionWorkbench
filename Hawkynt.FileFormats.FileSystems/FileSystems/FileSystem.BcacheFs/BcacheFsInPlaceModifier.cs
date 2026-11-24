#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Genuine in-place add/replace/remove for bcachefs volumes of metadata version 1.3 and 1.4:
/// those <see cref="BcacheFsWriter"/> emits, those <c>bcachefs format</c> lays out, on one
/// member device or several, with the volume's own metadata and data replicas.
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

    /// <summary>Per bucket-sized piece, the bucket holding each copy, by <see cref="BcacheFsBucketId" />.</summary>
    internal List<long[]> Buckets { get; } = [];
  }

  private sealed class Model {
    internal required BcacheFsDevices Devices { get; init; }
    internal required ulong Magic { get; init; }
    internal required Dictionary<string, DirectoryState> Directories { get; init; }
    internal required Dictionary<string, FileState> Files { get; init; }
    internal required Dictionary<int, List<Key>> PreservedTrees { get; init; }
    internal required Dictionary<ulong, List<Key>> Xattrs { get; init; }
    internal required List<Key> OtherInodeKeys { get; init; }
    /// <summary>The reflink tree's indirect extents, refcounts as this edit leaves them.</summary>
    internal required List<Key> Reflink { get; init; }
    internal required HashSet<long> OccupiedBuckets { get; init; }
    internal required Dictionary<long, BcacheFsMetadataCommit.BucketState> BucketStates { get; init; }
    internal required List<(byte Device, long Offset, long Length)> FreedRanges { get; init; }
    internal required ulong NextInode { get; set; }
  }

  internal static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    Add([image], inputs);
  }

  /// <summary>Adds or replaces files in a volume on the member devices <paramref name="devices" />, every one of them, in any order.</summary>
  internal static void Add(IReadOnlyList<Stream> devices, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(devices);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var device in devices) EnsureWritable(device);
    using var buckets = UseVolumeBuckets(devices);

    var model = ReadModel(devices);
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
        DropReflinks(model, existing.ExistingExtents);
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

    Commit(model);
  }

  internal static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    Remove([image], entryNames);
  }

  /// <summary>Removes files and directories from a volume on the member devices <paramref name="devices" />.</summary>
  internal static void Remove(IReadOnlyList<Stream> devices, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(devices);
    foreach (var device in devices) EnsureWritable(device);
    using var buckets = UseVolumeBuckets(devices);
    entryNames ??= [];

    var model = ReadModel(devices);
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
      DropReflinks(model, file.ExistingExtents);
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

    Commit(model);
  }

  /// <summary>
  /// Rewrites only the metadata trees, preserving every current file extent.
  /// Useful after a layout operation wants usage totals/backpointers normalized
  /// without touching payload bytes.
  /// </summary>
  internal static void NormalizeMetadata(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureWritable(image);
    using var buckets = UseVolumeBuckets(image);
    Commit(ReadModel([image]));
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
    BtreeExtents, BtreeInodes, BtreeDirents, BtreeXattrs, BtreeReflink, BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees,
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
  /// more than one member, newer than <see cref="MaxEditableVersion" />, with buckets
  /// other than those <see cref="UseVolumeBuckets(IReadOnlyList{Stream})" /> put in effect, or holding keys
  /// in a tree it neither carries nor rebuilds.</para>
  /// </remarks>

  /// <summary>
  /// Works in the bucket size of the volume <paramref name="image" /> holds for as long as the
  /// result is not disposed: an edit of a volume <c>bcachefs format</c> made writes its nodes,
  /// allocation keys and data in that volume's buckets, and its nodes in that volume's node size,
  /// several to a metadata bucket where a node is smaller than a bucket.
  /// </summary>
  internal static IDisposable UseVolumeBuckets(Stream image) => UseVolumeBuckets([image]);

  /// <inheritdoc cref="UseVolumeBuckets(Stream)" />
  /// <remarks>Every member has to have the same bucket size; a volume whose members differ is refused.</remarks>
  internal static IDisposable UseVolumeBuckets(IReadOnlyList<Stream> devices) {
    foreach (var device in devices) device.Position = 0;
    var core = BcacheFsCoreVolume.Open(devices);
    foreach (var device in devices) device.Position = 0;
    var bucket = core.Members[0].BucketSizeSectors;
    if (core.Members.Any(m => m.BucketSizeSectors != bucket))
      throw new NotSupportedException(
        $"bcachefs: the members' buckets differ ({string.Join(", ", core.Members.Select(m => m.BucketSizeSectors))} sectors); "
        + "an edit here works in one bucket size.");
    var node = core.Superblock.BtreeNodeSectors;
    var raw = core.Superblock.RawBytes;
    var timeBase = (Int128)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(128))
      | ((Int128)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(136)) << 64);
    var precision = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(140));
    var replicas = EffectiveReplicas(core, DataBtree);
    return BcacheFsBucketGeometry.Use(bucket, core.Superblock.BlockSizeSectors, timeBase, precision, node, replicas);
  }

  /// <summary>
  /// How many copies of <paramref name="dataType" /> a commit writes: what the volume asks for
  /// (<c>metadata_replicas</c> or <c>data_replicas</c>), at most one per supplied member allowed
  /// to hold that type.
  /// </summary>
  private static int EffectiveReplicas(BcacheFsCoreVolume core, byte dataType) {
    var flags0 = core.Superblock.Flags.Length > 0 ? core.Superblock.Flags[0] : 0UL;
    var wanted = Math.Max(1, (int)((flags0 >> (dataType == DataBtree ? 48 : 52)) & 0xF));
    var allowed = core.Devices.Keys.Count(d => d < core.Members.Count && (core.Members[d].DataAllowed & (1 << dataType)) != 0);
    return Math.Max(1, Math.Min(wanted, allowed));
  }

  /// <summary>The members, geometry and effective replicas a commit on <paramref name="core" /> writes with.</summary>
  private static BcacheFsDevices DevicesOf(BcacheFsCoreVolume core) {
    var devices = BcacheFsDevices.Of(core);
    return new BcacheFsDevices {
      Streams = devices.Streams,
      Geometry = devices.Geometry,
      DataAllowed = devices.DataAllowed,
      MetadataReplicas = EffectiveReplicas(core, DataBtree),
      DataReplicas = EffectiveReplicas(core, DataUser),
    };
  }

  internal static Dictionary<int, List<Key>> RequireWritableProfile(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    return RequireWritableProfile([image]);
  }

  /// <inheritdoc cref="RequireWritableProfile(Stream)" />
  internal static Dictionary<int, List<Key>> RequireWritableProfile(IReadOnlyList<Stream> devices) {
    ArgumentNullException.ThrowIfNull(devices);
    foreach (var device in devices) device.Position = 0;
    var core = BcacheFsCoreVolume.Open(devices);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs: the volume could not be recovered: " + string.Join("; ", core.Diagnostics));

    const string profile = "in-place edits are implemented for volumes at metadata version 1.3 or 1.4, with every member device supplied";
    var version = core.Superblock.Version;
    if (version < BcacheFsFormat.Version || version > MaxEditableVersion)
      throw new NotSupportedException(
        $"bcachefs: the volume is metadata version {version >> 10}.{version & 0x3FF}; {profile}.");
    if (!core.Clean)
      throw new NotSupportedException($"bcachefs: the volume was not cleanly shut down; {profile}.");
    var missing = Enumerable.Range(0, core.Members.Count).Where(i => !core.Devices.ContainsKey((byte)i)).ToArray();
    if (missing.Length != 0 || core.Devices.Count != core.Members.Count)
      throw new NotSupportedException(
        $"bcachefs: the volume has {core.Members.Count} member devices and {core.Devices.Count} were supplied"
        + (missing.Length == 0 ? "" : $" (member {string.Join(", ", missing)} missing)") + $"; {profile}.");
    for (var i = 0; i < core.Members.Count; ++i) {
      var member = core.Members[i];
      if (member.BucketSizeSectors != BucketSectors)
        throw new NotSupportedException(
          $"bcachefs: member {i}'s buckets are {member.BucketSizeSectors} sectors, not {BucketSectors}; {profile}.");
      if (member.State != BcacheFsMemberState.ReadWrite)
        throw new NotSupportedException($"bcachefs: member {i} is {member.State}, not read-write; {profile}.");
      // BCH_MEMBER_DURABILITY is stored plus one; zero means the default, one copy.
      var durability = member.Durability == 0 ? 1 : member.Durability - 1;
      if (durability != 1)
        throw new NotSupportedException($"bcachefs: member {i} counts as {durability} copies; {profile}.");
    }

    var journaled = false;
    foreach (var device in devices) {
      device.Position = 0;
      journaled |= BcacheFsSuperblockEditor.ReadGeometry(device).JournalBuckets.Count != 0;
    }
    if (!journaled)
      throw new InvalidDataException("bcachefs: the superblock names no journal buckets.");

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
    foreach (var device in devices) device.Position = 0;
    return trees;
  }

  private static Model ReadModel(IReadOnlyList<Stream> streams) {
    var trees = RequireWritableProfile(streams);
    foreach (var stream in streams) stream.Position = 0;
    var core = BcacheFsCoreVolume.Open(streams);
    var devices = DevicesOf(core);
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(core.Superblock.RawBytes.AsSpan(40));

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
    var foreignExtent = extentKeys.FirstOrDefault(k => k.Type is not (KeyExtent or KeyInlineData or KeyReservation or KeyReflinkP));
    if (foreignExtent.Value != null)
      throw new NotSupportedException(
        $"bcachefs in-place mutation carries pointer extents, inline data, reservations and reflinks; key type {foreignExtent.Type} "
        + "is left read-only.");
    var reflink = ReadTree(BtreeReflink);
    RequireConsistentReflinks(extentKeys, reflink);

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

    // Allocation keys sit at (member, bucket); a bucket is named across members by BcacheFsBucketId.
    long BucketsOf(ulong device) => device <= byte.MaxValue && devices.Geometry.TryGetValue((byte)device, out var g)
      ? g.Buckets
      : throw new InvalidDataException($"bcachefs: an allocation key names member {device}, which the volume does not have.");
    var generationByBucket = new Dictionary<long, byte>();
    foreach (var key in ReadTree(BtreeBucketGens)) {
      if (key.Type != KeyBucketGens || key.Value.Length != BucketGensNr)
        throw new NotSupportedException("bcachefs in-place mutation requires canonical bucket_gens keys.");
      var total = BucketsOf(key.Position.Inode);
      var first = checked((long)key.Position.Offset * BucketGensNr);
      for (var i = 0; i < key.Value.Length && first + i < total; ++i)
        if (key.Value[i] != 0)
          generationByBucket[BcacheFsBucketId.Of((byte)key.Position.Inode, first + i)] = key.Value[i];
    }

    var occupied = new HashSet<long>();
    var bucketStates = new Dictionary<long, BcacheFsMetadataCommit.BucketState>();
    var allocKeys = ReadTree(BtreeAlloc);
    if (allocKeys.Any(k => k.Type != KeyAllocV4))
      throw new NotSupportedException("bcachefs in-place mutation requires alloc_v4 allocation keys.");

    foreach (var key in allocKeys) {
      if (key.Position.Offset >= (ulong)BucketsOf(key.Position.Inode) || key.Value.Length < 20)
        throw new InvalidDataException("bcachefs: malformed alloc_v4 key in the allocation tree.");
      var bucket = BcacheFsBucketId.Of((byte)key.Position.Inode, (long)key.Position.Offset);
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
      ValidateExtent(extent, devices);
    foreach (var key in reflink)
      if (BcacheFsAllocationBuilder.IndirectAsExtent(key) is { } extent)
        ValidateExtent(extent, devices);

    var preserved = new Dictionary<int, List<Key>> {
      [BtreeSubvolumes] = ReadTree(BtreeSubvolumes),
      [BtreeSnapshots] = ReadTree(BtreeSnapshots),
      [BtreeSnapshotTrees] = ReadTree(BtreeSnapshotTrees),
    };

    var maxInode = inodeKeys.Keys.DefaultIfEmpty(FirstDynamicInode - 1).Max();
    return new Model {
      Devices = devices,
      Magic = magic,
      Directories = directories,
      Files = files,
      PreservedTrees = preserved,
      Xattrs = xattrs,
      OtherInodeKeys = otherInodeKeys,
      Reflink = reflink,
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

  /// <summary>
  /// The pointer extents in a bucket not laid out the way this package lays data down: every
  /// extent in a bucket belongs to one file, plain, its checksummed region exactly its live data,
  /// and they run back to back from the bucket's first sector. A bucket larger than one crc32
  /// extent (bcachefs format's 128 KiB default) holds several such extents of the same file.
  /// </summary>
  private static HashSet<Bpos> NonCanonicalExtents(IEnumerable<Key> extents) {
    var placed = extents
      .Select(e => (Key: e, At: BcacheFsAllocationBuilder.Footprint(e)))
      .Where(x => x.At.HasValue)
      .Select(x => (x.Key, At: x.At!.Value))
      .ToList();
    var result = new HashSet<Bpos>();
    foreach (var bucket in placed.GroupBy(x => x.At.Sector / BucketSectors)) {
      var run = bucket.OrderBy(x => x.At.Sector).ToArray();
      var next = bucket.Key * BucketSectors;
      var plain = !IsMetadataReservation(bucket.Key) && run.All(x => x.Key.Position.Inode == run[0].Key.Position.Inode);
      foreach (var (key, at) in run) {
        plain &= at.Sector == next && at.RegionOffset == 0 && at.RegionSectors == key.Size && !at.Compressed && !at.Encrypted;
        next = at.Sector + key.Size;
      }
      if (!plain) result.UnionWith(run.Select(x => x.Key.Position));
    }
    return result;
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
    using var buckets = UseVolumeBuckets(image);
    var model = ReadModel([image]);
    if (model.Devices.Streams.Count != 1)
      throw new NotSupportedException("bcachefs: the volume has several members; bringing it into the writer's layout is implemented for one.");
    if (model.Reflink.Count != 0)
      throw new NotSupportedException("bcachefs: the volume holds reflinked (shared) data, which is not moved into the writer's layout.");
    var moving = NonCanonicalExtents(model.Files.Values.SelectMany(f => f.FinalExtents));
    // Refused before a byte is written, so a refusal leaves the volume as it was.
    foreach (var file in model.Files.Values)
      foreach (var key in file.FinalExtents)
        if (moving.Contains(key.Position) && BcacheFsAllocationBuilder.Footprint(key) is { } coded
            && (coded.Compressed || coded.Encrypted))
          throw new NotSupportedException(
            $"bcachefs: '{file.Path}' has a compressed or encrypted extent, which an edit here does not move.");
    foreach (var file in model.Files.Values)
      foreach (var key in file.FinalExtents)
        if (moving.Contains(key.Position) && (key.Size > MaxCrc32ExtentSectors || key.Size > BucketSectors))
          throw new NotSupportedException(
            $"bcachefs: '{file.Path}' has a {key.Size}-sector extent, more than one crc32 extent or bucket holds; an edit here does not split it.");
    var free = FreeDataBuckets(model)[0];
    if (moving.Count > free.Count)
      throw new IOException("bcachefs: not enough free buckets to bring the volume into the writer's layout.");

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

    Commit(model);
  }

  /// <summary>
  /// Free buckets past the metadata reservation and before the tail superblock,
  /// those still at generation zero first, per member (bucket numbers within it).
  /// </summary>
  /// <remarks>
  /// A 1.3 backpointer has no field for the generation of the bucket it points
  /// into, so a newer bcachefs upgrading the volume discards every backpointer into
  /// a bucket whose generation is not zero and rebuilds them; data laid into
  /// never-reused buckets leaves it nothing to rebuild.
  /// </remarks>
  private static Dictionary<byte, Queue<long>> FreeDataBuckets(Model model) {
    var result = new Dictionary<byte, Queue<long>>();
    foreach (var (device, geometry) in model.Devices.Geometry) {
      var firstDataBucket = (long)FirstMetadataBucket + MetadataBuckets;
      var firstTailSbBucket = (geometry.DeviceSectors - SbSlotSectors) / BucketSectors;
      var candidates = new List<long>();
      if (model.Devices.Allows(device, DataUser))
        for (var bucket = firstDataBucket; bucket < firstTailSbBucket; ++bucket)
          if (!model.OccupiedBuckets.Contains(BcacheFsBucketId.Of(device, bucket))) candidates.Add(bucket);
      result[device] = new Queue<long>(candidates
        .OrderBy(b => model.BucketStates.GetValueOrDefault(BcacheFsBucketId.Of(device, b)).Generation != 0 ? 1 : 0)
        .ThenBy(b => b));
    }
    return result;
  }

  /// <summary>
  /// Takes one bucket on each of <paramref name="copies" /> different members from
  /// <paramref name="free" />, the members with the most left first, and returns them by
  /// <see cref="BcacheFsBucketId" />; null when fewer members than that have one.
  /// </summary>
  private static long[]? TakeCopies(Dictionary<byte, Queue<long>> free, int copies) {
    var members = free.Where(f => f.Value.Count != 0)
      .OrderByDescending(f => f.Value.Count).ThenBy(f => f.Key)
      .Take(copies).ToArray();
    if (members.Length < copies) return null;
    return [.. members.OrderBy(f => f.Key).Select(f => BcacheFsBucketId.Of(f.Key, f.Value.Dequeue()))];
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

  private static void Commit(Model model) {
    var devices = model.Devices;
    AssignNewDirentOffsets(model);
    AllocatePendingData(model);
    WritePendingData(model);

    var trees = BuildLogicalTrees(model);
    var live = trees[BtreeExtents]
      .Concat(trees[BtreeReflink].Select(BcacheFsAllocationBuilder.IndirectAsExtent).OfType<Key>())
      .SelectMany(BcacheFsAllocationBuilder.Footprints)
      .ToList();
    var userBuckets = live.Select(f => BcacheFsBucketId.OfSector(f.Device, f.Sector)).ToHashSet();

    // All live source keys were materialized before this point, so the buckets
    // the new generation lands in can be rewritten without needing any old node.
    var published = BcacheFsMetadataCommit.Publish(devices, model.Magic, trees,
      model.BucketStates, shapes => ChooseMetadataBuckets(shapes, devices, userBuckets, model.BucketStates));
    BcacheFsSuperblockEditor.PublishClean(devices, published);

    // Deleted/replaced user data becomes forensic free space only after the new
    // roots are live. New payload was deliberately allocated outside these runs;
    // the new metadata generation may have landed in a bucket they emptied, and a
    // surviving key may share a region with a removed one, so both are spared.
    var spared = live
      .Select(f => (f.Device, Offset: f.Sector * SectorSize, Length: (long)f.RegionSectors * SectorSize))
      .Concat(published.MetadataBuckets.Select(b =>
        (Device: BcacheFsBucketId.Device(b), Offset: BcacheFsBucketId.Index(b) * BucketBytes, Length: (long)BucketBytes)))
      .ToList();
    foreach (var (device, offset, length) in model.FreedRanges) {
      var onDevice = spared.Where(s => s.Device == device).Select(s => (s.Offset, s.Length)).ToList();
      foreach (var (from, to) in Subtract(offset, offset + length, onDevice))
        ZeroRange(devices[device], from, to - from);
    }

    devices.Flush();
    if (devices.Streams.Count != 1) return;

    // Internal consistency witness available on every platform; external
    // bcachefs fsck tests remain the authority where the tool exists.
    var image = devices[0];
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
      var parent = model.Directories[dir.ParentPath];
      dir.DirentOffset = ReserveDirentOffset(used, parent.Inode, HashSeedOf(parent), dir.Name);
    }
    foreach (var file in model.Files.Values.Where(f => f.ExistingDirent == null)) {
      var parent = model.Directories[file.ParentPath];
      file.DirentOffset = ReserveDirentOffset(used, parent.Inode, HashSeedOf(parent), file.Name);
    }
  }

  /// <summary>
  /// The seed a directory's entries are hashed with: <c>bi_hash_seed</c>, the second word of its
  /// <c>inode_v3</c>. The kernel draws it at random for a directory it makes; one this editor
  /// makes gets the seed its writer gives it.
  /// </summary>
  private static ulong HashSeedOf(DirectoryState directory)
    => directory.ExistingInode is { Value.Length: >= 16 } inode
      ? BinaryPrimitives.ReadUInt64LittleEndian(inode.Value.AsSpan(8))
      : BcacheFsWriter.HashSeed(directory.Inode);

  private static ulong ReserveDirentOffset(Dictionary<ulong, HashSet<ulong>> used, ulong parent, ulong seed, string name) {
    if (!used.TryGetValue(parent, out var set)) used[parent] = set = [];
    var offset = DirentHash(seed, name);
    while (!set.Add(offset)) ++offset;
    return offset;
  }

  private static void AllocatePendingData(Model model) {
    var free = FreeDataBuckets(model);
    var copies = model.Devices.DataReplicas;

    foreach (var file in model.Files.Values.Where(f => f.Pending != null)) {
      var remaining = file.Pending!.Length;
      while (remaining > 0) {
        file.Pending.Buckets.Add(TakeCopies(free, copies) ?? throw new IOException(
          $"bcachefs: not enough free buckets on {copies} member(s) for in-place write of '{file.Path}'."));
        remaining -= Math.Min((long)BucketBytes, remaining);
      }
    }
  }

  private static void WritePendingData(Model model) {
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

        var sectors = BcacheFsBucketGeometry.RoundUpSectors(got);
        Array.Clear(buffer, got, sectors * SectorSize - got);
        // One bucket per copy, each on its own member; the bytes are the same in all.
        var copies = pending.Buckets[bucketIndex++];
        foreach (var copy in copies) {
          var stream = model.Devices[BcacheFsBucketId.Device(copy)];
          stream.Position = BcacheFsBucketId.Index(copy) * BucketBytes;
          stream.Write(buffer, 0, sectors * SectorSize);
        }

        // A bucket larger than a crc32 entry can describe (bcachefs format's 128 KiB
        // default) holds its data as several back-to-back extents; each extent is the
        // checksum entry and then one pointer per copy.
        for (var done = 0; done < sectors;) {
          var piece = Math.Min(MaxCrc32ExtentSectors, sectors - done);
          var checksum = DataChecksum(buffer.AsSpan(done * SectorSize, piece * SectorSize));
          var value = new byte[8 + 8 * copies.Length];
          BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32(piece, checksum));
          for (var c = 0; c < copies.Length; ++c)
            BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8 + 8 * c),
              ExtentPointer(BcacheFsBucketId.Index(copies[c]) * BucketSectors + done, BcacheFsBucketId.Device(copies[c]),
                model.BucketStates.GetValueOrDefault(copies[c]).Generation));
          logicalSector += piece;
          done += piece;
          file.FinalExtents.Add(new Key(KeyExtent,
            new Bpos(file.Inode, (ulong)logicalSector, SnapshotIdMax),
            (uint)piece, value));
        }

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
      var sectors = (ulong)BcacheFsBucketGeometry.RoundUpSectors(file.Length);
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

    trees[BtreeReflink] = [.. model.Reflink];
    trees[BtreeSubvolumes] = [.. model.PreservedTrees[BtreeSubvolumes]];
    trees[BtreeSnapshots] = [.. model.PreservedTrees[BtreeSnapshots]];
    trees[BtreeSnapshotTrees] = [.. model.PreservedTrees[BtreeSnapshotTrees]];

    foreach (var list in trees.Values)
      list.Sort((a, b) => Compare(a.Position, b.Position));
    return trees;
  }

  /// <summary>The node slots one commit's metadata generation is written into.</summary>
  /// <remarks>
  /// Taken in order from the first bucket after the journal, skipping whatever
  /// holds file data. A placement pass is free to put extents anywhere, so the
  /// metadata has to be the thing that moves out of the way.
  /// </remarks>
  private static IReadOnlyDictionary<int, long[]> ChooseMetadataBuckets(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes,
      BcacheFsDevices devices,
      IReadOnlySet<long> userBuckets,
      IReadOnlyDictionary<long, BcacheFsMetadataCommit.BucketState> states) {
    // Where a node is smaller than a bucket the nodes share buckets, every one of
    // them metadata only. Each node has a copy on as many members as the volume
    // keeps metadata replicas: a row of buckets, one per copy, holds one bucket's
    // worth of nodes.
    var perBucket = BucketSectors / NodeSectors;
    var copies = devices.MetadataReplicas;
    var nodeCount = shapes.Values.Sum(s => s.Count);
    var count = (nodeCount + perBucket - 1) / perBucket;
    if (count > MetadataBuckets)
      throw new NotSupportedException(
        $"bcachefs metadata needs {count} buckets; an in-place commit budgets {MetadataBuckets}.");

    var free = new Dictionary<byte, Queue<long>>();
    foreach (var (device, geometry) in devices.Geometry)
      if (devices.Allows(device, DataBtree))
        free[device] = new Queue<long>(MetadataCandidates(device, geometry, count, userBuckets, states));

    var slots = new List<long>();
    for (var row = 0; row < count; ++row) {
      var buckets = TakeCopies(free, copies) ?? throw new NotSupportedException(
        $"bcachefs metadata needs {count} free buckets on {copies} member(s); fewer are available.");
      for (var k = 0; k < perBucket; ++k)
        foreach (var bucket in buckets)
          slots.Add(BcacheFsBucketId.Of(BcacheFsBucketId.Device(bucket), BcacheFsBucketId.Index(bucket) * perBucket + k));
    }
    var queue = new Queue<long>(slots);

    var result = new Dictionary<int, long[]>();
    foreach (var id in BcacheFsMetadataCommit.TreeOrder)
      if (shapes.TryGetValue(id, out var treeShapes))
        result[id] = [.. Enumerable.Range(0, treeShapes.Count * copies).Select(_ => queue.Dequeue())];
    return result;
  }

  /// <summary>
  /// A member's buckets a metadata generation may go into, best first: the reservation, then
  /// buckets still at generation zero, never a superblock slot, the journal or file data.
  /// </summary>
  private static IEnumerable<long> MetadataCandidates(byte device, BcacheFsAllocationBuilder.Geometry geometry, int count,
      IReadOnlySet<long> userBuckets, IReadOnlyDictionary<long, BcacheFsMetadataCommit.BucketState> states) {
    var lastSbBucket = (geometry.DeviceSectors - SbSlotSectors) / BucketSectors;
    // A volume bcachefs format laid out keeps its superblock slots and journal wherever it chose;
    // no node goes there.
    var fixedBuckets = new HashSet<long>();
    for (var sector = 0L; sector < PrimarySbSector + SbSlotSectors; sector += BucketSectors) fixedBuckets.Add(sector / BucketSectors);
    foreach (var slot in geometry.SuperblockSectors)
      for (var sector = slot; sector < slot + SbSlotSectors; sector += BucketSectors) fixedBuckets.Add(sector / BucketSectors);
    foreach (var (start, journalCount) in geometry.JournalBuckets)
      for (var b = start; b < start + journalCount; ++b) fixedBuckets.Add(b);
    // The reservation first, and within it the buckets still at generation zero:
    // a backpointer into a reused bucket is one a newer bcachefs throws away and
    // rebuilds when it upgrades the volume (see FreeDataBuckets).
    var candidates = new List<long>();
    for (var bucket = (long)FirstMetadataBucket; bucket < lastSbBucket; ++bucket) {
      if (userBuckets.Contains(BcacheFsBucketId.Of(device, bucket)) || fixedBuckets.Contains(bucket)) continue;
      candidates.Add(bucket);
      if (!IsMetadataReservation(bucket) && candidates.Count >= count + MetadataBuckets) break;
    }
    return candidates
      .OrderBy(b => IsMetadataReservation(b) ? 0 : 1)
      .ThenBy(b => states.GetValueOrDefault(BcacheFsBucketId.Of(device, b)).Generation != 0 ? 1 : 0)
      .ThenBy(b => b)
      .Take(count);
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
  // ── Reflinks ────────────────────────────────────────────────────────────

  /// <summary>
  /// The stretch of the reflink tree a <c>reflink_p</c> holds a reference to: <c>[idx - front_pad,
  /// idx + size + back_pad)</c> (value: idx u64, front_pad u32, back_pad u32). Every indirect extent
  /// overlapping it counts the reference once.
  /// </summary>
  private static (ulong Start, ulong End) ReferencedRange(Key reflinkP) {
    if (reflinkP.Value.Length < 8)
      throw new InvalidDataException($"bcachefs: reflink_p at inode {reflinkP.Position.Inode} is too short.");
    var idx = BinaryPrimitives.ReadUInt64LittleEndian(reflinkP.Value);
    var front = reflinkP.Value.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(reflinkP.Value.AsSpan(8)) : 0u;
    var back = reflinkP.Value.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(reflinkP.Value.AsSpan(12)) : 0u;
    return (idx - front, idx + reflinkP.Size + back);
  }

  private static bool Overlaps(Key indirect, (ulong Start, ulong End) range)
    => indirect.Position.Offset > range.Start && indirect.Position.Offset - indirect.Size < range.End;

  /// <summary>
  /// Refuses a reflink tree an edit here would publish wrong: a key that is not an indirect extent,
  /// or a refcount that is not the number of <c>reflink_p</c> keys referencing the extent — the
  /// only thing an edit changes is that count, and it starts from the truth or not at all.
  /// </summary>
  private static void RequireConsistentReflinks(List<Key> extents, List<Key> reflink) {
    var foreign = reflink.FirstOrDefault(k => k.Type is not (KeyReflinkV or KeyIndirectInlineData));
    if (foreign.Value != null)
      throw new NotSupportedException($"bcachefs: the reflink tree holds key type {foreign.Type}, which an edit here does not carry.");
    var ranges = extents.Where(static k => k.Type == KeyReflinkP).Select(ReferencedRange).ToList();
    foreach (var key in reflink) {
      if (key.Value.Length < 8)
        throw new InvalidDataException($"bcachefs: indirect extent at {key.Position.Offset} is too short.");
      var refcount = BinaryPrimitives.ReadUInt64LittleEndian(key.Value);
      var counted = (ulong)ranges.Count(r => Overlaps(key, r));
      if (refcount != counted)
        throw new NotSupportedException(
          $"bcachefs: the indirect extent ending at {key.Position.Offset} counts {refcount} references but {counted} reach it; it is not edited.");
    }
  }

  /// <summary>
  /// Takes away the references a removed or replaced file's <c>reflink_p</c> keys held: each
  /// indirect extent they reach counts one fewer, and one no file references any more is dropped,
  /// its data freed with it.
  /// </summary>
  private static void DropReflinks(Model model, IEnumerable<Key> extents) {
    foreach (var range in extents.Where(static k => k.Type == KeyReflinkP).Select(ReferencedRange).ToList()) {
      for (var i = model.Reflink.Count - 1; i >= 0; --i) {
        var key = model.Reflink[i];
        if (!Overlaps(key, range)) continue;
        var value = key.Value.ToArray();
        var refcount = BinaryPrimitives.ReadUInt64LittleEndian(value);
        if (refcount <= 1) {
          if (BcacheFsAllocationBuilder.IndirectAsExtent(key) is { } extent) AddFreedRange(model.FreedRanges, extent);
          model.Reflink.RemoveAt(i);
          continue;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(value, refcount - 1);
        model.Reflink[i] = key with { Value = value };
      }
    }
  }

  private static void ValidateExtent(Key extent, BcacheFsDevices devices) {
    if (extent.Type != KeyExtent) return;
    var copies = BcacheFsAllocationBuilder.Footprints(extent);
    if (copies.Count == 0)
      throw new InvalidDataException($"bcachefs extent at inode {extent.Position.Inode} carries no dirty pointer.");
    foreach (var at in copies) {
      if (!devices.Streams.ContainsKey(at.Device))
        throw new InvalidDataException($"bcachefs extent at inode {extent.Position.Inode} points at member {at.Device}, which was not supplied.");
      if (extent.Size == 0 || at.Sector / BucketSectors != (at.Sector + Math.Max(at.RegionSectors, at.DiskSectors) - 1) / BucketSectors)
        throw new NotSupportedException(
          $"bcachefs extent at inode {extent.Position.Inode} crosses a bucket boundary, which no writer produces.");
    }
  }

  /// <summary>
  /// Records the device sectors a removed extent's data occupied: its live part,
  /// or a compressed region whole. Inline data and reservations occupy none.
  /// </summary>
  private static void AddFreedRange(List<(byte Device, long Offset, long Length)> ranges, Key extent) {
    foreach (var at in BcacheFsAllocationBuilder.Footprints(extent))
      ranges.Add(at.Compressed
        ? (at.Device, at.Sector * SectorSize, (long)at.RegionSectors * SectorSize)
        : (at.Device, (at.Sector + at.RegionOffset) * SectorSize, (long)extent.Size * SectorSize));
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
