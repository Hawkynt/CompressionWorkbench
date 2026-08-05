#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
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
/// and never mounted for writing in between, the roots go in the superblock's
/// clean section, and no journal entries are needed to find them.</para>
///
/// <para>What is not written is the allocation information — the alloc, freespace,
/// backpointer and accounting trees a running filesystem keeps so it can decide
/// where to put the next write. A volume that will only ever be read does not need
/// them, and the format has a feature bit that says so; bcachefs's own tooling
/// strips exactly these trees for exactly this case. The consequence is visible
/// and worth stating: such a volume is mounted read-only, with
/// <c>-o norecovery</c>, and a read-write mount rebuilds what is missing before it
/// will start.</para>
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
  private const int JournalFirstBucket = 33;

  private const int JournalBuckets = 16;

  private const int FirstMetadataBucket = JournalFirstBucket + JournalBuckets;

  /// <summary>Largest extent one key describes: the size field is seven bits of sectors.</summary>
  private const int MaxExtentSectors = 128;

  private readonly List<(string Name, FilePayload Payload)> _files = [];
  private string _label = "cwb-bcachefs";
  private long _imageSize = MinImageSize;
  private Guid _internalUuid = Guid.NewGuid();
  private Guid _userUuid = Guid.NewGuid();
  private ulong _seed = 0x9E3779B97F4A7C15UL;

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

  /// <summary>Adds a file, held in memory.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>Adds a file whose bytes are read as the volume is written.</summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>
  /// The smallest volume that holds <paramref name="fileSizes" />: the superblock
  /// slots, the journal, one bucket per b-tree, the file data, and the slot at the
  /// tail.
  /// </summary>
  public static long EstimateSize(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var sizes = fileSizes as IReadOnlyCollection<long> ?? [.. fileSizes];

    var buckets = (long)FirstMetadataBucket + BtreeCount;
    foreach (var size in sizes)
      buckets += (size + BucketBytes - 1) / BucketBytes;

    // The tail superblock slot, plus a bucket of slack so the slot never lands on
    // the last file.
    var bytes = (buckets + 1) * BucketBytes + (long)SbSlotSectors * SectorSize;
    return Math.Max(MinImageSize, (bytes + (1L << 20) - 1) & ~((1L << 20) - 1));
  }

  /// <summary>The trees a volume written here carries.</summary>
  private static readonly int[] Btrees = [
    BtreeExtents, BtreeInodes, BtreeDirents,
    BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees, BtreeLoggedOps,
  ];

  private const int BtreeCount = 7;

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

    var roots = new List<(int Btree, Key Pointer)>(Btrees.Length);
    var node = new byte[BucketBytes];
    for (var i = 0; i < Btrees.Length; ++i) {
      var builder = plan.Nodes[i];
      Array.Clear(node);
      var sectors = builder.Write(node);
      var sector = (long)(FirstMetadataBucket + i) * BucketSectors;
      output.Position = sector * SectorSize;
      output.Write(node, 0, sectors * SectorSize);
      roots.Add((Btrees[i], builder.Pointer(sector, sectors)));
    }

    // ── The superblock, and its copies ────────────────────────────────────
    var superblock = this.BuildSuperblock(plan, roots);
    foreach (var slot in plan.SuperblockSectors) {
      BinaryPrimitives.WriteUInt64LittleEndian(superblock.AsSpan(104), (ulong)slot);
      StampSuperblockChecksum(superblock);
      output.Position = slot * SectorSize;
      output.Write(superblock, 0, superblock.Length);
    }

    // The layout is repeated on its own, ahead of the first superblock.
    output.Position = LayoutSector * SectorSize;
    output.Write(superblock, SbLayoutOffset, SbLayoutBytes);
    output.Flush();
  }

  // ── Planning ────────────────────────────────────────────────────────────

  private sealed class PlannedFile {
    internal required string Name { get; init; }
    internal required ulong Inode { get; init; }
    internal required ulong Parent { get; init; }
    internal required long Length { get; init; }
    internal required FilePayload Payload { get; init; }
    internal long FirstSector { get; set; }
  }

  private sealed class PlannedDirectory {
    internal required string Name { get; init; }
    internal required ulong Inode { get; init; }
    internal required ulong Parent { get; init; }
  }

  private sealed class Plan {
    internal required BcacheFsNodeBuilder[] Nodes { get; init; }
    internal required List<PlannedFile> Files { get; init; }
    internal required long[] SuperblockSectors { get; init; }
    internal required long Buckets { get; init; }
  }

  private ulong NextSeed() {
    // A node's identity only has to be unique and repeatable; this is the
    // splitmix step, which is both.
    this._seed += 0x9E3779B97F4A7C15UL;
    var z = this._seed;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    return z ^ (z >> 31);
  }

  /// <summary>
  /// Writes every file's bytes and adds the extents that name them.
  /// </summary>
  /// <remarks>
  /// An extent carries a checksum of the whole sectors it covers, tail padding
  /// included, so the bytes are hashed on their way to the volume rather than read
  /// back afterwards — a file that is streamed in is only ever read once.
  /// </remarks>
  private static void WriteFileData(Stream output, Plan plan) {
    var extents = plan.Nodes[0];
    var buffer = new byte[MaxExtentSectors * SectorSize];

    foreach (var file in plan.Files) {
      if (file.Length == 0) continue;

      output.Position = file.FirstSector * SectorSize;
      using var source = file.Payload.Open();

      var remaining = file.Length;
      var placed = 0L;
      while (remaining > 0) {
        var want = (int)Math.Min(buffer.Length, remaining);
        var got = 0;
        while (got < want) {
          var n = source.Read(buffer, got, want - got);
          if (n <= 0) break;
          got += n;
        }
        if (got <= 0) break;

        // The tail of the last extent is padding, and the checksum covers it.
        var sectors = (got + SectorSize - 1) / SectorSize;
        Array.Clear(buffer, got, sectors * SectorSize - got);
        output.Write(buffer, 0, sectors * SectorSize);

        extents.Add(ExtentKey(file, placed, sectors,
          DataChecksum(buffer.AsSpan(0, sectors * SectorSize))));
        placed += sectors;
        remaining -= got;
      }
    }
  }

  private Plan BuildPlan() {
    var directories = new Dictionary<string, PlannedDirectory>(StringComparer.Ordinal);
    var files = new List<PlannedFile>();
    var nextInode = 2147483648UL;

    ulong DirectoryInode(string path, ulong parent, string leaf) {
      if (directories.TryGetValue(path, out var existing)) return existing.Inode;
      var inode = nextInode++;
      directories[path] = new PlannedDirectory { Name = leaf, Inode = inode, Parent = parent };
      return inode;
    }

    foreach (var (rawName, payload) in this._files) {
      var parts = rawName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;

      var parent = RootInode;
      var accumulated = string.Empty;
      for (var i = 0; i < parts.Length - 1; ++i) {
        accumulated = accumulated.Length == 0 ? parts[i] : accumulated + "/" + parts[i];
        parent = DirectoryInode(accumulated, parent, parts[i]);
      }

      files.Add(new PlannedFile {
        Name = parts[^1],
        Inode = nextInode++,
        Parent = parent,
        Length = payload.Size,
        Payload = payload,
      });
    }

    // ── Where everything goes ─────────────────────────────────────────────
    var bucket = (long)FirstMetadataBucket + BtreeCount;
    foreach (var file in files) {
      file.FirstSector = bucket * BucketSectors;
      bucket += (file.Length + BucketBytes - 1) / BucketBytes;
    }

    var needed = (bucket + 1) * BucketBytes + (long)SbSlotSectors * SectorSize;
    if (this._imageSize < needed)
      this._imageSize = Math.Max(MinImageSize, (needed + (1L << 20) - 1) & ~((1L << 20) - 1));

    var deviceSectors = this._imageSize / SectorSize;
    var buckets = deviceSectors / BucketSectors;

    // ── The trees ─────────────────────────────────────────────────────────
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(this._internalUuid.ToByteArray());
    var nodes = new BcacheFsNodeBuilder[Btrees.Length];
    for (var i = 0; i < Btrees.Length; ++i)
      nodes[i] = new BcacheFsNodeBuilder {
        BtreeId = Btrees[i], Seq = this.NextSeed(), SuperblockMagic = magic,
      };

    var inodes = nodes[1];
    var dirents = nodes[2];

    // The root directory, then every directory under it, then every file.
    inodes.Add(InodeKey(RootInode, 0, 0, isDirectory: true, size: 0, sectors: 0,
      links: directories.Values.Count(d => d.Parent == RootInode)));

    foreach (var directory in directories.Values) {
      var offset = DirentHash(HashSeed(directory.Parent), directory.Name);
      inodes.Add(InodeKey(directory.Inode, directory.Parent, offset, isDirectory: true,
        size: 0, sectors: 0,
        links: directories.Values.Count(d => d.Parent == directory.Inode)));
      dirents.Add(DirentKey(directory.Parent, directory.Name, directory.Inode, DtDir));
    }

    foreach (var file in files) {
      var sectors = (file.Length + SectorSize - 1) / SectorSize;
      var offset = DirentHash(HashSeed(file.Parent), file.Name);
      inodes.Add(InodeKey(file.Inode, file.Parent, offset, isDirectory: false,
        size: (ulong)file.Length, sectors: (ulong)sectors, links: 0));
      dirents.Add(DirentKey(file.Parent, file.Name, file.Inode, DtReg));

    }

    // The one subvolume, the one snapshot, and the tree that ties them together.
    nodes[3].Add(SubvolumeKey());
    nodes[4].Add(SnapshotKey());
    nodes[5].Add(SnapshotTreeKey());
    nodes[6].Add(InodeAllocCursorKey(nextInode));

    return new Plan {
      Nodes = nodes,
      Files = files,
      Buckets = buckets,
      SuperblockSectors = [
        PrimarySbSector,
        PrimarySbSector + SbSlotSectors,
        deviceSectors - SbSlotSectors,
      ],
    };
  }

  // ── Keys ────────────────────────────────────────────────────────────────

  /// <summary>The hash seed a directory's entries are placed by.</summary>
  private static ulong HashSeed(ulong inode) => inode * 0x9E3779B97F4A7C15UL | 1UL;

  private static Key InodeKey(ulong inode, ulong parent, ulong parentOffset,
      bool isDirectory, ulong size, ulong sectors, int links) {
    // The fixed part: journal sequence, hash seed, flags, sectors, size, version.
    var fields = new byte[256];
    var cursor = 0;

    // The field list is positional — every field up to the last non-zero one is
    // present, a zero field being a single zero byte.
    void Field(ulong value, bool wide = false) {
      cursor += WriteVarint(fields.AsSpan(cursor), value);
      if (wide) fields[cursor++] = 0;   // the high half of a 96-bit time
    }

    Field(0, wide: true);                       // bi_atime
    Field(0, wide: true);                       // bi_ctime
    Field(0, wide: true);                       // bi_mtime
    Field(0, wide: true);                       // bi_otime
    Field(0);                                   // bi_uid
    Field(0);                                   // bi_gid
    // The stored count is the link count less what the kind of inode always has:
    // one for a file, two for a directory. Storing the count itself gives every
    // file a link it does not have.
    Field((ulong)links);                        // bi_nlink, biased
    Field(0);                                   // bi_generation
    Field(0);                                   // bi_dev
    Field(0);                                   // bi_data_checksum
    Field(0);                                   // bi_compression
    Field(0);                                   // bi_project
    Field(0);                                   // bi_background_compression
    Field(0);                                   // bi_data_replicas
    Field(0);                                   // bi_promote_target
    Field(0);                                   // bi_foreground_target
    Field(0);                                   // bi_background_target
    Field(0);                                   // bi_erasure_code
    Field(0);                                   // bi_fields_set
    Field(parent);                              // bi_dir
    // Where in that directory the entry naming this inode sits — the same hash the
    // entry is keyed by. A zero here is a back pointer to nowhere, and the kernel
    // says so of every file on the volume.
    Field(parentOffset);                        // bi_dir_offset
    const int fieldsPresent = 21;

    var value = new byte[48 + cursor];
    BinaryPrimitives.WriteUInt64LittleEndian(value, 0);                       // bi_journal_seq
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), HashSeed(inode));
    var mode = (ulong)(isDirectory ? 0x41ED : 0x81A4);                        // 040755 / 0100644
    // The flags word carries the string-hash choice, how many fields follow, where
    // they start, and the mode.
    var flags = ((ulong)InodeStrHashSiphash << 20)
      | ((ulong)fieldsPresent << 24)
      | (6UL << 31)                                                          // fields start, in words
      | (mode << 36);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), flags);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), sectors);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), size);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40), 0);            // bi_version
    fields.AsSpan(0, cursor).CopyTo(value.AsSpan(48));

    return new Key(KeyInodeV3, new Bpos(0, inode, SnapshotIdMax), 0, value);
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

  /// <summary>The volume-wide option that asks for that hash.</summary>
  private const int BchStrHashSiphash = 2;

  /// <summary>The checksum option a volume written here asks for.</summary>
  private const int ChecksumOptionCrc32C = 1;

  private static Key DirentKey(ulong directory, string name, ulong target, byte type) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    // The value is the target, a type byte, and the name — padded out to a whole
    // number of words, which is also what tells a reader where the name ends.
    var length = 9 + nameBytes.Length;
    var value = new byte[(length + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt64LittleEndian(value, target);
    value[8] = type;
    nameBytes.CopyTo(value.AsSpan(9));

    var offset = DirentHash(HashSeed(directory), name);
    return new Key(KeyDirent, new Bpos(directory, offset, SnapshotIdMax), 0, value);
  }

  private static Key ExtentKey(PlannedFile file, long firstSector, int sectors, uint checksum) {
    var value = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32(sectors, checksum));
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8),
      ExtentPointer(file.FirstSector + firstSector));

    // An extent is keyed by where it ends, not where it starts.
    return new Key(KeyExtent, new Bpos(file.Inode, (ulong)(firstSector + sectors), SnapshotIdMax),
      (uint)sectors, value);
  }

  private static Key SubvolumeKey() {
    var value = new byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(value, 0);                        // flags
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), SnapshotIdMax);  // snapshot
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), RootInode);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), 0);             // creation_parent
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), 0);             // fs_path_parent
    return new Key(KeySubvolume, new Bpos(0, RootSubvolume, 0), 0, value);
  }

  private static Key SnapshotKey() {
    var value = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(value, 0);                        // flags
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 0);              // parent
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), 0);              // children[0]
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(12), 0);             // children[1]
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), RootSubvolume);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), 1);             // tree
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(24), 0);             // depth
    return new Key(KeySnapshot, new Bpos(0, SnapshotIdMax, 0), 0, value);
  }

  private static Key SnapshotTreeKey() {
    var value = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(value, RootSubvolume);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), SnapshotIdMax);
    return new Key(KeySnapshotTree, new Bpos(0, 1, 0), 0, value);
  }

  private static Key InodeAllocCursorKey(ulong next) {
    var value = new byte[24];
    BinaryPrimitives.WriteUInt64LittleEndian(value, 2147483648UL);             // min
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), long.MaxValue);  // max
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), next);          // idx
    return new Key(KeyInodeAllocCursor, new Bpos(1, 1, 0), 0, value);
  }

  // ── Superblock ──────────────────────────────────────────────────────────

  private byte[] BuildSuperblock(Plan plan, List<(int Btree, Key Pointer)> roots) {
    var sections = new List<byte[]> {
      this.MembersSection(plan),
      JournalSection(),
      CleanSection(roots),
      ExtSection(),
      ErrorsSection(),
    };

    var variable = sections.Sum(s => s.Length);
    var sb = new byte[SbFixedBytes + variable];
    var span = sb.AsSpan();

    BinaryPrimitives.WriteUInt16LittleEndian(span[16..], BcacheFsFormat.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(span[18..], VersionMin);
    Magic.CopyTo(span[24..]);
    WriteGuid(span[40..], this._internalUuid);
    WriteGuid(span[56..], this._userUuid);
    var label = Encoding.ASCII.GetBytes(this._label);
    label.AsSpan(0, Math.Min(31, label.Length)).CopyTo(span[72..]);
    BinaryPrimitives.WriteUInt64LittleEndian(span[112..], 1);                  // seq
    BinaryPrimitives.WriteUInt16LittleEndian(span[120..], 1);                  // block_size, in sectors
    sb[122] = 0;                                                               // dev_idx
    sb[123] = 1;                                                               // nr_devices
    BinaryPrimitives.WriteUInt32LittleEndian(span[124..], (uint)(variable / 8));
    BinaryPrimitives.WriteUInt32LittleEndian(span[140..], 1);                  // time_precision

    WriteFlags(span[144..200]);
    BinaryPrimitives.WriteUInt64LittleEndian(span[208..],
      FeatureNewSiphash | FeatureNewExtentOverwrite | FeatureBtreePtrV2
      | FeatureExtentsAboveBtreeUpdates | FeatureBtreeUpdatesJournalled | FeatureNewVarint
      | FeatureJournalNoFlush | FeatureAllocV2 | FeatureExtentsAcrossBtreeNodes
      | FeatureIncompatVersionField | FeatureNoAllocInfo);
    BinaryPrimitives.WriteUInt64LittleEndian(span[224..],
      CompatAllocInfo | CompatAllocMetadata | CompatExtentsAboveBtreeUpdatesDone
      | CompatBformatOverflowDone | CompatNoStalePtrs);

    // The layout: where every copy of this superblock is.
    var layout = span[SbLayoutOffset..];
    Magic.CopyTo(layout);
    layout[16] = 0;                                                            // layout_type
    layout[17] = SbMaxSizeBits;
    layout[18] = (byte)plan.SuperblockSectors.Length;
    for (var i = 0; i < plan.SuperblockSectors.Length; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(layout[(24 + 8 * i)..], (ulong)plan.SuperblockSectors[i]);

    var cursor = SbFixedBytes;
    foreach (var section in sections) {
      section.CopyTo(sb, cursor);
      cursor += section.Length;
    }

    return sb;
  }

  private static void WriteFlags(Span<byte> flags) {
    var words = new ulong[7];

    void Set(int word, int lo, int hi, ulong value)
      => words[word] |= (value & ((1UL << (hi - lo)) - 1)) << lo;

    Set(0, 0, 1, 1);                     // initialized
    Set(0, 1, 2, 1);                     // clean
    Set(0, 2, 8, CsumTypeCrc32CNonzero); // superblock checksum
    Set(0, 12, 28, BucketSectors);       // btree node size, in sectors
    Set(0, 28, 33, 8);                   // gc reserve, per cent
    // These two name a choice, not a function: "crc32c" is one option, and which
    // of the two crc32c variants it becomes depends on whether the block being
    // summed is metadata or data.
    Set(0, 40, 44, ChecksumOptionCrc32C);
    Set(0, 44, 48, ChecksumOptionCrc32C);
    Set(0, 48, 52, 1);                   // metadata replicas wanted
    Set(0, 52, 56, 1);                   // data replicas wanted

    Set(1, 0, 4, BchStrHashSiphash);
    Set(1, 14, 20, 9);                   // encoded extent max, as a power of two sectors
    Set(1, 20, 24, 1);                   // metadata replicas required
    Set(1, 24, 28, 1);                   // data replicas required

    Set(5, 0, 16, BcacheFsFormat.Version); // version upgrade complete

    Set(6, 4, 14, 30);                   // write error timeout
    Set(6, 14, 20, 3);                   // checksum error retries

    for (var i = 0; i < 7; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(flags[(8 * i)..], words[i]);
  }

  private static byte[] Section(uint type, int payloadBytes) {
    var section = new byte[8 + (payloadBytes + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(section, (uint)(section.Length / 8));
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), type);
    return section;
  }

  private byte[] MembersSection(Plan plan) {
    var section = Section(FieldMembersV2, 8 + MemberBytes);
    BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(8), MemberBytes);

    var member = section.AsSpan(16);
    WriteGuid(member, this._internalUuid);
    BinaryPrimitives.WriteUInt64LittleEndian(member[16..], (ulong)plan.Buckets);
    BinaryPrimitives.WriteUInt16LittleEndian(member[24..], 0);                 // first_bucket
    BinaryPrimitives.WriteUInt16LittleEndian(member[26..], BucketSectors);
    // The member's flags: readwrite, everything allowed on it, one copy of each
    // thing that lands there.
    var flags = (1UL << 14)              // discard
      | (28UL << 15)                     // journal, btree and user data allowed
      | (1UL << 28)                      // durability
      | (1UL << 32) | (1UL << 33);       // rotational, and that we know it
    BinaryPrimitives.WriteUInt64LittleEndian(member[40..], flags);
    BinaryPrimitives.WriteUInt64LittleEndian(member[112..], 1);                // seq
    return section;
  }

  private static byte[] JournalSection() {
    // One range of buckets, given as a start and a count.
    var section = Section(FieldJournalV2, 16);
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(8), JournalFirstBucket);
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16), JournalBuckets);
    return section;
  }

  private static byte[] CleanSection(List<(int Btree, Key Pointer)> roots) {
    // Two clock entries, then one entry per b-tree root.
    var entries = new List<byte[]>();

    for (var clock = 0; clock < 2; ++clock) {
      // A clock entry is the header, a byte saying which clock, and the time.
      var entry = new byte[24];
      BinaryPrimitives.WriteUInt16LittleEndian(entry, 2);                      // u64s past the header
      entry[4] = 7;                                                            // type: clock
      entry[8] = (byte)clock;                                                  // read or write
      entries.Add(entry);
    }

    foreach (var (btree, pointer) in roots) {
      var entry = new byte[8 + pointer.Bytes];
      BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)(pointer.Bytes / 8));
      entry[2] = (byte)btree;
      entry[3] = 0;                                                            // level
      entry[4] = 1;                                                            // type: btree root
      WriteKey(entry.AsSpan(8), pointer);
      entries.Add(entry);
    }

    // The section's own head — flags, the two clocks that are no longer read, and
    // the journal sequence — comes before the entries.
    var payload = 16 + entries.Sum(e => e.Length);
    var section = Section(FieldClean, payload);
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(8), 0);            // flags
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16), 1);           // journal_seq

    var cursor = 24;
    foreach (var entry in entries) {
      entry.CopyTo(section, cursor);
      cursor += entry.Length;
    }

    return section;
  }

  private static byte[] ExtSection() => Section(FieldExt, 96);

  private static byte[] ErrorsSection() => Section(FieldErrors, 0);

  private static void StampSuperblockChecksum(byte[] superblock) {
    var checksum = MetadataChecksum(superblock.AsSpan(16));
    BinaryPrimitives.WriteUInt64LittleEndian(superblock, checksum);
    BinaryPrimitives.WriteUInt64LittleEndian(superblock.AsSpan(8), 0);
  }

  private static void WriteGuid(Span<byte> destination, Guid value) {
    Span<byte> bytes = stackalloc byte[16];
    value.TryWriteBytes(bytes);
    bytes.CopyTo(destination);
  }
}
