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
/// <para>The allocation information is written too: the alloc tree says what each
/// bucket holds and how much of it is used, the bucket_gens tree gives every
/// bucket's generation, and the freespace tree covers the runs of buckets nothing
/// has been laid into. What each bucket holds has to agree with what the extents
/// say, and the count of b-tree buckets feeds itself — those keys are themselves
/// keys, so adding them can want another node — which is why the description is
/// settled by repetition rather than worked out once.</para>
///
/// <para>It used to be left out, and the volume claimed <c>no_alloc_info</c> and
/// <c>small_image</c> to be let past the check that would have built it. No
/// formatter sets either, so the volume could be told from one by the bits alone,
/// and a read-write mount was refused outright. Neither is claimed now.</para>
///
/// <para>The accounting tree carries the totals: how many inodes there are, and
/// per kind of content how many buckets, how many live sectors and how many
/// sectors sit unused inside used buckets. They come off the same walk as the
/// alloc keys, because they are the same facts added up, and counting them
/// separately is how the two come to disagree.</para>
///
/// <para>Accounting is the one part here the checker will not confirm. A volume
/// carrying wrong totals passes <c>fsck</c> exactly as one carrying right ones
/// does — tested, not assumed — so the numbers are held instead to a filesystem
/// <c>mkfs.bcachefs</c> made and the kernel initialised. Against that, the
/// superblock and journal rows match to the sector.</para>
///
/// <para>The backpointers tree points the other way: for each b-tree node, from
/// the space it occupies back to the tree that holds it. Those keys have to know
/// where each node landed, which is decided by the same rule the write pass
/// follows — trees in order, each taking as many consecutive buckets as it has
/// nodes — so the rule is applied here rather than the assignment being threaded
/// out of the writer.</para>
///
/// <para>Accounting also carries what each tree costs and what each snapshot
/// holds in it. Both are read off the trees themselves, so they cannot come to
/// describe a volume other than the one being written, and the accounting tree
/// measures itself among them.</para>
///
/// <para>The LRU tree stays empty, which is what a filesystem
/// <c>mkfs.bcachefs</c> made and the kernel initialised also has, so there is
/// nothing there to write.</para>
///
/// <para>The replicas counters say how many sectors of each kind of content
/// there are per set of devices holding a copy of it, and the superblock's
/// replicas section declares those sets. The two go together: a counter naming
/// a set the section does not declare is refused, and the volume with it. See
/// <c>docs/BCACHEFS-ACCOUNTING.md</c>.</para>
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

  /// <summary>
  /// Buckets set aside for the b-trees, whatever shape they turn out to be.
  /// </summary>
  /// <remarks>
  /// A fixed reservation rather than a count worked out from the files, because
  /// where a volume's own structures end has to be knowable from the volume rather
  /// than from what was written into it — the layout pass needs it, and so does
  /// anything asking what space is free.
  /// </remarks>
  private const int MetadataBuckets = 64;

  /// <summary>Largest extent one key describes: the size field is seven bits of sectors.</summary>
  private const int MaxExtentSectors = 128;

  private readonly List<(string Name, FilePayload Payload)> _files = [];
  // Empty by default, as `bcachefs format` leaves it unless a label is asked for.
  private string _label = "";
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

    var buckets = (long)FirstMetadataBucket + MetadataBuckets;
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
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeAccounting, BtreeBackpointers,
  ];

  private const int BtreeCount = 12;

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

    var roots = new List<(int Btree, int Level, Key Pointer)>(Btrees.Length);
    var nextMetadataBucket = (long)FirstMetadataBucket;
    for (var i = 0; i < Btrees.Length; ++i) {
      var (level, pointer) = this.WriteTree(output, plan.Nodes[i], ref nextMetadataBucket);
      roots.Add((Btrees[i], level, pointer));
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


  /// <summary>
  /// Writes one b-tree and returns the pointer to its root.
  /// </summary>
  /// <remarks>
  /// A tree whose keys fit in one node is that node. A tree whose keys do not is a
  /// row of leaves, each responsible for a range of positions, under a root that
  /// holds one pointer per leaf — and the ranges have to meet exactly: a leaf ends
  /// at the last key it holds and its neighbour begins at the position after it, so
  /// that every position falls inside exactly one of them.
  /// </remarks>
  private (int Level, Key Pointer) WriteTree(Stream output, BcacheFsNodeBuilder tree, ref long nextBucket) {
    var buffer = new byte[BucketBytes];

    Key Place(BcacheFsNodeBuilder node, ref long bucket) {
      Array.Clear(buffer);
      var sectors = node.Write(buffer);
      var sector = bucket * BucketSectors;
      ++bucket;
      if (bucket > FirstMetadataBucket + MetadataBuckets)
        throw new NotSupportedException(
          $"A bcachefs volume of this many files needs more than the {MetadataBuckets} buckets "
          + "reserved for its b-trees.");

      output.Position = sector * SectorSize;
      output.Write(buffer, 0, sectors * SectorSize);
      return node.Pointer(sector, sectors);
    }

    if (tree.Bytes <= BucketBytes)
      return (0, Place(tree, ref nextBucket));

    var keys = tree.Keys.OrderBy(k => k, Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position))).ToList();
    var leaves = new List<BcacheFsNodeBuilder>();
    var root = new BcacheFsNodeBuilder {
      BtreeId = tree.BtreeId, Seq = this.NextSeed(), SuperblockMagic = tree.SuperblockMagic, Level = 1,
    };

    var index = 0;
    while (index < keys.Count) {
      var leaf = new BcacheFsNodeBuilder {
        BtreeId = tree.BtreeId, Seq = this.NextSeed(), SuperblockMagic = tree.SuperblockMagic,
        MinKey = leaves.Count == 0 ? Bpos.Min : Successor(keys[index - 1].Position),
      };

      var bytes = BcacheFsNodeBuilder.KeysOffset;
      while (index < keys.Count && bytes + keys[index].Bytes <= BucketBytes) {
        bytes += keys[index].Bytes;
        leaf.Add(keys[index]);
        ++index;
      }

      if (leaf.Count == 0)
        throw new NotSupportedException("A bcachefs key too large for one b-tree node.");

      leaves.Add(leaf);
    }

    for (var i = 0; i < leaves.Count; ++i) {
      // The last leaf carries the tree's upper bound; the rest end at their own
      // last key.
      var bounded = new BcacheFsNodeBuilder {
        BtreeId = leaves[i].BtreeId, Seq = leaves[i].Seq, SuperblockMagic = leaves[i].SuperblockMagic,
        MinKey = leaves[i].MinKey,
        MaxKey = i == leaves.Count - 1 ? Bpos.Max : leaves[i].Keys[^1].Position,
      };
      foreach (var key in leaves[i].Keys) bounded.Add(key);
      root.Add(Place(bounded, ref nextBucket));
    }

    return (1, Place(root, ref nextBucket));
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

    /// <summary>How many buckets the b-trees occupy, starting at the first of them.</summary>
    internal required long BtreeBuckets { get; init; }
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
    var bucket = (long)FirstMetadataBucket + MetadataBuckets;
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
    // The root is the subvolume's root inode, and says so.
    inodes.Add(InodeKey(RootInode, 0, 0, isDirectory: true, size: 0, sectors: 0,
      links: directories.Values.Count(d => d.Parent == RootInode),
      subvolume: RootSubvolume));

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

    // ── What each bucket holds ────────────────────────────────────────────
    // Said once here, for the alloc tree, and again as free-or-not for the
    // freespace tree. The two have to agree with each other and with the
    // extents, so both are driven off the same walk rather than written twice
    // from the same assumptions.
    var alloc = nodes[7];
    var bucketGens = nodes[8];
    var freespace = nodes[9];
    var accounting = nodes[10];
    var backpointers = nodes[11];

    var firstLastSbBucket = (deviceSectors - SbSlotSectors) / BucketSectors;
    var fileEnd = (long)FirstMetadataBucket + MetadataBuckets;
    foreach (var file in files)
      fileEnd += (file.Length + BucketBytes - 1) / BucketBytes;

    for (long b = 0; b < buckets; b += BucketGensNr)
      bucketGens.Add(BucketGensKey(b));

    // How many b-tree buckets get used depends on how many keys there are, and
    // the alloc keys are themselves keys — so the count feeds itself. Settle it
    // by counting the nodes the current keys would occupy, describing the buckets
    // on that basis, and counting again; adding or dropping alloc keys can push a
    // node over a boundary, which changes the count once and then stops.
    var btreeBuckets = (long)Btrees.Length;
    for (var attempt = 0; attempt < 8; ++attempt) {
      alloc.Clear();
      freespace.Clear();
      accounting.Clear();
      var runStart = -1L;

      // The same walk answers both questions: what each bucket holds, key by
      // key, and how much of each kind there is in total. Counting them
      // separately is how the two come to disagree.
      var bucketsOf = new ulong[DataUser + 1];
      var sectorsOf = new ulong[DataUser + 1];

      for (long b = 0; b <= buckets; ++b) {
        var free = b < buckets;
        if (free) {
          var (type, sectors) = this.BucketContents(b, files, fileEnd, firstLastSbBucket, btreeBuckets);
          free = type == DataFree;
          ++bucketsOf[type];
          sectorsOf[type] += sectors;
          if (!free) alloc.Add(AllocKey(b, type, sectors));
        }

        if (free) {
          if (runStart < 0) runStart = b;
          continue;
        }

        if (runStart >= 0) freespace.Add(FreespaceKey(runStart, b));
        runStart = -1;
      }

      accounting.Add(NrInodesKey((ulong)(1 + directories.Count + files.Count)));
      for (byte type = DataFree; type <= DataUser; ++type)
        if (bucketsOf[type] > 0)
          accounting.Add(DevDataTypeKey(type, bucketsOf[type], sectorsOf[type]));

      // One backpointer per b-tree node, which needs to know where each node
      // lands. That is not recorded anywhere yet — the nodes are placed as they
      // are written — but it is decided by the same rule the writer follows:
      // trees in order, each taking as many consecutive buckets as it has nodes,
      // leaves before the root. Mirroring it here rather than threading the
      // assignment out of the write pass keeps one description of the layout.
      backpointers.Clear();
      var at = (long)FirstMetadataBucket;
      foreach (var tree in nodes) {
        var count = NodeCount(tree);
        for (var i = 0; i < count; ++i) {
          // A tree of one node is that node; a tree of several is its leaves and
          // then its root one level above them.
          var level = count > 1 && i == count - 1 ? 1 : 0;
          backpointers.Add(NodeBackpointerKey(at + i, tree.BtreeId, level, BucketSectors));
        }
        at += count;
      }

      // What each tree costs, and what each snapshot holds in it. Both are read
      // off the trees themselves, so they cannot describe a volume other than
      // the one being written. The accounting tree measures itself here too:
      // adding these keys can grow it, which the loop settles.
      ulong btreeSectors = 0;
      foreach (var tree in nodes) {
        var count = (ulong)NodeCount(tree);
        btreeSectors += count * BucketSectors;
        accounting.Add(BtreeAccountingKey(tree.BtreeId, count));
      }

      foreach (var tree in new[] { nodes[0], nodes[1], nodes[2] }) {
        if (tree.Count == 0) continue;
        var keyBytes = (ulong)tree.Keys.Sum(k => k.Bytes);
        accounting.Add(SnapshotAccountingKey(SnapshotIdMax, tree.BtreeId,
          (ulong)tree.Count, keyBytes,
          tree.BtreeId == BtreeExtents ? sectorsOf[DataUser] : 0));
      }

      // Both of these name the one device, and both sets are declared in the
      // superblock's replicas section; a counter naming a set that is not
      // declared there is refused.
      accounting.Add(ReplicasKey(DataBtree, btreeSectors));
      if (sectorsOf[DataUser] > 0) accounting.Add(ReplicasKey(DataUser, sectorsOf[DataUser]));

      var settled = nodes.Sum(NodeCount);
      if (settled == btreeBuckets) break;
      btreeBuckets = settled;
    }

    return new Plan {
      Nodes = nodes,
      Files = files,
      Buckets = buckets,
      BtreeBuckets = btreeBuckets,
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
  /// <para>The two time fields are wider than a varint carries, so each is a varint
  /// and a zero byte for the half above it.</para>
  /// </remarks>
  private static Key InodeKey(ulong inode, ulong parent, ulong parentOffset,
      bool isDirectory, ulong size, ulong sectors, int links, uint subvolume = 0) {
    // In the order the format lists them; a trailing run of zeroes is not written.
    (ulong Value, bool Wide)[] fields = [
      (0, true),                  // bi_atime
      (0, true),                  // bi_ctime
      (0, true),                  // bi_mtime
      (0, true),                  // bi_otime
      (0, false),                 // bi_uid
      (0, false),                 // bi_gid
      // The stored count is the link count less what the kind of inode always has:
      // one for a file, two for a directory. Storing the count itself gives every
      // file a link it does not have.
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
      // the entry is keyed by. A zero here is a back pointer to nowhere, and a
      // mount says so of every file on the volume.
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
    var mode = (ulong)(isDirectory ? 0x41ED : 0x81A4);                        // 040755 / 0100644
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
    // The flag that says a subvolume points at this snapshot. Naming the subvolume
    // in the field below without setting it says the two disagree, and a mount
    // stops to reconcile them.
    const uint pointedAtBySubvolume = 1u << 1;
    BinaryPrimitives.WriteUInt32LittleEndian(value, pointedAtBySubvolume);
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

  /// <summary>How many buckets a tree will occupy once written.</summary>
  /// <remarks>
  /// The same split <see cref="WriteTree" /> performs, counted rather than laid
  /// down: a tree that fits in one node is one bucket, and a tree that does not
  /// is one bucket per leaf plus one for the root. It has to agree with that
  /// method exactly, because what it counts is what the alloc keys then claim.
  /// </remarks>
  private static int NodeCount(BcacheFsNodeBuilder tree) {
    if (tree.Bytes <= BucketBytes) return 1;

    var leaves = 0;
    var bytes = BcacheFsNodeBuilder.KeysOffset;
    var any = false;
    foreach (var key in tree.Keys.OrderBy(k => k, Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position)))) {
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

  /// <summary>Which of the volume's regions a bucket falls in, and how much of it is used.</summary>
  /// <remarks>
  /// The layout is fixed by <see cref="Plan" />: two superblock slots at the
  /// front, the journal, a fixed reservation for the b-trees, then the files in
  /// order, then a third superblock slot in the last bucket. Only the tail bucket
  /// of a file is partly used; everything else a volume written whole touches, it
  /// fills.
  /// </remarks>
  private (byte Type, uint Sectors) BucketContents(
    long bucket, List<PlannedFile> files, long fileEnd, long firstLastSbBucket, long btreeBuckets) {
    // The two front superblock slots run to sector PrimarySbSector + 2 * SbSlotSectors,
    // which is not a bucket boundary: the bucket it ends in is partly used, and
    // saying it is wholly used is as wrong as saying it is free.
    var sbEndSector = PrimarySbSector + 2L * SbSlotSectors;
    if (bucket < sbEndSector / BucketSectors) return (DataSb, BucketSectors);
    if (bucket == sbEndSector / BucketSectors) return (DataSb, (uint)(sbEndSector % BucketSectors));

    // The trailing slot is a whole slot, so it spans several buckets, not one.
    if (bucket >= firstLastSbBucket) return (DataSb, BucketSectors);

    if (bucket < FirstMetadataBucket) return (DataJournal, BucketSectors);

    var firstDataBucket = (long)FirstMetadataBucket + MetadataBuckets;
    // Only the b-tree buckets actually laid into are b-tree buckets; the rest of
    // the reservation is free, and claiming otherwise is a discrepancy the
    // checker finds by walking the trees it can see.
    if (bucket < FirstMetadataBucket + btreeBuckets) return (DataBtree, BucketSectors);
    if (bucket < firstDataBucket) return (DataFree, 0);
    if (bucket >= fileEnd) return (DataFree, 0);

    // Inside the files: find the one this bucket belongs to, and give the tail
    // bucket only the sectors the file actually reaches into it.
    var at = firstDataBucket;
    foreach (var file in files) {
      var span = (file.Length + BucketBytes - 1) / BucketBytes;
      if (bucket < at + span) {
        var into = (bucket - at) * BucketBytes;
        var remaining = file.Length - into;
        var sectors = (remaining + SectorSize - 1) / SectorSize;
        return (DataUser, (uint)Math.Min(sectors, BucketSectors));
      }
      at += span;
    }
    return (DataFree, 0);
  }

  /// <summary>
  /// What one bucket holds, as the alloc tree records it.
  /// </summary>
  /// <remarks>
  /// The value is a <c>bch_alloc_v4</c>: forty-eight bytes, of which a volume
  /// written whole needs only the generation, the data type and the sector count.
  /// A bucket written once has never been reused, so its generation is zero and
  /// its oldest generation is zero with it, and neither io_time matters because
  /// nothing has read or rewritten it. A full bucket carries the whole bucket in
  /// dirty sectors; the tail bucket of a file carries only what the file reaches
  /// into it, because the checker adds these up and compares the total against
  /// what the extents say.
  /// </remarks>
  private static Key AllocKey(long bucket, byte dataType, uint dirtySectors) {
    var value = new byte[48];
    // journal_seq_nonempty and flags stay zero: nothing here waits on a flush,
    // and a bucket that was never emptied needs neither discard nor a new gen.
    value[12] = 0;                                                             // generation
    value[13] = 0;                                                             // oldest_gen
    value[14] = dataType;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), dirtySectors);
    // cached_sectors, io_time, stripe and backpointer counts stay zero.
    return new Key(KeyAllocV4, new Bpos(0, (ulong)bucket, 0), 0, value);
  }

  /// <summary>The generations of one run of 256 buckets.</summary>
  /// <remarks>
  /// Every bucket here is on its first use, so every generation is zero and the
  /// key is a run of zero bytes. It is written even so: the tree is what the
  /// checker walks to learn a bucket's generation, and a missing key does not
  /// <summary>
  /// An accounting key: a counter, positioned by what it counts.
  /// </summary>
  /// <remarks>
  /// <para>The position is not an ordinary triple. It is a
  /// <c>disk_accounting_pos</c> — a type-tagged union laid over the twenty bytes
  /// of a position and treated as one twenty-byte integer, so that every key of
  /// a type sorts together. The overlay is byte-reversed against the struct: the
  /// type tag is the struct's first byte and lands in the most significant byte
  /// of the inode field, while multi-byte fields inside the struct keep their
  /// native little-endian order.</para>
  ///
  /// <para>Which is why this takes the struct's bytes and folds them, rather
  /// than writing the fields into a position directly. See
  /// <c>docs/BCACHEFS-ACCOUNTING.md</c> for how the layout was read off a real
  /// filesystem.</para>
  /// </remarks>
  private static Key AccountingKey(ReadOnlySpan<byte> position, params ulong[] counters) {
    Span<byte> s = stackalloc byte[20];
    s.Clear();
    position.CopyTo(s);

    var inode = BinaryPrimitives.ReadUInt64BigEndian(s);
    var offset = BinaryPrimitives.ReadUInt64BigEndian(s[8..]);
    var snapshot = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);

    var value = new byte[8 * counters.Length];
    for (var i = 0; i < counters.Length; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8 * i), counters[i]);
    return new Key(KeyAccounting, new Bpos(inode, offset, snapshot), 0, value);
  }

  /// <summary>How many inodes the volume holds.</summary>
  private static Key NrInodesKey(ulong inodes) => AccountingKey([AccountingNrInodes], inodes);

  /// <summary>
  /// How many sectors of one kind of content there are, per set of devices
  /// holding a copy of it.
  /// </summary>
  /// <remarks>
  /// The position is a <c>bch_replicas_entry_v1</c> — what the content is, how
  /// many devices carry it, how many are needed, and which. One device here, so
  /// the list is a single zero. Every set named by one of these has to be
  /// declared in the superblock's replicas section as well, or the checker
  /// refuses the volume; see <see cref="ReplicasV0Section" />.
  /// </remarks>
  private static Key ReplicasKey(byte dataType, ulong sectors) =>
    AccountingKey([AccountingReplicas, dataType, 1, 1, 0], sectors);

  /// <summary>What one b-tree costs: its sectors, its nodes, and its inner nodes.</summary>
  private static Key BtreeAccountingKey(int btreeId, ulong nodes) {
    Span<byte> position = stackalloc byte[5];
    position[0] = AccountingBtree;
    BinaryPrimitives.WriteUInt32LittleEndian(position[1..], (uint)btreeId);
    // A tree of one node is that node and nothing above it; a tree of several
    // carries one root over its leaves, which is the only non-leaf here.
    return AccountingKey(position, nodes * BucketSectors, nodes, nodes > 1 ? 1UL : 0);
  }

  /// <summary>
  /// What one snapshot holds in one tree: how many keys, how many bytes of key,
  /// and how many sectors of data outside the tree they name.
  /// </summary>
  /// <remarks>
  /// The last is only ever nonzero for the extents tree, because only an extent
  /// names bytes that live somewhere else.
  /// </remarks>
  private static Key SnapshotAccountingKey(uint snapshot, int btreeId,
    ulong keys, ulong keyBytes, ulong externalSectors) {
    Span<byte> position = stackalloc byte[9];
    position[0] = AccountingSnapshot;
    BinaryPrimitives.WriteUInt32LittleEndian(position[1..], snapshot);
    BinaryPrimitives.WriteUInt32LittleEndian(position[5..], (uint)btreeId);
    return AccountingKey(position, keys, keyBytes, externalSectors);
  }

  /// <summary>
  /// Says what occupies a stretch of the device, pointing back from the space to
  /// the thing that holds it.
  /// </summary>
  /// <remarks>
  /// <para>The alloc tree says a bucket holds a b-tree node; this says which one.
  /// The position is where the target sits, shifted up so the low bits can carry
  /// an offset inside the bucket, and the value names the tree, the node's level
  /// within it, and how much of the bucket the node takes.</para>
  ///
  /// <para>Unlike the accounting totals, <c>fsck</c> checks these: a key naming
  /// the wrong tree or the wrong level is a volume the checker rejects, so these
  /// are developed against it rather than against a control image.</para>
  /// </remarks>
  private static Key NodeBackpointerKey(long bucket, int btreeId, int level, int sectors) {
    var value = new byte[32];
    value[0] = (byte)btreeId;
    value[1] = (byte)level;
    value[2] = DataBtree;
    value[3] = 0;                                                              // bucket_gen
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 0);              // flags, incl. sub-offset
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);  // bucket_len
    // A node is not an extent, so the position it covers is the top of the space.
    WriteBpos(value.AsSpan(12), Bpos.Max);
    return new Key(KeyBackpointer,
      new Bpos(0, (ulong)(bucket * BucketSectors) << ExtentBpShift, 0), 0, value);
  }

  /// <summary>
  /// Buckets, live sectors and fragmented sectors, for one kind of content.
  /// </summary>
  /// <remarks>
  /// <para>Fragmented is what a used bucket holds that the content does not: the
  /// bucket is counted whole, so the difference between its sectors and the live
  /// ones is space inside it that carries nothing. A free bucket has none — it is
  /// not partly used, it is unused — and a real filesystem records zero there
  /// rather than the whole bucket.</para>
  ///
  /// <para><c>fsck</c> does not check these counters: a volume with the wrong
  /// number here is accepted exactly as one with the right number is, which was
  /// tested rather than assumed. The values are held to a filesystem that
  /// <c>mkfs.bcachefs</c> made and the kernel initialised, because that is the
  /// only thing that will contradict them.</para>
  /// </remarks>
  private static Key DevDataTypeKey(byte dataType, ulong buckets, ulong sectors) =>
    AccountingKey([AccountingDevDataType, 0, dataType],
      buckets, sectors, dataType == DataFree ? 0 : buckets * BucketSectors - sectors);

  private static Key BucketGensKey(long first) =>
    new(KeyBucketGens, new Bpos(0, (ulong)(first / BucketGensNr), 0), 0, new byte[BucketGensNr]);

  /// <summary>Marks a run of free buckets, in the freespace tree.</summary>
  /// <remarks>
  /// The freespace tree is an extents tree, so a key covers a range rather than a
  /// single position: it is keyed by where the run ends and carries its length as
  /// the key's size. A key per bucket would have size zero, which an extents tree
  /// rejects — a run of free buckets is one key, however long it is. The value is
  /// empty; a bare <c>KEY_TYPE_set</c> is the whole fact.
  /// </remarks>
  private static Key FreespaceKey(long firstBucket, long endBucket) =>
    new(KeySet, new Bpos(0, (ulong)endBucket, 0), (uint)(endBucket - firstBucket), []);

  private static Key InodeAllocCursorKey(ulong next) {
    var value = new byte[24];
    BinaryPrimitives.WriteUInt64LittleEndian(value, 2147483648UL);             // min
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), long.MaxValue);  // max
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), next);          // idx
    return new Key(KeyInodeAllocCursor, new Bpos(1, 1, 0), 0, value);
  }

  // ── Superblock ──────────────────────────────────────────────────────────

  private byte[] BuildSuperblock(Plan plan, List<(int Btree, int Level, Key Pointer)> roots) {
    var sections = new List<byte[]> {
      this.MembersSection(plan),
      ReplicasV0Section(),
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
    var features = FeatureNewSiphash | FeatureNewExtentOverwrite | FeatureBtreePtrV2
      | FeatureExtentsAboveBtreeUpdates | FeatureBtreeUpdatesJournalled | FeatureNewVarint
      | FeatureJournalNoFlush | FeatureAllocV2 | FeatureExtentsAcrossBtreeNodes
      | FeatureIncompatVersionField;
    // Neither no_alloc_info nor small_image is claimed any more. They were how a
    // volume without allocation information asked to be let past the check that
    // would have built it, and no volume a formatter writes says either — which
    // made ours answerable by the bit alone. The alloc, bucket_gens and freespace
    // trees are written now, so there is nothing to be let past.
    BinaryPrimitives.WriteUInt64LittleEndian(span[208..], features);
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
    // How far a backpointer's position sits above the sector it names. Left
    // unset it reads as a default of ten, and the backpointers written here
    // would then name a bucket sixty-four times too far along; a formatter
    // writes sixteen, and so does this.
    Set(6, 40, 48, ExtentBpShift);

    for (var i = 0; i < 7; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(flags[(8 * i)..], words[i]);
  }

  private static byte[] Section(uint type, int payloadBytes) {
    var section = new byte[8 + (payloadBytes + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(section, (uint)(section.Length / 8));
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), type);
    return section;
  }

  /// <summary>
  /// Which sets of devices hold a copy of what, as the superblock declares them.
  /// </summary>
  /// <remarks>
  /// An accounting counter may only name a set that appears here, so this and
  /// those counters have to be written together: the checker reads the counters,
  /// looks each set up in this section, and refuses the volume when one is
  /// missing. The v0 entry is the content type, how many devices carry it, and
  /// which — there being one device here, that is a single zero.
  /// </remarks>
  private static byte[] ReplicasV0Section() {
    byte[] types = [DataBtree, DataUser];
    var section = Section(FieldReplicasV0, 3 * types.Length);
    for (var i = 0; i < types.Length; ++i) {
      section[8 + 3 * i] = types[i];
      section[9 + 3 * i] = 1;                                                  // nr_devs
      section[10 + 3 * i] = 0;                                                 // the one device
    }
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
      // Durability is stored one higher than it is, so that a zero can mean "the
      // default, which is one". Storing the durability itself declares a device
      // that keeps no copy of anything, and such a device is never chosen to hold
      // a journal or a b-tree — which is what "no writeable journal devices" means.
      | (2UL << 28)
      | (1UL << 32) | (1UL << 33);       // rotational, and that we know it
    BinaryPrimitives.WriteUInt64LittleEndian(member[40..], flags);
    BinaryPrimitives.WriteUInt64LittleEndian(member[112..], 1);                // seq

    // Which parts of the device hold b-tree nodes, as sixty-four bits covering
    // the device between them. The shift is the smallest that brings the last
    // b-tree sector inside those sixty-four bits, which is how a kernel widening
    // the bitmap picks it — pick a wider one and the bits land elsewhere than the
    // checker recomputes them.
    var btreeEndSector = (FirstMetadataBucket + plan.BtreeBuckets) * BucketSectors;
    var shift = 0;
    while ((64L << shift) < btreeEndSector) ++shift;
    var bitmap = 0UL;
    for (var b = (long)FirstMetadataBucket; b < FirstMetadataBucket + plan.BtreeBuckets; ++b) {
      var first = b * BucketSectors >> shift;
      var last = ((b + 1) * BucketSectors - 1) >> shift;
      for (var bit = first; bit <= last; ++bit) bitmap |= 1UL << (int)bit;
    }
    member[28] = (byte)shift;
    BinaryPrimitives.WriteUInt64LittleEndian(member[128..], bitmap);
    return section;
  }

  private static byte[] JournalSection() {
    // One range of buckets, given as a start and a count.
    var section = Section(FieldJournalV2, 16);
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(8), JournalFirstBucket);
    BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(16), JournalBuckets);
    return section;
  }

  private static byte[] CleanSection(List<(int Btree, int Level, Key Pointer)> roots) {
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

    foreach (var (btree, level, pointer) in roots) {
      var entry = new byte[8 + pointer.Bytes];
      BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)(pointer.Bytes / 8));
      entry[2] = (byte)btree;
      // How deep the tree is: a reader checks this against the node it finds, and a
      // root of pointers announced as a root of keys is a root it will not read.
      entry[3] = (byte)level;
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
