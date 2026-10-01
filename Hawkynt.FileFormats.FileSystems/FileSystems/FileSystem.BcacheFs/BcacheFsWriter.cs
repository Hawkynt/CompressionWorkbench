#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Writes a bcachefs volume: a superblock, the b-trees that describe the files,
/// and the files themselves.
/// </summary>
/// <remarks>
/// <para>bcachefs keeps no directory blocks and no inode table. A file's name is a
/// key in the dirents tree, its metadata a key in the inodes tree, and its bytes
/// are named by keys in the extents tree; a volume is those trees plus a
/// superblock that says where their roots are. Because the volume is written whole
/// and never mounted in between, it is written as a cleanly shut down volume: the
/// roots and the usage totals go in the superblock's clean section, and the
/// journal is left empty.</para>
///
/// <para>The volume is metadata version 1.3, the version the bcachefs-tools that
/// Debian and Ubuntu ship write and check, so <c>bcachefs fsck</c> from those
/// packages can confirm it, and newer tools upgrade it as they open it. The allocation
/// information it carries is derived by <see cref="BcacheFsAllocationBuilder" />
/// from where everything actually landed; <c>docs/BCACHEFS-ON-DISK.md</c> says
/// what each structure holds and how it was established.</para>
/// </remarks>
public sealed class BcacheFsWriter {

  /// <summary>BCHFS_MAGIC, in storage byte order.</summary>
  public static readonly byte[] BcachefsMagic = Magic;

  /// <summary>
  /// Smallest volume this writes. A bcachefs device needs at least 512 buckets, and
  /// the two superblock slots at the front already claim thirty-three of them.
  /// </summary>
  public const long MinImageSize = 128L * 1024 * 1024;

  /// <summary>First bucket the journal takes, past the two front superblock slots.</summary>
  internal const int JournalFirstBucket = 33;

  internal const int JournalBuckets = 16;

  internal const int FirstMetadataBucket = JournalFirstBucket + JournalBuckets;

  /// <summary>
  /// Buckets set aside for the b-trees, whatever shape they turn out to be.
  /// </summary>
  /// <remarks>
  /// A fixed reservation rather than a count worked out from the files, because
  /// where a volume's own structures end has to be knowable from the volume rather
  /// than from what was written into it — the layout pass needs it, and so does
  /// anything asking what space is free.
  /// </remarks>
  internal const int MetadataBuckets = 64;

  /// <summary>Largest extent one key describes: the encoded extent maximum, 64 KiB.</summary>
  private const int MaxExtentSectors = 1 << EncodedExtentMaxBits;

  /// <summary>The first inode number handed to anything other than the root.</summary>
  internal const ulong FirstDynamicInode = 2147483648UL;

  private enum Kind { File, Directory, Symlink }

  private readonly List<(string Name, Kind Kind, FilePayload Payload, ArchiveEntryMetadata? Metadata)> _entries = [];
  // Empty by default, as `bcachefs format` leaves it unless a label is asked for.
  private string _label = "";
  private long _imageSize = MinImageSize;
  private Guid _internalUuid = Guid.NewGuid();
  private Guid _userUuid = Guid.NewGuid();
  private readonly Guid _memberUuid = Guid.NewGuid();

  /// <summary>Sets the volume label; it is truncated into the superblock's 32-byte field.</summary>
  public void SetLabel(string label) {
    ArgumentNullException.ThrowIfNull(label);
    this._label = label;
  }

  /// <summary>Overrides the internal UUID, which is also what the metadata magic is derived from.</summary>
  public void SetInternalUuid(Guid uuid) => this._internalUuid = uuid;

  /// <summary>Overrides the user-facing UUID.</summary>
  public void SetUserUuid(Guid uuid) => this._userUuid = uuid;

  /// <summary>Sets the total volume size in bytes.</summary>
  public void SetImageSize(long bytes) {
    if (bytes < MinImageSize)
      throw new ArgumentOutOfRangeException(nameof(bytes),
        $"A bcachefs volume must be at least {MinImageSize} bytes.");
    this._imageSize = bytes;
  }

  /// <summary>Adds a file, held in memory, with the times, ownership and mode it should carry.</summary>
  public void AddFile(string name, byte[] data, ArchiveEntryMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((name, Kind.File, FilePayload.FromBytes(data), metadata));
  }

  /// <summary>Adds a file whose bytes are read as the volume is written.</summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream,
      ArchiveEntryMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._entries.Add((name, Kind.File, FilePayload.FromStream(size, openStream), metadata));
  }

  /// <summary>Adds a directory, which may stay empty.</summary>
  public void AddDirectory(string name) {
    ArgumentNullException.ThrowIfNull(name);
    this._entries.Add((name, Kind.Directory, FilePayload.Empty, null));
  }

  /// <summary>
  /// Adds a symbolic link pointing at <paramref name="target" />.
  /// </summary>
  /// <remarks>
  /// bcachefs keeps a link's target as the link inode's data, written through the
  /// page cache with its terminating NUL, so the inode's size is the target's
  /// length plus one.
  /// </remarks>
  public void AddSymlink(string name, string target) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentException.ThrowIfNullOrEmpty(target);
    var bytes = Encoding.UTF8.GetBytes(target);
    if (bytes.Contains((byte)0))
      throw new ArgumentException("A symbolic link target cannot contain NUL.", nameof(target));
    var data = new byte[bytes.Length + 1];
    bytes.CopyTo(data, 0);
    this._entries.Add((name, Kind.Symlink, FilePayload.FromBytes(data), null));
  }

  /// <summary>
  /// The smallest volume that holds <paramref name="fileSizes" />: the superblock
  /// slots, the journal, the b-tree reservation, the file data, and the slot at
  /// the tail.
  /// </summary>
  public static long EstimateSize(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var buckets = (long)FirstMetadataBucket + MetadataBuckets;
    foreach (var size in fileSizes)
      buckets += (size + BucketBytes - 1) / BucketBytes;

    // The tail superblock slot, plus a bucket of slack so the slot never lands on
    // the last file.
    var bytes = (buckets + 1) * BucketBytes + (long)SbSlotSectors * SectorSize;
    return Math.Max(MinImageSize, (bytes + (1L << 20) - 1) & ~((1L << 20) - 1));
  }

  /// <summary>Writes the volume.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("Writing a bcachefs volume needs a seekable, writable stream.", nameof(output));

    var plan = this.BuildPlan();
    output.SetLength(0);
    output.SetLength(this._imageSize);

    // The data goes down first, because an extent records a checksum of the bytes
    // it covers and those are not known until they have been read.
    WriteFileData(output, plan);

    var deviceSectors = this._imageSize / SectorSize;
    long[] slots = [PrimarySbSector, PrimarySbSector + SbSlotSectors, deviceSectors - SbSlotSectors];
    (long, long)[] journal = [(JournalFirstBucket, JournalBuckets)];
    var geometry = new BcacheFsAllocationBuilder.Geometry(deviceSectors, slots, journal);
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(this._internalUuid.ToByteArray());

    var published = BcacheFsMetadataCommit.Publish(output, magic, geometry, plan.Trees,
      new Dictionary<long, BcacheFsMetadataCommit.BucketState>(), ReservedPlacement);

    var superblock = BcacheFsSuperblockComposer.Build(
      new BcacheFsSuperblockComposer.Description(this._internalUuid, this._userUuid, this._memberUuid,
        this._label, deviceSectors, slots, journal),
      published.Roots, published.Usage, published.Inodes);
    foreach (var slot in slots) {
      BinaryPrimitives.WriteUInt64LittleEndian(superblock.AsSpan(104), (ulong)slot);
      BcacheFsSuperblockComposer.StampChecksum(superblock);
      output.Position = slot * SectorSize;
      output.Write(superblock, 0, superblock.Length);
    }

    // The last journal entry a clean shutdown leaves: no keys, the same roots and totals.
    output.Position = JournalFirstBucket * (long)BucketBytes;
    output.Write(BcacheFsSuperblockComposer.JournalEntry(magic, published.Roots, published.Usage, published.Inodes));

    // The layout is repeated on its own, in the sector before the first superblock.
    output.Position = LayoutSector * SectorSize;
    output.Write(superblock, SbLayoutOffset, SbLayoutBytes);
    output.Flush();
  }

  /// <summary>
  /// Gives the trees consecutive buckets of the reservation, in the order they are
  /// laid down.
  /// </summary>
  internal static IReadOnlyDictionary<int, long[]> ReservedPlacement(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes) {
    var next = (long)FirstMetadataBucket;
    var result = new Dictionary<int, long[]>();
    foreach (var id in BcacheFsMetadataCommit.TreeOrder) {
      if (!shapes.TryGetValue(id, out var treeShapes)) continue;
      var buckets = new long[treeShapes.Count];
      for (var i = 0; i < buckets.Length; ++i) buckets[i] = next++;
      result[id] = buckets;
    }
    if (next > FirstMetadataBucket + MetadataBuckets)
      throw new NotSupportedException(
        $"A bcachefs volume of this many files needs {next - FirstMetadataBucket} b-tree buckets, "
        + $"more than the {MetadataBuckets} reserved for them.");
    return result;
  }

  // ── Planning ────────────────────────────────────────────────────────────

  private sealed class PlannedFile {
    internal required ulong Inode { get; init; }
    internal required long Length { get; init; }
    internal required FilePayload Payload { get; init; }
    internal ArchiveEntryMetadata? Metadata { get; init; }
    internal long FirstSector { get; set; }
    internal List<byte[]> ExtentValues { get; } = [];
  }

  private sealed class PlannedDirectory {
    internal required string Name { get; init; }
    internal required ulong Inode { get; init; }
    internal required ulong Parent { get; init; }
  }

  private sealed class Plan {
    internal required Dictionary<int, List<Key>> Trees { get; init; }
    internal required List<PlannedFile> Files { get; init; }
  }

  /// <summary>Where one file's extents fall: each one's first sector and length.</summary>
  private static IEnumerable<(long Placed, int Sectors)> ExtentSpans(long length) {
    var placed = 0L;
    var remaining = length;
    while (remaining > 0) {
      var want = (int)Math.Min(MaxExtentSectors * SectorSize, remaining);
      var sectors = (want + SectorSize - 1) / SectorSize;
      yield return (placed, sectors);
      placed += sectors;
      remaining -= want;
    }
  }

  /// <summary>
  /// Writes every file's bytes and stamps the checksum into the extents that name them.
  /// </summary>
  /// <remarks>
  /// An extent carries a checksum of the whole sectors it covers, tail padding
  /// included, so the bytes are hashed on their way to the volume rather than read
  /// back afterwards — a file that is streamed in is only ever read once.
  /// </remarks>
  private static void WriteFileData(Stream output, Plan plan) {
    var buffer = new byte[MaxExtentSectors * SectorSize];

    foreach (var file in plan.Files) {
      if (file.Length == 0) continue;

      output.Position = file.FirstSector * SectorSize;
      using var source = file.Payload.Open();

      var remaining = file.Length;
      var next = 0;
      while (remaining > 0) {
        var want = (int)Math.Min(buffer.Length, remaining);
        var got = 0;
        while (got < want) {
          var n = source.Read(buffer, got, want - got);
          if (n <= 0)
            throw new EndOfStreamException($"bcachefs input ended {remaining - got} bytes short of its declared size.");
          got += n;
        }

        // The tail of the last extent is padding, and the checksum covers it.
        var sectors = (got + SectorSize - 1) / SectorSize;
        Array.Clear(buffer, got, sectors * SectorSize - got);
        output.Write(buffer, 0, sectors * SectorSize);

        BinaryPrimitives.WriteUInt64LittleEndian(file.ExtentValues[next++],
          ExtentCrc32(sectors, DataChecksum(buffer.AsSpan(0, sectors * SectorSize))));
        remaining -= got;
      }
    }
  }

  private Plan BuildPlan() {
    var directories = new Dictionary<string, PlannedDirectory>(StringComparer.Ordinal);
    var nextInode = FirstDynamicInode;
    var inodes = new List<Key>();
    var dirents = new List<Key>();
    var extents = new List<Key>();
    var files = new List<PlannedFile>();
    var leaves = new List<(string Name, ulong Parent, Kind Kind, FilePayload Payload, ArchiveEntryMetadata? Metadata)>();

    ulong DirectoryInode(string path, ulong parent, string leaf) {
      if (directories.TryGetValue(path, out var existing)) return existing.Inode;
      var inode = nextInode++;
      directories[path] = new PlannedDirectory { Name = leaf, Inode = inode, Parent = parent };
      return inode;
    }

    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (rawName, kind, payload, metadata) in this._entries) {
      var parts = rawName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;

      var parent = RootInode;
      var accumulated = string.Empty;
      var depth = kind == Kind.Directory ? parts.Length : parts.Length - 1;
      for (var i = 0; i < depth; ++i) {
        accumulated = accumulated.Length == 0 ? parts[i] : accumulated + "/" + parts[i];
        parent = DirectoryInode(accumulated, parent, parts[i]);
      }
      if (kind == Kind.Directory) continue;

      var path = string.Join('/', parts);
      if (directories.ContainsKey(path) || !seen.Add(path))
        throw new ArgumentException($"bcachefs: '{path}' is added twice.");
      leaves.Add((parts[^1], parent, kind, payload, metadata));
    }

    // ── Where the data goes: each file from a fresh bucket, after the reservation ──
    var bucket = (long)FirstMetadataBucket + MetadataBuckets;
    var planned = new List<(PlannedFile File, string Name, ulong Parent, Kind Kind)>();
    foreach (var (name, parent, kind, payload, metadata) in leaves) {
      var file = new PlannedFile {
        Inode = nextInode++, Length = payload.Size, Payload = payload, Metadata = metadata,
        FirstSector = bucket * BucketSectors,
      };
      bucket += (file.Length + BucketBytes - 1) / BucketBytes;
      files.Add(file);
      planned.Add((file, name, parent, kind));
    }

    var needed = (bucket + 1) * BucketBytes + (long)SbSlotSectors * SectorSize;
    if (this._imageSize < needed)
      this._imageSize = Math.Max(MinImageSize, (needed + (1L << 20) - 1) & ~((1L << 20) - 1));

    // ── The keys ──
    inodes.Add(InodeKey(RootInode, 0, 0, ModeDirectory, size: 0, sectors: 0,
      links: directories.Values.Count(d => d.Parent == RootInode), subvolume: RootSubvolume));

    foreach (var directory in directories.Values) {
      inodes.Add(InodeKey(directory.Inode, directory.Parent, DirentHash(HashSeed(directory.Parent), directory.Name),
        ModeDirectory, size: 0, sectors: 0, links: directories.Values.Count(d => d.Parent == directory.Inode)));
      dirents.Add(DirentKey(directory.Parent, directory.Name, directory.Inode, DtDir));
    }

    foreach (var (file, name, parent, kind) in planned) {
      var sectors = (file.Length + SectorSize - 1) / SectorSize;
      inodes.Add(InodeKey(file.Inode, parent, DirentHash(HashSeed(parent), name),
        kind == Kind.Symlink ? ModeSymlink : ModeFile, size: (ulong)file.Length, sectors: (ulong)sectors, links: 0,
        metadata: file.Metadata));
      dirents.Add(DirentKey(parent, name, file.Inode, kind == Kind.Symlink ? DtLnk : DtReg));

      foreach (var (placed, spanSectors) in ExtentSpans(file.Length)) {
        // The checksum is not known until the bytes are read; the key is, and the
        // layout has to count it now or the trees are sized for another volume.
        var extent = ExtentKey(file, placed, spanSectors);
        extents.Add(extent);
        file.ExtentValues.Add(extent.Value);
      }
    }

    return new Plan {
      Files = files,
      Trees = new Dictionary<int, List<Key>> {
        [BtreeExtents] = extents,
        [BtreeInodes] = inodes,
        [BtreeDirents] = dirents,
        [BtreeSubvolumes] = [SubvolumeKey()],
        [BtreeSnapshots] = [SnapshotKey()],
        [BtreeSnapshotTrees] = [SnapshotTreeKey()],
      },
    };
  }

  // ── Keys ────────────────────────────────────────────────────────────────

  internal const ulong ModeDirectory = 0x41ED;                                  // 040755
  internal const ulong ModeFile = 0x81A4;                                       // 0100644
  internal const ulong ModeSymlink = 0xA1FF;                                    // 0120777

  /// <summary>The hash seed a directory's entries are placed by.</summary>
  internal static ulong HashSeed(ulong inode) => inode * 0x9E3779B97F4A7C15UL | 1UL;

  /// <summary>
  /// Builds one inode record.
  /// </summary>
  /// <remarks>
  /// <para>The fixed part carries the hash seed, the flags, the size and the sector
  /// count; everything else is a list of variable-length fields in a fixed order,
  /// each one a varint, and a field the inode has nothing to say about is a single
  /// zero byte. The list stops at the last field that has something in it, and the
  /// flags record how many that was — write more and a reader looks past the end of
  /// the record, write fewer and it reads the wrong field.</para>
  ///
  /// <para>The four time fields are 96 bits wide, so each is a varint for the low
  /// 64 bits followed by one for the high 32. A time is a signed count of the
  /// superblock's time units from its time base, stored two's-complement in the
  /// low half.</para>
  ///
  /// <para><paramref name="metadata" /> supplies the times, the owner and group, and
  /// the permission bits; the kind of inode stays what <paramref name="mode" /> says.</para>
  /// </remarks>
  internal static Key InodeKey(ulong inode, ulong parent, ulong parentOffset,
      ulong mode, ulong size, ulong sectors, int links, uint subvolume = 0,
      ArchiveEntryMetadata? metadata = null) {
    if (metadata?.UnixMode is { } permissions)
      mode = (mode & 0xF000) | ((ulong)permissions & 0x1FF);

    // In the order the format lists them; a trailing run of zeroes is not written.
    (ulong Value, bool Wide)[] fields = [
      (ToBcacheTime(metadata?.LastAccessTimeUtc), true),    // bi_atime
      (ToBcacheTime(metadata?.StatusChangeTimeUtc), true),  // bi_ctime
      (ToBcacheTime(metadata?.LastWriteTimeUtc), true),     // bi_mtime
      (ToBcacheTime(metadata?.CreationTimeUtc), true),      // bi_otime
      (metadata?.UnixUserId ?? 0, false),                   // bi_uid
      (metadata?.UnixGroupId ?? 0, false),                  // bi_gid
      // The stored count is the link count less what the kind of inode always has:
      // one for a file, two for a directory.
      ((ulong)links, false),      // bi_nlink, biased
      (0, false),                 // bi_generation
      (0, false),                 // bi_dev
      (0, false),                 // bi_data_checksum
      (0, false),                 // bi_compression
      (0, false),                 // bi_project
      (0, false),                 // bi_background_compression
      (0, false),                 // bi_data_replicas
      (0, false),                 // bi_promote_target
      (0, false),                 // bi_foreground_target
      (0, false),                 // bi_background_target
      (0, false),                 // bi_erasure_code
      (0, false),                 // bi_fields_set
      (parent, false),            // bi_dir
      // Where in that directory the entry naming this inode sits — the same hash
      // the entry is keyed by.
      (parentOffset, false),      // bi_dir_offset
      // Which subvolume this inode is the root of, for the one inode that is.
      (subvolume, false),         // bi_subvol
    ];

    var present = fields.Length;
    while (present > 0 && fields[present - 1].Value == 0) --present;

    var buffer = new byte[256];
    var cursor = 0;
    for (var i = 0; i < present; ++i) {
      cursor += WriteVarint(buffer.AsSpan(cursor), fields[i].Value);
      if (fields[i].Wide) buffer[cursor++] = 0;
    }

    var value = new byte[48 + cursor];
    BinaryPrimitives.WriteUInt64LittleEndian(value, 0);                       // bi_journal_seq
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), HashSeed(inode));
    // The flags word carries the string-hash choice, how many fields follow, where
    // they start, and the mode.
    var flags = ((ulong)InodeStrHashSiphash << 20)
      | ((ulong)present << 24)
      | (6UL << 31)                                                          // fields start, in words
      | (mode << 36);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), flags);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), sectors);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), size);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40), 0);            // bi_version
    buffer.AsSpan(0, cursor).CopyTo(value.AsSpan(48));

    return new Key(KeyInodeV3, new Bpos(0, inode, SnapshotIdMax), 0, value);
  }

  /// <summary>
  /// A time in the units an inode stores it in: this writer's superblock says one
  /// unit is 100 ns from a time base of zero, so a unit is exactly one .NET tick
  /// since the Unix epoch, and a time before it is negative.
  /// </summary>
  internal static ulong ToBcacheTime(DateTimeOffset? time) {
    if (time is not { } value) return 0;
    var ticks = checked(value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks);
    return unchecked((ulong)ticks);
  }

  /// <summary>
  /// The hash an inode says its directory entries were placed by.
  /// </summary>
  /// <remarks>
  /// This is not the same number as the volume-wide option of the same name. The
  /// option asks for "siphash" and is two; what an inode records is which siphash,
  /// and the one whose key is the seed itself — rather than a digest of it — is
  /// three. Writing the option's number into the inode asks for the older hash and
  /// puts every name at an offset the kernel does not look at.
  /// </remarks>
  private const int InodeStrHashSiphash = 3;

  internal static Key DirentKey(ulong directory, string name, ulong target, byte type)
    => DirentKey(directory, DirentHash(HashSeed(directory), name), name, target, type);

  internal static Key DirentKey(ulong directory, ulong offset, string name, ulong target, byte type) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    // The value is the target, a type byte, and the name — padded out to a whole
    // number of words, which is also what tells a reader where the name ends.
    var length = 9 + nameBytes.Length;
    var value = new byte[(length + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt64LittleEndian(value, target);
    value[8] = type;
    nameBytes.CopyTo(value.AsSpan(9));
    return new Key(KeyDirent, new Bpos(directory, offset, SnapshotIdMax), 0, value);
  }

  private static Key ExtentKey(PlannedFile file, long firstSector, int sectors) {
    var value = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32(sectors, 0));
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), ExtentPointer(file.FirstSector + firstSector));

    // An extent is keyed by where it ends, not where it starts.
    return new Key(KeyExtent, new Bpos(file.Inode, (ulong)(firstSector + sectors), SnapshotIdMax),
      (uint)sectors, value);
  }

  private static Key SubvolumeKey() {
    var value = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(value, 0);                        // flags
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), SnapshotIdMax);  // snapshot
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), RootInode);
    // parent, pad and otime stay zero: this subvolume is not a snapshot of another.
    return new Key(KeySubvolume, new Bpos(0, RootSubvolume, 0), 0, value);
  }

  private static Key SnapshotKey() {
    var value = new byte[40];
    // The flag that says a subvolume points at this snapshot. Naming the subvolume
    // in the field below without setting it says the two disagree.
    const uint pointedAtBySubvolume = 1u << 1;
    BinaryPrimitives.WriteUInt32LittleEndian(value, pointedAtBySubvolume);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), RootSubvolume);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), 1);             // tree
    return new Key(KeySnapshot, new Bpos(0, SnapshotIdMax, 0), 0, value);
  }

  private static Key SnapshotTreeKey() {
    var value = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(value, RootSubvolume);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), SnapshotIdMax);
    return new Key(KeySnapshotTree, new Bpos(0, 1, 0), 0, value);
  }
}
