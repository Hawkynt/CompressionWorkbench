#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// Builds minimal ext2 filesystem images from scratch. Uses 1024-byte blocks by default
/// with a single block group. Files are stored using direct block pointers.
/// <para>
/// Produces fsck-clean output: free-block/free-inode counts, used-dirs count, inode
/// link counts, inode i_blocks (sector tally), and all three inode timestamps are
/// populated so that <c>dumpe2fs</c> / <c>e2fsck</c> do not report inconsistencies.
/// </para>
/// </summary>
public sealed class ExtWriter {
  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming
  /// entry's (firstBlock, blockCount, size, opener) is appended for use
  /// by <see cref="BuildToStreaming"/>'s post-stream pass. When null, the
  /// writer behaves identically to before.
  /// </summary>
  private List<(IReadOnlyList<int> Blocks, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>
  /// Store a file's runs of zeros as holes rather than allocating blocks for them.
  /// </summary>
  /// <remarks>
  /// ext records an absent block as a zero pointer in the block map, and a reader
  /// hands back zeros for it. So a block that is entirely zero need not be
  /// allocated at all: the file keeps its length and every one of its bytes, and
  /// the volume is sized for what was actually written. The map itself is
  /// unchanged in shape — a hole still occupies a slot in it, so the indirect
  /// blocks that address the file's tail are still needed and still counted.
  /// </remarks>
  public bool MakeSparse { get; set; }

  /// <summary>Which of a file's blocks are entirely zero, per block size.</summary>
  private readonly Dictionary<(int Index, int BlockSize), bool[]> _holeCache = [];

  /// <summary>
  /// Store one copy of files whose bytes are identical and give the rest a second
  /// name for it.
  /// </summary>
  /// <remarks>
  /// That is what a hard link is: one inode, one set of blocks, and a count of how
  /// many directory entries point at it. ext keeps that count in the inode, and
  /// e2fsck checks it against the entries it finds, so linking is a matter of
  /// naming the same inode twice and saying so. The content is stored once
  /// however many names it has.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>A file's contents, as a key that two identical files share.</summary>
  private readonly Dictionary<int, string> _contentKeys = [];

  private string ContentKey(int index) {
    if (this._contentKeys.TryGetValue(index, out var cached)) return cached;

    var entry = this._files[index];
    byte[] digest;
    if (entry.StreamOpener != null) {
      using var source = entry.StreamOpener();
      digest = System.Security.Cryptography.SHA256.HashData(source);
    } else {
      digest = System.Security.Cryptography.SHA256.HashData(entry.Data);
    }

    // The length goes in the key as well: a digest is what says two files are the
    // same, and the length is what makes saying so cheap to be sure of.
    var length = entry.StreamingSize ?? (long)entry.Data.Length;
    var key = length.ToString(System.Globalization.CultureInfo.InvariantCulture)
      + ":" + Convert.ToHexString(digest);
    this._contentKeys[index] = key;
    return key;
  }

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, byte[] data) => _files.Add((name, data, null, null));

  /// <summary>
  /// Reads a file once and says which of its blocks hold nothing but zeros.
  /// </summary>
  /// <remarks>
  /// Empty when holes are not wanted, so every caller can ask without checking.
  /// The answer depends on the block size, which is not settled until the volume
  /// is planned, so it is cached per size rather than computed per use — the
  /// planner and the writer have to agree about it exactly or the volume is sized
  /// for one layout and written with another.
  /// </remarks>
  private bool[] HoleMap(int index, int blockSize) {
    if (!this.MakeSparse) return [];
    if (this._holeCache.TryGetValue((index, blockSize), out var cached)) return cached;

    var entry = this._files[index];
    var length = entry.StreamingSize ?? (long)entry.Data.Length;
    var count = length <= 0 ? 0 : (int)((length + blockSize - 1) / blockSize);
    var holes = new bool[count];

    if (count > 0) {
      if (entry.StreamOpener != null) {
        using var source = entry.StreamOpener();
        var buffer = new byte[blockSize];
        for (var b = 0; b < count; ++b) {
          var want = (int)Math.Min(blockSize, length - (long)b * blockSize);
          var read = 0;
          while (read < want) {
            var n = source.Read(buffer, read, want - read);
            if (n <= 0) break;
            read += n;
          }
          holes[b] = !buffer.AsSpan(0, read).ContainsAnyExcept((byte)0);
        }
      } else {
        for (var b = 0; b < count; ++b) {
          var at = (long)b * blockSize;
          var want = (int)Math.Min(blockSize, entry.Data.Length - at);
          holes[b] = want <= 0 || !entry.Data.AsSpan((int)at, want).ContainsAnyExcept((byte)0);
        }
      }
    }

    this._holeCache[(index, blockSize)] = holes;
    return holes;
  }

  /// <summary>How many blocks a file actually occupies once its holes are left out.</summary>
  private long AllocatedBlocks(int index, int blockSize) {
    var length = this._files[index].StreamingSize ?? (long)this._files[index].Data.Length;
    if (length <= 0) return 0;

    var total = (length + blockSize - 1) / blockSize;
    var holes = this.HoleMap(index, blockSize);
    if (holes.Length == 0) return total;

    var absent = 0L;
    foreach (var hole in holes) if (hole) ++absent;
    return total - absent;
  }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives extent + inode
  /// + block-group sizing in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of
  /// <see cref="BuildToStreaming"/>. Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>
  /// ext filesystem revision selector used by the writer's
  /// <see cref="Build(int, int, ExtVersion, bool, string, int)"/> overload.
  /// Drives the feature-flag set in the superblock — ext2 leaves
  /// HAS_JOURNAL/EXTENTS/64BIT clear; ext3 adds HAS_JOURNAL + a journal inode;
  /// ext4 adds EXTENTS + 64BIT on top.
  /// </summary>
  public enum ExtVersion { Ext2, Ext3, Ext4 }

  /// <summary>
  /// Builds the image with the block size chosen by
  /// <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/> to minimise
  /// slack + metadata overhead, and the block count sized to exactly hold the files.
  /// </summary>
  /// <param name="requestedBlockSize">Block size in bytes (0 = auto-select).</param>
  public byte[] BuildAutoSized(int requestedBlockSize = 0)
    => this.BuildAutoSized(requestedBlockSize, ExtVersion.Ext2, journal: false, volumeLabel: "", inodeSize: 128);

  /// <summary>
  /// Auto-sizes the volume to the files added, honouring the requested version,
  /// journal, label and inode size.
  /// </summary>
  public byte[] BuildAutoSized(int requestedBlockSize, ExtVersion version, bool journal,
                               string volumeLabel, int inodeSize) {
    var (blockSize, totalBlocks) = PlanAutoSize(requestedBlockSize, version, journal, inodeSize);
    return Build(blockSize, totalBlocks, version, journal, volumeLabel, inodeSize);
  }

  /// <summary>
  /// Chooses the block size and volume size for the files added. Shared by the
  /// buffered and the streaming auto-sized builds so both lay out the same volume.
  /// </summary>
  private (int BlockSize, int TotalBlocks) PlanAutoSize(int requestedBlockSize, ExtVersion version,
                                                        bool journal, int inodeSize) {
    var fileSizes = _files.Select(f => f.StreamingSize ?? (long)f.Data.Length).ToList();

    var blockSize = requestedBlockSize > 0
      ? requestedBlockSize
      : this.SelectOptimalBlockSize();

    // A single block group holds 8 * blockSize blocks, so the larger the block the
    // larger the volume this writer can express. Step up when the payload needs it.
    var totalPayload = fileSizes.Sum();
    foreach (var candidate in BlockSizeCandidates)
      if (candidate > blockSize && totalPayload * 12 / 10 > (long)8 * blockSize * blockSize)
        blockSize = candidate;

    // Distinct directories implied by the nested paths (the root plus every
    // path prefix), and the number of entries (children) each holds — a
    // directory's entries can span several data blocks once they overflow one.
    var dirPaths = new HashSet<string> { "" };
    var dirEntryCount = new Dictionary<string, int>();
    foreach (var entry in _files) {
      var name = entry.Name;
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      var prefix = "";
      for (var s = 0; s < segments.Length - 1; ++s) {
        var parent = prefix;
        prefix = prefix.Length == 0 ? segments[s] : prefix + "/" + segments[s];
        if (dirPaths.Add(prefix))
          dirEntryCount[parent] = dirEntryCount.GetValueOrDefault(parent) + 1; // subdir adds an entry to its parent
      }
      // The leaf file adds an entry to its immediate parent directory.
      dirEntryCount[prefix] = dirEntryCount.GetValueOrDefault(prefix) + 1;
    }

    // Each directory needs enough data blocks to hold its entries (its own
    // "."/".." plus children), so estimate per-directory block usage and add a
    // singly-indirect block whenever a directory needs more than 12 blocks.
    var dirBlocks = 0L;
    foreach (var dir in dirPaths) {
      var entries = dirEntryCount.GetValueOrDefault(dir) + 2; // + "." + ".."
      // Worst-case entry size for the short names this tool produces is small,
      // but reserve a generous fixed slot so the estimate never under-shoots.
      var perBlock = Math.Max(1, blockSize / 32);
      var blocks = (entries + perBlock - 1) / perBlock;
      dirBlocks += blocks + (blocks > 12 ? 1 : 0); // + singly-indirect block
    }

    // Inode table must hold the reserved inodes, every directory and every file.
    var inodeCount = ChooseInodeCount(dirPaths.Count + _files.Count);
    var inodeTableBlocks = ((long)inodeCount * Math.Max(128, inodeSize) + blockSize - 1) / blockSize;
    // What the files occupy once their holes are left out. Sizing from the
    // logical lengths instead would leave the volume exactly as large as before
    // and the space reclaimed as free room inside it, which is not what asking
    // for a smaller image means.
    var dataBlocks = 0L;
    var countedContent = new HashSet<string>(StringComparer.Ordinal);
    for (var i = 0; i < this._files.Count; ++i) {
      // A file that will be a second name for another one costs no blocks, so
      // sizing the volume as though it did would give back an image no smaller
      // than before and call the difference free space.
      if (this.DeduplicateWithLinks && !countedContent.Add(this.ContentKey(i))) continue;
      dataBlocks += this.AllocatedBlocks(i, blockSize);
    }

    // Pointer blocks for the single/double/triple-indirect map. A file needing n
    // data blocks past the first twelve costs roughly n/ptrs level-1 blocks plus
    // n/ptrs^2 level-2 blocks; budgeting for them keeps the auto-sized image from
    // coming up short once a file outgrows its direct pointers.
    var ptrsPerBlock = Math.Max(1, blockSize / 4);
    var pointerBlocks = fileSizes.Sum(s => {
      if (s <= 0) return 0L;
      var n = (s + blockSize - 1) / blockSize - 12;
      if (n <= 0) return 0L;
      var lvl1 = (n + ptrsPerBlock - 1) / ptrsPerBlock;
      var lvl2 = (lvl1 + ptrsPerBlock - 1) / ptrsPerBlock;
      return lvl1 + lvl2 + 2; // + the double- and triple-indirect roots
    });

    // A journal costs up to 1024 blocks plus its own pointer blocks.
    var journalBlocks = journal && version is ExtVersion.Ext3 or ExtVersion.Ext4 ? 1100L : 0L;

    // Everything the volume must hold besides per-group metadata.
    var payloadBlocks = dirBlocks + dataBlocks + pointerBlocks + journalBlocks;
    var totalBlocks = (int)Math.Max(4096, (payloadBlocks + inodeTableBlocks + 4) * 11 / 10);

    // Each block group carries its own superblock backup, descriptor copy,
    // bitmaps and inode table, so the volume has to grow to cover them. The
    // group count follows from the size, so settle it by iterating: three
    // passes is ample, since each one only adds metadata for the groups the
    // previous size implied.
    var neededInodes = 11 + CountDirectories() + _files.Count;
    for (var pass = 0; pass < 3; ++pass) {
      var geo = ExtBlockGroupGeometry.Compute(blockSize, totalBlocks, inodeSize, neededInodes);
      var needed = payloadBlocks + (long)geo.GroupCount * geo.PerGroupMetaBlocks;
      var want = (int)Math.Max(4096, Math.Min(int.MaxValue / 2, needed * 11 / 10));
      if (want <= totalBlocks) break;
      totalBlocks = want;
    }

    return (blockSize, totalBlocks);
  }

  /// <summary>
  /// ext block sizes legal for this minimal writer: 1 KB, 2 KB, 4 KB.
  /// </summary>
  private static readonly int[] BlockSizeCandidates = [1024, 2048, 4096];

  /// <summary>
  /// Picks the block size (bytes) that minimises file-tail slack plus the ext
  /// metadata footprint (superblock + group descriptor + block/inode bitmaps +
  /// inode table) for the current file-set, via the shared
  /// <see cref="Compression.Core.Layout.LayoutOptimizerAdapter"/>. Every candidate
  /// is a legal ext block size, so the chosen image always round-trips.
  /// </summary>
  /// <param name="inodeSize">On-disk inode size in bytes (128 or 256), used to
  /// weight the inode-table overhead term.</param>
  public int SelectOptimalBlockSize(int inodeSize = 128) {
    var fileSizes = _files.Select(f => f.StreamingSize ?? (long)f.Data.Length).ToList();
    var estimatedInodes = ChooseInodeCount(_files.Count + 1);
    return Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
      BlockSizeCandidates,
      fileSizes,
      fixedOverhead: bs => {
        // ext metadata: superblock + group desc + 2 bitmaps + inode table.
        var inodeTableBytes = (long)estimatedInodes * inodeSize;
        return 4L * bs + inodeTableBytes;
      });
  }

  /// <summary>
  /// Legacy two-argument <c>Build()</c> overload — emits the historical
  /// minimal ext2 layout (dynamic-rev superblock with FILETYPE only, 128-byte
  /// inodes, no journal, no extents, no 64BIT). Kept byte-compatible with the
  /// upstream writer so <see cref="ExtModifier"/>, <see cref="BuildAutoSized(int)"/>,
  /// the external-conformance tests, and the version detector (which classifies
  /// the image by feature flags) all observe the same ext2
  /// baseline this writer has always produced. The verbose <c>Build(int, int,
  /// ExtVersion, bool, string, int)</c> overload — invoked from the
  /// descriptor's Create() — drives the new ext3/ext4 paths.
  /// </summary>
  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096)
    => Build(blockSize, totalBlocks, ExtVersion.Ext2, journal: false, volumeLabel: "", inodeSize: 128);

  /// <summary>
  /// Builds an ext2/3/4 filesystem image with caller-selected revision, block
  /// size, journal flag, volume label, and inode size. The default overload
  /// (above) keeps the historical "minimal ext4 image" behaviour; the verbose
  /// overload is invoked by the format descriptor's Create() once it has
  /// resolved the user-supplied options.
  /// </summary>
  /// <param name="blockSize">Block size in bytes — 1024, 2048, or 4096.</param>
  /// <param name="totalBlocks">Total blocks in the image.</param>
  /// <param name="version">Filesystem revision to advertise (ext2/3/4).</param>
  /// <param name="journal">Enable the journal — always true for ext3/ext4
  /// (the flag is ignored for ext2 since it has no journal).</param>
  /// <param name="volumeLabel">Optional volume label (up to 16 ASCII bytes).</param>
  /// <param name="inodeSize">Inode size in bytes — 128 (classic) or 256 (modern).</param>
  public byte[] Build(int blockSize, int totalBlocks, ExtVersion version, bool journal, string volumeLabel, int inodeSize)
    => BuildCore(blockSize, totalBlocks, version, journal, volumeLabel, inodeSize).Materialise();

  /// <summary>
  /// Lays the volume out and writes it straight into a seekable stream. Only the
  /// blocks the filesystem actually touches are ever resident, so a volume larger
  /// than a byte[] can address is written without being materialised.
  /// </summary>
  public void BuildTo(Stream output, int blockSize, int totalBlocks, ExtVersion version,
                      bool journal, string volumeLabel, int inodeSize) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildTo requires a writable, seekable stream.", nameof(output));
    BuildCore(blockSize, totalBlocks, version, journal, volumeLabel, inodeSize).WriteTo(output);
  }

  private SparseBlockImage BuildCore(int blockSize, int totalBlocks, ExtVersion version, bool journal,
                               string volumeLabel, int inodeSize) {
    const ushort ExtMagic = 0xEF53;
    // EXT2_GOOD_OLD_FIRST_INO — first inode available for user files on a
    // revision-0 (GOOD_OLD_REV) filesystem. Inodes 1..10 are reserved:
    //   1=bad-blocks, 2=root, 3=ACL-idx (obsolete), 4=ACL-data (obsolete),
    //   5=boot-loader, 6=undeleted-dir, 7=resize, 8=journal, 9=exclude,
    //   10=replica.  e2fsck refuses to accept dirents pointing at 3..10.
    const uint FirstUserInode = 11;
    // EXT4 feature flags (fs/ext4/ext4.h).
    const uint FeatureIncompatFiletype = 0x0002;
    const uint FeatureIncompatExtents = 0x0040;
    const uint FeatureIncompat64Bit = 0x0080;
    const uint FeatureIncompatCsumSeed = 0x2000;
    const uint FeatureIncompatFlexBg = 0x0200;
    const uint FeatureCompatOrphanFile = 0x1000;
    const uint ExtentsFlag = 0x00080000;
    const ushort ExtentMagic = 0xF30A;
    const int LogGroupsPerFlex = 4;
    const uint FeatureRoCompatMetadataCsum = 0x0400;
    const byte ChecksumTypeCrc32c = 1;
    const uint FeatureCompatHasJournal = 0x0004;
    const uint FeatureCompatExtAttr = 0x0008;
    const uint FeatureCompatDirIndex = 0x0020;
    const uint FeatureCompatResizeInode = 0x0010;
    const uint FeatureRoCompatSparseSuper = 0x0001;
    const uint FeatureRoCompatExtraIsize = 0x0040;
    const int MinimumInodeSize = 128;
    const int ExtraIsizeBytes = 32;
    const uint FeatureRoCompatLargeFile = 0x0002;
    const uint FeatureRoCompatHugeFile = 0x0008;
    const uint FeatureRoCompatDirNlink = 0x0020;
    const byte HashVersionHalfMd4 = 1;
    const byte JournalBackupBlocks = 1;
    const uint FlagSignedHash = 0x0001;
    // Dynamic revision — required so s_inode_size / s_first_ino / feature
    // flags are honoured by the kernel and fsck.
    const uint RevLevelDynamic = 1;
    // Journal inode number reserved by the kernel (inode 8 in the
    // boot/reserved range). When HAS_JOURNAL is set the SB advertises this
    // inode as the journal device.
    const uint JournalInode = 8;
    const uint ResizeInode = 7;
    const uint OrphanFileInode = 12;

    if (inodeSize != 128 && inodeSize != 256)
      throw new ArgumentException($"Invalid inodeSize {inodeSize}; must be 128 or 256.", nameof(inodeSize));

    var neededInodes = (int)FirstUserInode + CountDirectories() + _files.Count;
    // A 64BIT volume writes 64-byte group descriptors: the classic fields, then a
    // high half for each. Ours are all small enough that the high halves are zero,
    // but the width is declared and the table is sized for it either way.
    var descriptorSize = version == ExtVersion.Ext4 ? WideGroupDescriptorSize : GroupDescriptorSize;
    var geo = ExtBlockGroupGeometry.Compute(blockSize, totalBlocks, inodeSize, neededInodes, descriptorSize);
    totalBlocks = geo.TotalBlocks;
    var firstDataBlock = geo.FirstDataBlock;
    var blocksPerGroup = geo.BlocksPerGroup;
    var groupCount = geo.GroupCount;
    var gdtBlocks = geo.GdtBlocks;
    var inodesPerGroup = geo.InodesPerGroup;
    var inodeTableBlocks = geo.InodeTableBlocks;

    var img = new SparseBlockImage(blockSize, (long)totalBlocks * blockSize);
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sectorsPerBlock = blockSize / 512;

    // The volume's identity, and the seed every structure on it is summed with.
    // Both are needed from the first directory block onward, so they are settled
    // before anything is laid down rather than at the superblock.
    var uuid = Guid.NewGuid().ToByteArray(bigEndian: true);
    var checksums = version == ExtVersion.Ext4;
    var orphanFile = checksums;
    var volumeSeed = ExtChecksums.SeedFromUuid(uuid);

    // Group g owns blocks [GroupStart(g), GroupStart(g) + blocksPerGroup) and
    // opens with its block bitmap, inode bitmap and inode table, behind a spare
    // superblock and descriptor table in the groups that keep one.
    int GroupStart(int g) => firstDataBlock + g * blocksPerGroup;
    int GroupBlocks(int g) => (int)Math.Min(blocksPerGroup, (long)totalBlocks - GroupStart(g));

    // Only some groups keep a spare superblock and descriptor table: the first
    // two, and every group whose number is a power of three, five or seven. That
    // is what SPARSE_SUPER means, and mke2fs has laid volumes out that way since
    // before ext3 — a volume with a spare in every group is one nobody makes.
    // Blank descriptor-table blocks kept behind the real ones so the volume can be
    // grown later without moving anything. mke2fs reserves enough for a thousand-
    // fold growth, capped by what one indirect block can address.
    var addressesPerBlock = blockSize / 4;
    var reservedGdtBlocks = ReservedGdtBlocks(totalBlocks, firstDataBlock, blocksPerGroup,
                                              blockSize, gdtBlocks, addressesPerBlock, descriptorSize);

    int SuperblockBlocks(int g) => HasSuperblock(g) ? 1 + gdtBlocks + reservedGdtBlocks : 0;

    // Which blocks the volume's own structures occupy, and where each group's
    // bitmaps and inode table ended up.
    //
    // A flex group is a run of groups whose bitmaps and inode tables are gathered
    // into the front of the run rather than each sitting in its own group: all the
    // block bitmaps together, then all the inode bitmaps, then the tables. mke2fs
    // has grouped them in sixteens for as long as ext4 has existed. What the run
    // has to work around is the spare superblocks, which stay where SPARSE_SUPER
    // puts them, so the gathering takes free blocks rather than counting from an
    // offset.
    var occupied = new bool[totalBlocks];
    var blockBitmapOf = new int[groupCount];
    var inodeBitmapOf = new int[groupCount];
    var inodeTableOf = new int[groupCount];
    // Sixteen groups to a flex group, which is the size mke2fs has always used;
    // an ext2 or ext3 volume has no flex groups at all, which is a run of one.
    var logGroupsPerFlex = version == ExtVersion.Ext4 ? LogGroupsPerFlex : 0;
    var groupsPerFlex = 1 << logGroupsPerFlex;

    for (var g = 0; g < groupCount; ++g)
      for (var b = GroupStart(g); b < GroupStart(g) + SuperblockBlocks(g) && b < totalBlocks; ++b)
        occupied[b] = true;

    var cursor = firstDataBlock;
    for (var flexFirst = 0; flexFirst < groupCount; flexFirst += groupsPerFlex) {
      var flexEnd = Math.Min(flexFirst + groupsPerFlex, groupCount);
      cursor = Math.Max(cursor, GroupStart(flexFirst));

      int TakeRun(int count) {
        while (true) {
          while (cursor < totalBlocks && occupied[cursor]) ++cursor;
          if (cursor + count > totalBlocks)
            throw new InvalidOperationException(
              $"ext writer: the {totalBlocks}-block volume has no room for a group's metadata.");

          var clear = true;
          for (var i = 0; i < count; ++i)
            if (occupied[cursor + i]) { cursor += i + 1; clear = false; break; }
          if (!clear) continue;

          var start = cursor;
          for (var i = 0; i < count; ++i) occupied[start + i] = true;
          cursor = start + count;
          return start;
        }
      }

      for (var g = flexFirst; g < flexEnd; ++g) blockBitmapOf[g] = TakeRun(1);
      for (var g = flexFirst; g < flexEnd; ++g) inodeBitmapOf[g] = TakeRun(1);
      for (var g = flexFirst; g < flexEnd; ++g) inodeTableOf[g] = TakeRun(inodeTableBlocks);
    }

    int BlockBitmapBlock(int g) => blockBitmapOf[g];
    int InodeBitmapBlock(int g) => inodeBitmapOf[g];
    int InodeTableBlock(int g) => inodeTableOf[g];

    void MarkBlockUsed(int block) {
      var g = (block - firstDataBlock) / blocksPerGroup;
      var bit = block - GroupStart(g);
      img.Block(BlockBitmapBlock(g))[bit / 8] |= (byte)(1 << (bit % 8));
    }

    long InodeOffset(uint inode) {
      var g = (int)((inode - 1) / (uint)inodesPerGroup);
      var idx = (int)((inode - 1) % (uint)inodesPerGroup);
      return (long)InodeTableBlock(g) * blockSize + (long)idx * inodeSize;
    }

    void MarkInodeUsed(uint inode) {
      var g = (int)((inode - 1) / (uint)inodesPerGroup);
      var idx = (int)((inode - 1) % (uint)inodesPerGroup);
      img.Block(InodeBitmapBlock(g))[idx / 8] |= (byte)(1 << (idx % 8));
    }

    // Blocks are handed out in ascending order, stepping over each group's own
    // metadata. A file's blocks are therefore contiguous except where they cross
    // a group boundary, which the block map expresses without difficulty.
    var nextBlock = firstDataBlock;
    int AllocBlock() {
      while (nextBlock < totalBlocks && occupied[nextBlock]) ++nextBlock;
      if (nextBlock >= totalBlocks)
        throw new InvalidOperationException(
          $"ext writer: the {totalBlocks}-block volume has no free block left to allocate.");

      var block = nextBlock++;
      MarkBlockUsed(block);
      return block;
    }

    // --- Every group's metadata is in use from the outset. Bit N of a group's
    //     block bitmap refers to block GroupStart(g) + N, so the boot-area slot
    //     on 1 KiB filesystems is implicit and not tracked by any bit. ---
    for (var b = firstDataBlock; b < totalBlocks; ++b)
      if (occupied[b]) MarkBlockUsed(b);

    // --- Inodes 1..(FirstUserInode-1) are all reserved; inode 2 (root) is
    // actually in use. Set bits for inodes 1..10 so fsck doesn't flag "reserved
    // inode in use but empty", which is the default mkfs.ext4 behaviour. ---
    for (var ino = 1u; ino < FirstUserInode; ++ino)
      MarkInodeUsed(ino);

    // --- Inode 7 owns the reserved descriptor-table blocks ---
    // A file with nothing but a double-indirect block, whose slots name the
    // reserved blocks in this group, and each of those in turn names its own
    // copies in the groups that keep a spare superblock. e2fsck checks the whole
    // shape and calls a volume whose seventh inode does not have it invalid.
    if (reservedGdtBlocks > 0) {
      var dind = AllocBlock();
      var reservedFirst = firstDataBlock + 1 + gdtBlocks;
      var backupGroups = new List<int>();
      for (var g = 1; g < groupCount; ++g)
        if (HasSuperblock(g)) backupGroups.Add(g);

      var highestSlot = 0;
      for (var reserved = 0; reserved < reservedGdtBlocks; ++reserved) {
        var gdtBlock = reservedFirst + reserved;
        var slot = (reserved + 1) % addressesPerBlock;
        highestSlot = Math.Max(highestSlot, slot);
        BinaryPrimitives.WriteUInt32LittleEndian(img.At((long)dind * blockSize + slot * 4, 4), (uint)gdtBlock);

        // Where the same reserved block sits in every group that mirrors it.
        var withinGroup = gdtBlock - firstDataBlock;
        var copies = img.Block(gdtBlock);
        for (var i = 0; i < backupGroups.Count; ++i)
          BinaryPrimitives.WriteUInt32LittleEndian(copies.Slice(i * 4, 4),
            (uint)(GroupStart(backupGroups[i]) + withinGroup));
      }

      var resizeInode = img.At(InodeOffset(ResizeInode), inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(resizeInode, 0x8000 | 0x0180);   // i_mode = S_IFREG | 0600
      BinaryPrimitives.WriteUInt16LittleEndian(resizeInode[26..], 1);           // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[8..], now);          // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[12..], now);         // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[16..], now);         // i_mtime
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[92..], (uint)dind);  // i_block[13], the double-indirect block

      // The file reaches as far as the last slot the double-indirect block uses.
      var logicalBlocks = 12L + addressesPerBlock + (long)(highestSlot + 1) * addressesPerBlock;
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[4..], (uint)(logicalBlocks * blockSize)); // i_size
      // Every block it holds: the double-indirect block, this group's reserved
      // blocks, and the copies of those in each group that mirrors them.
      var heldBlocks = 1L + (long)reservedGdtBlocks * (1 + backupGroups.Count);
      BinaryPrimitives.WriteUInt32LittleEndian(resizeInode[28..], (uint)(heldBlocks * sectorsPerBlock)); // i_blocks
    }

    var nextInode = FirstUserInode;
    // Every volume mke2fs makes opens with a lost+found for e2fsck to reconnect
    // orphans into, and keeps the orphan file behind it. A root without one is a
    // root nobody's mkfs laid out, so both take their inodes before any file does.
    var lostAndFoundInode = nextInode++;
    if (orphanFile) ++nextInode;   // the orphan file's, claimed below

    // --- Build the directory tree from the (possibly nested) file paths. ---
    // The root directory is inode 2. Every path segment before the final name
    // becomes a real subdirectory inode; the final segment is the regular file.
    const uint RootInode = 2;
    var root = new DirNode { Inode = RootInode, Parent = RootInode };
    root.Subdirs["lost+found"] =
      new DirNode { Inode = lostAndFoundInode, Parent = RootInode, Name = "lost+found" };

    // Each entry carries the position it came from in the registered list. The
    // hole map and the content key are both looked up by that position, and
    // once deduplication starts dropping entries this list no longer runs in
    // step with it — a file's own place here is not where it was registered.
    var fileInodes = new List<(uint Inode, DirNode Parent, string LeafName, byte[] Data,
      long? StreamingSize, Func<Stream>? StreamOpener, int SourceIndex)>();
    // When identical files are to share one copy, the first of them gets the
    // inode and the blocks; every later one gets a directory entry naming that
    // same inode and nothing else. The inode's link count is how many names it
    // ends up with, and e2fsck checks that against the entries it finds.
    var inodeOfContent = new Dictionary<string, uint>(StringComparer.Ordinal);
    var linksOfInode = new Dictionary<uint, int>();
    var fileOrdinalForContent = -1;
    foreach (var (name, data, streamingSize, opener) in _files) {
      ++fileOrdinalForContent;
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var dir = root;
      for (var s = 0; s < segments.Length - 1; ++s) {
        var segment = segments[s];
        if (!dir.Subdirs.TryGetValue(segment, out var child)) {
          child = new DirNode { Inode = nextInode++, Parent = dir.Inode, Name = segment };
          dir.Subdirs.Add(segment, child);
        }
        dir = child;
      }

      var leaf = segments[^1];

      if (this.DeduplicateWithLinks) {
        var key = this.ContentKey(fileOrdinalForContent);
        if (inodeOfContent.TryGetValue(key, out var shared)) {
          dir.Files.Add((leaf, shared));
          linksOfInode[shared] = linksOfInode.GetValueOrDefault(shared) + 1;
          continue;
        }

        var firstInode = nextInode++;
        inodeOfContent[key] = firstInode;
        linksOfInode[firstInode] = 1;
        fileInodes.Add((firstInode, dir, leaf, data, streamingSize, opener, fileOrdinalForContent));
        dir.Files.Add((leaf, firstInode));
        continue;
      }

      var fileInode = nextInode++;
      linksOfInode[fileInode] = 1;
      fileInodes.Add((fileInode, dir, leaf, data, streamingSize, opener, fileOrdinalForContent));
      dir.Files.Add((leaf, fileInode));
    }

    if (nextInode > (uint)inodesPerGroup * (uint)groupCount)
      throw new InvalidOperationException(
        $"ext writer: {nextInode - 1} inodes needed but the volume holds only {(long)inodesPerGroup * groupCount}.");

    // --- Mark every allocated inode (directories + files) as used. ---
    var allDirs = new List<DirNode>();
    CollectDirs(root, allDirs);
    foreach (var node in allDirs) {
      if (node.Inode < FirstUserInode) continue; // root (inode 2) reserved-bit is set above
      MarkInodeUsed(node.Inode);
    }
    foreach (var fi in fileInodes)
      MarkInodeUsed(fi.Inode);

    // --- Lay out each directory's entries across one or more data blocks. ---
    // Records never straddle a block boundary: when the next record would not
    // fit, the current block's last record has its rec_len padded to the block
    // end and the next record opens a fresh block. "." / ".." remain the first
    // two records of the first block. Up to 12 direct blocks are used, after
    // which a singly-indirect block chains the rest.
    const int MaxDirEntryBytes = 8 + 255 + 3 & ~3; // largest possible single dirent
    var dirBlockLists = new Dictionary<uint, List<int>>();
    foreach (var node in allDirs) {
      // Assemble the ordered list of entries this directory holds.
      var entries = new List<(uint Inode, string Name, byte FileType)> {
        (node.Inode, ".", 2),
        (node.Parent, "..", 2),
      };
      foreach (var child in node.Subdirs.Values)
        entries.Add((child.Inode, child.Name, 2));
      foreach (var (leaf, fileInode) in node.Files)
        entries.Add((fileInode, leaf, 1));

      // Split the entries into per-block runs, then emit each block, padding the
      // final record of every block to the end of the space records may use — which
      // on a summed volume stops twelve bytes short, where the tail goes.
      var usableEnd = blockSize - (checksums ? ExtChecksums.DirectoryTailBytes : 0);
      var blockList = new List<int>();
      var idx = 0;
      while (idx < entries.Count) {
        var blockData = new byte[blockSize];
        var pos = 0;
        var firstInBlock = idx;
        while (idx < entries.Count) {
          var nameLen = Encoding.UTF8.GetByteCount(entries[idx].Name);
          var size = 8 + nameLen + 3 & ~3;
          if (pos + size > usableEnd) break; // does not fit; close this block
          pos += size;
          ++idx;
        }
        pos = 0; // reset for the write pass below
        if (idx == firstInBlock)
          throw new InvalidOperationException(
            $"ext2 writer: directory entry '{entries[firstInBlock].Name}' exceeds a single {blockSize}-byte block.");

        for (var e = firstInBlock; e < idx; ++e) {
          var (inode, name, fileType) = entries[e];
          var isLast = e == idx - 1; // last record in this block gets padded rec_len
          pos = WriteDirEntry(blockData, pos, inode, name, fileType, usableEnd, isLast);
        }

        if (checksums)
          ExtChecksums.StampDirectoryTail(blockData, blockSize,
            ExtChecksums.InodeSeed(volumeSeed, node.Inode, generation: 0));

        var blockNum = AllocBlock();
        blockData.CopyTo(img.Block(blockNum));
        blockList.Add(blockNum);
      }

      // mke2fs gives lost+found room to reconnect into without having to grow the
      // directory first: sixteen kilobytes, or the twelve blocks an inode maps
      // without an indirect block, whichever is less.
      if (node.Inode == lostAndFoundInode) {
        var wanted = Math.Min(12, Math.Max(1, 16384 / blockSize));
        while (blockList.Count < wanted) {
          var padding = new byte[blockSize];
          BinaryPrimitives.WriteUInt32LittleEndian(padding, 0);                     // no entry here
          BinaryPrimitives.WriteUInt16LittleEndian(padding.AsSpan(4), (ushort)usableEnd);
          if (checksums)
            ExtChecksums.StampDirectoryTail(padding, blockSize,
              ExtChecksums.InodeSeed(volumeSeed, node.Inode, generation: 0));

          var padBlock = AllocBlock();
          padding.CopyTo(img.Block(padBlock));
          blockList.Add(padBlock);
        }
      }

      node.Block = blockList[0];
      dirBlockLists[node.Inode] = blockList;

      const int MaxDirectBlocks = 12;
      var pointersPerBlock = blockSize / 4;
      var maxDirBlocks = MaxDirectBlocks + pointersPerBlock;
      if (blockList.Count > maxDirBlocks)
        throw new InvalidOperationException(
          $"ext2 writer: directory '{(node.Inode == RootInode ? "/" : node.Name)}' needs {blockList.Count} blocks " +
          $"but only direct + singly-indirect blocks are supported (max {maxDirBlocks} blocks, " +
          $"≈ {maxDirBlocks * (blockSize / MaxDirEntryBytes)} entries at {blockSize}-byte blocks).");
    }

    // --- Directory inodes (root + every subdirectory). ---
    // i_links_count for a directory = 2 (its own "." and the parent's entry for
    // it) + one per child subdirectory (each child's ".." links back here).
    foreach (var node in allDirs) {
      var blockList = dirBlockLists[node.Inode];
      const int MaxDirectBlocks = 12;

      // A singly-indirect block holds the block pointers past the first 12.
      var indirectBlockNum = 0;
      var allocatedBlocks = blockList.Count;
      if (blockList.Count > MaxDirectBlocks) {
        indirectBlockNum = AllocBlock();
        ++allocatedBlocks; // the indirect block itself counts toward i_blocks
        var indOff = (long)indirectBlockNum * blockSize;
        for (var p = MaxDirectBlocks; p < blockList.Count; ++p)
          BinaryPrimitives.WriteUInt32LittleEndian(img.At(indOff + (p - MaxDirectBlocks) * 4, 4), (uint)blockList[p]);
      }

      var dirInodeOffset = InodeOffset(node.Inode);
      var dirIno = img.At(dirInodeOffset, inodeSize);
      var mode = node.Inode == lostAndFoundInode ? 0x01C0 : 0x01ED;                  // 0700 for lost+found, else 0755
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno, (ushort)(0x4000 | mode));     // i_mode: directory
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[4..], (uint)(blockList.Count * blockSize)); // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[8..], now);                    // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[12..], now);                   // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[16..], now);                   // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno[26..], (ushort)(2 + node.Subdirs.Count)); // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[28..], (uint)(allocatedBlocks * sectorsPerBlock)); // i_blocks
      var directCount = Math.Min(MaxDirectBlocks, blockList.Count);
      for (var b = 0; b < directCount; ++b)
        BinaryPrimitives.WriteUInt32LittleEndian(dirIno[(40 + b * 4)..], (uint)blockList[b]); // direct blocks 0..11
      if (indirectBlockNum != 0)
        BinaryPrimitives.WriteUInt32LittleEndian(dirIno[88..], (uint)indirectBlockNum); // singly-indirect pointer
    }

    // --- File inodes + data blocks ---
    // Files use up to 12 direct block pointers, then the classic single /
    // double / triple indirect map. Pointer blocks count toward i_blocks
    // (e2fsck tallies them in the 512-byte sector count).
    foreach (var (fileInode, _, _, data, streamingSize, streamOpener, sourceIndex) in fileInodes) {
      var fileInodeOffset = InodeOffset(fileInode);
      var effectiveLength = streamingSize ?? (long)data.Length;

      var blocksNeeded = effectiveLength == 0 ? 0 : (int)((effectiveLength + blockSize - 1) / blockSize);

      // A hole keeps its slot in the map and costs no block: the pointer is zero,
      // and ext hands back zeros for it.
      var holes = this.HoleMap(sourceIndex, blockSize);
      var fileBlocks = new List<int>(blocksNeeded);
      for (var b = 0; b < blocksNeeded; ++b)
        fileBlocks.Add(b < holes.Length && holes[b] ? 0 : AllocBlock());

      // Streaming entries leave their data blocks unwritten; BuildToStreaming
      // post-fills them from the source. Block tail past `effectiveLength`
      // stays sparse-zero.
      if (streamOpener != null) {
        if (this._streamingSink != null && blocksNeeded > 0 && effectiveLength > 0)
          this._streamingSink.Add((fileBlocks, effectiveLength, streamOpener));
      } else {
        var written = 0;
        foreach (var fb in fileBlocks) {
          var toWrite = Math.Min(blockSize, data.Length - written);
          if (toWrite > 0 && fb != 0) data.AsSpan(written, toWrite).CopyTo(img.Block(fb));
          written += toWrite;
        }
      }

      var fileAllocatedBlocks = 0;
      foreach (var fb in fileBlocks) if (fb != 0) ++fileAllocatedBlocks;

      var ino = img.At(fileInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(ino, 0x8000 | 0x01A4);           // i_mode: regular file, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(ino[4..], (uint)effectiveLength); // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(ino[8..], now);                  // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[12..], now);                 // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[16..], now);                 // i_mtime
      // How many directory entries name this inode. e2fsck counts the entries it
      // finds and compares: an inode that five names point at while claiming one
      // is a volume it reports as damaged and offers to repair.
      BinaryPrimitives.WriteUInt16LittleEndian(ino[26..],
        (ushort)Math.Max(1, linksOfInode.GetValueOrDefault(fileInode, 1)));     // i_links_count
      fileAllocatedBlocks += WriteInodeBlockMap(img, fileInodeOffset, fileBlocks, blockSize, AllocBlock);
      BinaryPrimitives.WriteUInt32LittleEndian(
        img.At(fileInodeOffset + 28, 4), (uint)(fileAllocatedBlocks * sectorsPerBlock)); // i_blocks, incl. pointer blocks
    }

    // --- Journal (inode 8) for ext3 / ext4 ───────────────────────────────
    // A HAS_JOURNAL volume must carry a valid jbd2 journal, or e2fsck rejects
    // it with "defektes Journal (Inode 8)". We emit a CLEAN, empty journal:
    // a jbd2 V2 superblock with s_start=0 (nothing to replay) occupying the
    // first journal block, the rest zeroed. The journal blocks are mapped into
    // inode 8 via the classic direct + single-indirect + double-indirect block
    // map (valid on ext4 too, since the EXTENTS feature only permits — never
    // requires — per-inode extent maps).
    if (journal && (version == ExtVersion.Ext3 || version == ExtVersion.Ext4)) {
      var ptrsPerBlock = blockSize / 4;
      // Canonical minimum journal length; clamp to the space actually left so a
      // small image still produces a consistent (if short) journal.
      var journalBlocks = Math.Min(1024, Math.Max(64, totalBlocks - nextBlock - 32));
      var journalMap = new List<int>(journalBlocks);
      for (var b = 0; b < journalBlocks; ++b)
        journalMap.Add(AllocBlock());

      // jbd2 V2 superblock in the first journal block (all multi-byte fields BE).
      var jsbOff = (long)journalMap[0] * blockSize;
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff, 4), 0xc03b3998u);              // h_magic
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 4, 4), 4u);                   // h_blocktype = SUPERBLOCK_V2
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 8, 4), 0u);                   // h_sequence
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 12, 4), (uint)blockSize);     // s_blocksize
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 16, 4), (uint)journalBlocks); // s_maxlen
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 20, 4), 1u);                  // s_first (log starts after SB)
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 24, 4), 1u);                  // s_sequence (first commit ID)
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 28, 4), 0u);                  // s_start = 0 → empty/clean
      BinaryPrimitives.WriteUInt32BigEndian(img.At(jsbOff + 64, 4), 1u);                  // s_nr_users = 1

      // Map the journal data blocks into inode 8.
      var jinoOff = InodeOffset(JournalInode);
      var metaBlocks = 0;

      // Direct blocks 0..11 (i_block[0..11] at inode offset 40).
      for (var i = 0; i < 12 && i < journalBlocks; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(jinoOff + 40 + i * 4, 4), (uint)journalMap[i]);

      // Single-indirect (i_block[12], offset 88) covers blocks 12..12+ptrs-1.
      if (journalBlocks > 12) {
        var ind = AllocBlock();
        ++metaBlocks;
        var io = (long)ind * blockSize;
        for (var i = 0; i < ptrsPerBlock && 12 + i < journalBlocks; ++i)
          BinaryPrimitives.WriteUInt32LittleEndian(img.At(io + i * 4, 4), (uint)journalMap[12 + i]);
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(jinoOff + 88, 4), (uint)ind); // i_block[12]
      }

      // Double-indirect (i_block[13], offset 92) covers everything past 12+ptrs.
      var dindFirst = 12 + ptrsPerBlock;
      if (journalBlocks > dindFirst) {
        var dind = AllocBlock();
        ++metaBlocks;
        var dio = (long)dind * blockSize;
        var remaining = journalBlocks - dindFirst;
        var numInd = (remaining + ptrsPerBlock - 1) / ptrsPerBlock;
        for (var k = 0; k < numInd; ++k) {
          var ind2 = AllocBlock();
          ++metaBlocks;
          BinaryPrimitives.WriteUInt32LittleEndian(img.At(dio + k * 4, 4), (uint)ind2);
          var i2o = (long)ind2 * blockSize;
          for (var i = 0; i < ptrsPerBlock; ++i) {
            var blkIdx = dindFirst + k * ptrsPerBlock + i;
            if (blkIdx >= journalBlocks) break;
            BinaryPrimitives.WriteUInt32LittleEndian(img.At(i2o + i * 4, 4), (uint)journalMap[blkIdx]);
          }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(jinoOff + 92, 4), (uint)dind); // i_block[13]
      }

      // Journal inode metadata: a regular file with mode 0600 (what mke2fs writes
      // for inode 8 — e2fsck rejects the journal if inode 8 has mode 0), one link,
      // size = log length.
      var jino = img.At(jinoOff, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(jino, 0x8000 | 0x0180);                      // i_mode = S_IFREG | 0600
      BinaryPrimitives.WriteUInt32LittleEndian(jino[4..], (uint)((long)journalBlocks * blockSize)); // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(jino[8..], now);                             // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(jino[12..], now);                            // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(jino[16..], now);                            // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(jino[26..], 1);                              // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(jino[28..], (uint)((journalBlocks + metaBlocks) * sectorsPerBlock)); // i_blocks
    }

    // --- How much of each inode past the classic 128 bytes it actually uses ---
    // An inode wider than the original 128 bytes says so in its own first extra
    // field, and the volume declares the smallest and the preferred width. Every
    // inode mke2fs writes into a 256-byte table carries it; one that leaves it
    // zero is read as a 128-byte inode sitting in a 256-byte slot.
    var extraIsize = inodeSize > MinimumInodeSize ? (ushort)ExtraIsizeBytes : (ushort)0;
    if (extraIsize > 0)
      for (var ino = 1u; ino <= (uint)inodesPerGroup * groupCount; ++ino) {
        var g = (int)((ino - 1) / (uint)inodesPerGroup);
        var idx = (int)((ino - 1) % (uint)inodesPerGroup);
        if ((img.Block(InodeBitmapBlock(g))[idx / 8] & (1 << (idx % 8))) == 0) continue;

        BinaryPrimitives.WriteUInt16LittleEndian(img.At(InodeOffset(ino) + MinimumInodeSize, 2), extraIsize);
      }

    // --- Inode 12 holds the list of files unlinked while still open ---
    // A run of blocks, each ending in a magic and a sum, mapped by an extent tree
    // written into the inode itself rather than by block pointers. mke2fs gives
    // every volume one; a volume without it is one from before they did.
    var orphanBlocks = 0;
    if (orphanFile) {
      MarkInodeUsed(OrphanFileInode);
      orphanBlocks = OrphanFileBlocks(totalBlocks);
      var first = 0;
      for (var i = 0; i < orphanBlocks; ++i) {
        var block = AllocBlock();
        if (i == 0) first = block;
        else if (block != first + i)
          throw new InvalidOperationException("ext writer: the orphan file needs a contiguous run of blocks.");

        ExtChecksums.StampOrphanBlock(img.Block(block), blockSize, volumeSeed, OrphanFileInode, block);
      }

      var orphanInode = img.At(InodeOffset(OrphanFileInode), inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(orphanInode, 0x8000 | 0x0180);       // i_mode = S_IFREG | 0600
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[4..], (uint)((long)orphanBlocks * blockSize)); // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[8..], now);              // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[12..], now);             // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[16..], now);             // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(orphanInode[26..], 1);               // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[28..], (uint)(orphanBlocks * sectorsPerBlock)); // i_blocks
      BinaryPrimitives.WriteUInt32LittleEndian(orphanInode[32..], ExtentsFlag);     // i_flags

      // An extent tree of one leaf, small enough to live in the inode's own map.
      var extents = orphanInode[40..];
      BinaryPrimitives.WriteUInt16LittleEndian(extents, ExtentMagic);
      BinaryPrimitives.WriteUInt16LittleEndian(extents[2..], 1);                    // one extent
      BinaryPrimitives.WriteUInt16LittleEndian(extents[4..], 4);                    // room for four
      BinaryPrimitives.WriteUInt16LittleEndian(extents[6..], 0);                    // no depth: this is the leaf
      BinaryPrimitives.WriteUInt32LittleEndian(extents[12..], 0);                   // from the file's first block
      BinaryPrimitives.WriteUInt16LittleEndian(extents[16..], (ushort)orphanBlocks);
      BinaryPrimitives.WriteUInt16LittleEndian(extents[18..], 0);                   // start, high half
      BinaryPrimitives.WriteUInt32LittleEndian(extents[20..], (uint)first);         // start, low half
    }

    // --- Each inode's own sum, over itself ---
    // Stamped once every inode is final, and before the group descriptors are
    // summed, because an inode is part of no sum but its own.
    if (checksums)
      for (var ino = 1u; ino <= (uint)inodesPerGroup * groupCount; ++ino) {
        var g = (int)((ino - 1) / (uint)inodesPerGroup);
        var idx = (int)((ino - 1) % (uint)inodesPerGroup);
        if ((img.Block(InodeBitmapBlock(g))[idx / 8] & (1 << (idx % 8))) == 0) continue;

        var generation = BinaryPrimitives.ReadUInt32LittleEndian(img.At(InodeOffset(ino) + 100, 4));
        ExtChecksums.StampInode(img.At(InodeOffset(ino), inodeSize), inodeSize,
          ExtChecksums.InodeSeed(volumeSeed, ino, generation));
      }

    // --- Per-group bitmap padding, free counts and group descriptors ---
    // Padding at the tail of each bitmap block must be set to 1 per mkfs
    // convention; fsck flags unset padding as a corruption hint.
    var dirsPerGroup = new int[groupCount];
    foreach (var node in allDirs)
      ++dirsPerGroup[(int)((node.Inode - 1) / (uint)inodesPerGroup)];

    var totalFreeBlocks = 0L;
    var totalFreeInodes = 0L;
    var gdtBase = (long)(GroupStart(0) + 1) * blockSize;
    for (var g = 0; g < groupCount; ++g) {
      var validBlocks = GroupBlocks(g);
      var blockBitmap = img.Block(BlockBitmapBlock(g));
      for (var bit = validBlocks; bit < blockSize * 8; ++bit)
        blockBitmap[bit / 8] |= (byte)(1 << (bit % 8));
      var usedBlocks = 0;
      for (var bit = 0; bit < validBlocks; ++bit)
        if ((blockBitmap[bit / 8] & (1 << (bit % 8))) != 0) ++usedBlocks;
      var freeBlocksInGroup = validBlocks - usedBlocks;
      totalFreeBlocks += freeBlocksInGroup;

      var inodeBitmap = img.Block(InodeBitmapBlock(g));
      for (var bit = inodesPerGroup; bit < blockSize * 8; ++bit)
        inodeBitmap[bit / 8] |= (byte)(1 << (bit % 8));
      var usedInodesInGroup = 0;
      for (var bit = 0; bit < inodesPerGroup; ++bit)
        if ((inodeBitmap[bit / 8] & (1 << (bit % 8))) != 0) ++usedInodesInGroup;
      var freeInodesInGroup = inodesPerGroup - usedInodesInGroup;
      totalFreeInodes += freeInodesInGroup;

      // Group descriptor: 32 bytes, reserved area zeroed.
      var bgd = img.At(gdtBase + (long)g * descriptorSize, descriptorSize);
      BinaryPrimitives.WriteUInt32LittleEndian(bgd, (uint)BlockBitmapBlock(g));      // bg_block_bitmap
      BinaryPrimitives.WriteUInt32LittleEndian(bgd[4..], (uint)InodeBitmapBlock(g)); // bg_inode_bitmap
      BinaryPrimitives.WriteUInt32LittleEndian(bgd[8..], (uint)InodeTableBlock(g));  // bg_inode_table
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[12..], (ushort)freeBlocksInGroup); // bg_free_blocks_count
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[14..], (ushort)freeInodesInGroup); // bg_free_inodes_count
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[16..], (ushort)dirsPerGroup[g]);   // bg_used_dirs_count

      if (!checksums) continue;

      // Both bitmaps are summed over the part of them that means anything: one bit
      // per block the group holds, one per inode it holds.
      ExtChecksums.StampBitmap(bgd, descriptorSize, blockBitmap,
        Math.Min((blocksPerGroup + 7) / 8, blockSize), volumeSeed, isBlockBitmap: true);
      ExtChecksums.StampBitmap(bgd, descriptorSize, inodeBitmap,
        Math.Min((inodesPerGroup + 7) / 8, blockSize), volumeSeed, isBlockBitmap: false);
      ExtChecksums.StampGroupDescriptor(bgd, descriptorSize, (uint)g, volumeSeed);
    }

    // --- Superblock at offset 1024 ---
    var sb = img.At(1024, 1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)((long)inodesPerGroup * groupCount)); // s_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)totalBlocks);          // s_blocks_count
    // Five per cent held back for root, which is what mke2fs reserves unless told
    // otherwise; a volume reserving nothing is one nobody's mkfs made.
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], (uint)(totalBlocks / 20));   // s_r_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], (uint)totalFreeBlocks);     // s_free_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], (uint)totalFreeInodes);     // s_free_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], (uint)firstDataBlock);      // s_first_data_block
    var logBlockSize = blockSize == 1024 ? 0u : blockSize == 2048 ? 1u : 2u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], logBlockSize);              // s_log_block_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[28..], logBlockSize);              // s_log_frag_size (same)
    // s_blocks_per_group is the group's capacity, not the volume's size, and e2fsck
    // derives its bitmap-padding expectations from it: one block bitmap's worth of
    // bits, i.e. 8 * blockSize. A volume needing more than that gets more groups,
    // never a wider group.
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)blocksPerGroup);      // s_blocks_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], (uint)blocksPerGroup);      // s_frags_per_group (matches blocks_per_group)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], (uint)inodesPerGroup);      // s_inodes_per_group
    // Never mounted, so no time it last was — dumpe2fs reads a zero here as "n/a",
    // and a volume claiming a mount time with a mount count of nought is a
    // contradiction no mkfs writes.
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 0);                         // s_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(sb[48..], now);                       // s_wtime
    BinaryPrimitives.WriteUInt16LittleEndian(sb[52..], 0);                         // s_mnt_count
    // Minus one: no mount-count check. mkfs.ext4 has written that for twenty years,
    // and a volume asking to be checked every twenty mounts is one no current tool
    // produces.
    BinaryPrimitives.WriteInt16LittleEndian(sb[54..], -1);                         // s_max_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(sb[56..], ExtMagic);                  // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(sb[58..], 1);                         // s_state = CLEAN
    BinaryPrimitives.WriteUInt16LittleEndian(sb[60..], 1);                         // s_errors = CONTINUE
    BinaryPrimitives.WriteUInt16LittleEndian(sb[62..], 0);                         // s_minor_rev_level
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], now);                       // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 0);                         // s_checkinterval
    BinaryPrimitives.WriteUInt32LittleEndian(sb[72..], 0);                         // s_creator_os = Linux
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], RevLevelDynamic);           // s_rev_level = DYNAMIC_REV
    BinaryPrimitives.WriteUInt16LittleEndian(sb[80..], 0);                         // s_def_resuid
    BinaryPrimitives.WriteUInt16LittleEndian(sb[82..], 0);                         // s_def_resgid
    // The mount options a volume asks for by default, and when it was made. Every
    // ext volume mkfs makes asks for extended attributes and access lists, and
    // records the moment of its own creation; dumpe2fs prints both back, and a
    // volume that says "(none)" and has no creation date is one mkfs.ext4 did not
    // make.
    const uint defaultMountUserXattr = 0x0004;
    const uint defaultMountAcl = 0x0008;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[256..], defaultMountUserXattr | defaultMountAcl);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[264..], now);                      // s_mkfs_time
    // What making the volume cost in writes. mke2fs starts the tally at what it
    // laid down, and dumpe2fs reports it as the volume's lifetime writes.
    var kilobytesWritten = ((long)totalBlocks - totalFreeBlocks) * blockSize / 1024;
    BinaryPrimitives.WriteUInt64LittleEndian(sb[376..], (ulong)kilobytesWritten);  // s_kbytes_written
    // Dynamic-rev extension fields start at offset 84. s_first_ino tells
    // fsck which inode number user files may start at — without this set
    // (default 11 for GOOD_OLD_REV), any dirent pointing at inodes 3..10
    // is flagged as "invalid inode # reserved".
    BinaryPrimitives.WriteUInt32LittleEndian(sb[84..], FirstUserInode);            // s_first_ino
    BinaryPrimitives.WriteUInt16LittleEndian(sb[88..], (ushort)inodeSize);         // s_inode_size

    // Feature-flag composition by version:
    //   ext2 — FILETYPE only (compat/incompat clear of everything else).
    //   ext3 — FILETYPE + HAS_JOURNAL (compat).
    //   ext4 — FILETYPE + HAS_JOURNAL (compat) + EXTENTS (incompat).
    //   The read-only-compatible set below says what the volume may come to
    //   contain, not what it does: mke2fs turns all four on for every volume it
    //   makes, and a volume with none of them on is one no mke2fs made.
    uint compatFlags = FeatureCompatExtAttr | FeatureCompatDirIndex;
    if (reservedGdtBlocks > 0) compatFlags |= FeatureCompatResizeInode;
    var incompatFlags = FeatureIncompatFiletype;
    var roCompatFlags = FeatureRoCompatSparseSuper | FeatureRoCompatLargeFile
      | FeatureRoCompatHugeFile | FeatureRoCompatDirNlink;
    if (inodeSize > MinimumInodeSize) roCompatFlags |= FeatureRoCompatExtraIsize;
    if (version == ExtVersion.Ext3 || version == ExtVersion.Ext4) {
      if (journal) compatFlags |= FeatureCompatHasJournal;
    }
    if (version == ExtVersion.Ext4) {
      incompatFlags |= FeatureIncompatExtents | FeatureIncompat64Bit | FeatureIncompatFlexBg;
      BinaryPrimitives.WriteUInt16LittleEndian(sb[254..], (ushort)descriptorSize);  // s_desc_size
      sb[372] = (byte)logGroupsPerFlex;                                             // s_log_groups_per_flex
    }

    if (orphanFile) {
      compatFlags |= FeatureCompatOrphanFile;
      BinaryPrimitives.WriteUInt32LittleEndian(sb[640..], OrphanFileInode);        // s_orphan_file_inum
    }

    if (checksums) {
      // Everything on the volume carries a crc32c, and the seed they are salted
      // with is written down rather than left to be derived from the volume's
      // identity — which is what lets that identity be changed afterwards.
      roCompatFlags |= FeatureRoCompatMetadataCsum;
      incompatFlags |= FeatureIncompatCsumSeed;
      sb[373] = ChecksumTypeCrc32c;                                               // s_checksum_type
      BinaryPrimitives.WriteUInt32LittleEndian(sb[624..], volumeSeed);            // s_checksum_seed
    }
    BinaryPrimitives.WriteUInt32LittleEndian(sb[92..], compatFlags);               // s_feature_compat
    BinaryPrimitives.WriteUInt32LittleEndian(sb[96..], incompatFlags);             // s_feature_incompat
    BinaryPrimitives.WriteUInt32LittleEndian(sb[100..], roCompatFlags);            // s_feature_ro_compat
    BinaryPrimitives.WriteUInt16LittleEndian(sb[206..], (ushort)reservedGdtBlocks); // s_reserved_gdt_blocks

    // Directories may be indexed, so the volume carries what an index would be
    // hashed with: the scheme, and the seed no two volumes share. dumpe2fs prints
    // both back, and a volume that has neither is one mke2fs did not make.
    // mke2fs draws the seed the same way it draws the volume's identity, so it
    // reads back as a well-formed one; sixteen loose random bytes do not.
    Guid.NewGuid().ToByteArray(bigEndian: true).CopyTo(sb.Slice(236, 16));         // s_hash_seed
    sb[252] = HashVersionHalfMd4;                                                 // s_def_hash_version
    if (inodeSize > MinimumInodeSize) {
      BinaryPrimitives.WriteUInt16LittleEndian(sb[348..], ExtraIsizeBytes);        // s_min_extra_isize
      BinaryPrimitives.WriteUInt16LittleEndian(sb[350..], ExtraIsizeBytes);        // s_want_extra_isize
    }

    BinaryPrimitives.WriteUInt32LittleEndian(sb[352..], FlagSignedHash);          // s_flags

    // UUID at offset 104 (16 bytes) — blkid/dumpe2fs rely on this to identify
    // the filesystem. The kernel accepts any non-zero UUID at rev 0 (it becomes
    // mandatory at rev 1, which is harmless to set unconditionally).
    uuid.CopyTo(sb.Slice(104, 16));

    // Volume label at offset 120 (16 bytes). ASCII, NUL-padded; values longer
    // than 16 bytes are truncated, and any non-ASCII chars are dropped to fit
    // the dumpe2fs contract.
    if (!string.IsNullOrEmpty(volumeLabel)) {
      var labelBytes = Encoding.ASCII.GetBytes(volumeLabel);
      var labelSpan = sb.Slice(120, 16);
      labelSpan.Clear();
      labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 16)).CopyTo(labelSpan);
    }
    // Last-mount path at offset 136 (64 bytes) — optional.

    // Journal inode (offset 224) when HAS_JOURNAL is set.
    if ((compatFlags & FeatureCompatHasJournal) != 0) {
      BinaryPrimitives.WriteUInt32LittleEndian(sb[224..], JournalInode);             // s_journal_inum

      // A copy of where the journal is, kept in the superblock so a volume whose
      // inode table is lost can still be replayed. mke2fs writes it for every
      // journalled volume, and dumpe2fs reports its absence as plainly as its
      // presence.
      var journalInode = img.At(InodeOffset(JournalInode), inodeSize);
      journalInode.Slice(40, 60).CopyTo(sb.Slice(268, 60));                          // s_jnl_blocks[0..14] = i_block[0..14]
      journalInode.Slice(108, 4).CopyTo(sb.Slice(328, 4));                           // s_jnl_blocks[15] = i_size_high
      journalInode.Slice(4, 4).CopyTo(sb.Slice(332, 4));                             // s_jnl_blocks[16] = i_size
      sb[253] = JournalBackupBlocks;                                                 // s_jnl_backup_type
    }

    // The superblock's own sum, over everything ahead of where it sits, once every
    // other field is settled.
    if (checksums) ExtChecksums.StampSuperblock(sb);

    // --- Superblock and group-descriptor backups ---
    // A spare superblock and descriptor table in each of the groups SPARSE_SUPER
    // nominates. e2fsck needs them to be able to repair a volume whose primary
    // superblock is gone, and flags a missing one — or one in a group that should
    // not have it — outright.
    if (groupCount > 1) {
      var primarySuperblock = sb.ToArray();
      for (var g = 1; g < groupCount; ++g) {
        if (!HasSuperblock(g)) continue;

        var start = GroupStart(g);
        primarySuperblock.CopyTo(img.Block(start));
        BinaryPrimitives.WriteUInt16LittleEndian(img.At((long)start * blockSize + 90, 2), (ushort)g); // s_block_group_nr
        // A spare says which group it sits in, so its sum is not the primary's.
        if (checksums) ExtChecksums.StampSuperblock(img.At((long)start * blockSize, 1024));
        for (var d = 0; d < gdtBlocks; ++d)
          img.Block(GroupStart(0) + 1 + d).CopyTo(img.Block(start + 1 + d));
      }
    }

    return img;
  }

  /// <summary>
  /// How many blank descriptor-table blocks to keep behind the real ones, so the
  /// volume can be grown in place later.
  /// </summary>
  /// <remarks>
  /// Enough to describe a volume a thousand times this one, less what the real
  /// table already covers, and never more than one indirect block can address —
  /// which is the arithmetic mke2fs does, and it stops well short of a group.
  /// </remarks>
  private static int ReservedGdtBlocks(int totalBlocks, int firstDataBlock, int blocksPerGroup,
                                       int blockSize, int gdtBlocks, int addressesPerBlock,
                                       int descriptorSize) {
    var groupsPerGdtBlock = blockSize / descriptorSize;
    var grownBlocks = (long)totalBlocks * 1024;
    var grownGroups = (grownBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
    var reserved = (grownGroups + groupsPerGdtBlock - 1) / groupsPerGdtBlock - gdtBlocks;
    reserved = Math.Min(reserved, addressesPerBlock);
    // A group has to have room left for its bitmaps and inode table after them.
    reserved = Math.Min(reserved, blocksPerGroup / 4);
    return (int)Math.Max(0, reserved);
  }

  /// <summary>
  /// How many blocks the orphan file gets: a thousandth of the volume, never fewer
  /// than thirty-two nor more than a thousand and twenty-four, which is the
  /// arithmetic mke2fs does.
  /// </summary>
  private static int OrphanFileBlocks(int totalBlocks) => Math.Clamp(totalBlocks / 4096, 32, 1024);

  private const int GroupDescriptorSize = 32;
  private const int WideGroupDescriptorSize = 64;

  /// <summary>
  /// Whether block group <paramref name="group" /> keeps a spare superblock and
  /// descriptor table.
  /// </summary>
  /// <remarks>
  /// The first two groups do, and after those only the groups whose number is a
  /// power of three, five or seven — which thins the spares out to a handful
  /// however large the volume grows. This is what the SPARSE_SUPER feature names,
  /// and e2fsck checks the placement both ways: a group that should have a spare
  /// and hasn't is an error, and so is a group that has one and shouldn't.
  /// </remarks>
  private static bool HasSuperblock(int group) {
    if (group <= 1) return true;
    if ((group & 1) == 0) return false;   // every power of 3, 5 or 7 past 1 is odd

    return IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);
  }

  private static bool IsPowerOf(int value, int radix) {
    for (var power = radix; power <= value; power *= radix)
      if (power == value) return true;

    return false;
  }

  // Rounds the required inode count up to a sensible group size. The minimal
  // writer keeps a single block group, so the inode count is simply sized to
  // hold every reserved/dir/file inode with headroom, never below the classic 128.
  private static int ChooseInodeCount(int needed) {
    var withHeadroom = Math.Max(128, needed + needed / 10 + 16);
    // Round up to a multiple of 8 so the inode bitmap byte boundaries stay tidy.
    return withHeadroom + 7 & ~7;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives block-group geometry from the
  /// declared sizes of <see cref="AddStreamingFile"/> entries; pass 2 emits
  /// the superblock + BGD + bitmaps + inode table + directory blocks with
  /// file data blocks left zero, then streams each entry's bytes from its
  /// factory into its first allocated block via 64 KB chunks. Block tail
  /// past each entry's exact <c>Size</c> stays sparse-zero.
  /// </summary>
  /// <remarks>
  /// Partial coverage: only files small enough to fit in 12 contiguous direct
  /// blocks (ext writer's invariant for the streaming copy) are streamed
  /// contiguously; larger files still use the existing direct+indirect
  /// allocation path and stream into their contiguous run of allocated
  /// blocks since pass 1 allocates them in order. Entry CONTENTS never
  /// travel through a byte[] inside the writer.
  /// </remarks>
  public void BuildToStreaming(Stream output, int blockSize, int totalBlocks,
      ExtVersion version, bool journal, string volumeLabel, int inodeSize) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(IReadOnlyList<int> Blocks, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    SparseBlockImage image;
    try {
      image = BuildCore(blockSize, totalBlocks, version, journal, volumeLabel, inodeSize);
    } finally {
      this._streamingSink = null;
    }
    output.Position = 0;
    image.WriteTo(output);
    StreamEntries(output, sink, blockSize);
  }

  /// <summary>
  /// Pass 2 of a streaming build: copies each entry's bytes into the blocks it
  /// was allocated. The allocator hands out ascending blocks, so an entry's map
  /// is contiguous except where it steps over a group's metadata; writing it as
  /// runs keeps that to one seek per boundary rather than one per block.
  /// </summary>
  private static void StreamEntries(Stream output,
      List<(IReadOnlyList<int> Blocks, long Size, Func<Stream> Opener)> sink, int blockSize) {
    var buf = new byte[Math.Max(blockSize, 1024 * 1024)];
    foreach (var (blocks, size, opener) in sink) {
      if (size <= 0 || blocks.Count == 0) continue;
      using var src = opener();
      var remaining = size;

      var runStart = 0;
      while (runStart < blocks.Count && remaining > 0) {
        // A hole has no block to write to — block zero is the volume's own start,
        // and writing a file's bytes there would put them over the superblock. The
        // source still has to be walked past the bytes the hole stands for.
        if (blocks[runStart] == 0) {
          var skip = (int)Math.Min(remaining, blockSize);
          remaining -= skip;
          while (skip > 0) {
            var n = src.Read(buf, 0, Math.Min(buf.Length, skip));
            if (n <= 0) break;
            skip -= n;
          }
          ++runStart;
          continue;
        }

        var runEnd = runStart;
        while (runEnd + 1 < blocks.Count && blocks[runEnd + 1] != 0
               && blocks[runEnd + 1] == blocks[runEnd] + 1) ++runEnd;

        var runBytes = Math.Min(remaining, (long)(runEnd - runStart + 1) * blockSize);
        output.Position = (long)blocks[runStart] * blockSize;
        while (runBytes > 0) {
          var want = (int)Math.Min(buf.Length, runBytes);
          var n = src.Read(buf, 0, want);
          if (n <= 0) { remaining = 0; break; }
          output.Write(buf, 0, n);
          runBytes -= n;
          remaining -= n;
        }
        runStart = runEnd + 1;
      }
      // Block tail past `size` retains zero: the writer never wrote those bytes.
    }
    output.Flush();
  }

  /// <summary>Two-pass streaming Build with auto-sized geometry.</summary>
  public void BuildToStreamingAutoSized(Stream output, ExtVersion version, bool journal,
      string volumeLabel, int inodeSize)
    => this.BuildToStreamingAutoSized(output, 0, version, journal, volumeLabel, inodeSize);

  /// <summary>Two-pass streaming Build with auto-sized geometry and a caller-chosen block size.</summary>
  public void BuildToStreamingAutoSized(Stream output, int requestedBlockSize, ExtVersion version,
      bool journal, string volumeLabel, int inodeSize) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreamingAutoSized requires a writable, seekable stream.", nameof(output));

    var (blockSize, totalBlocks) = PlanAutoSize(requestedBlockSize, version, journal, inodeSize);
    this.BuildToStreaming(output, blockSize, totalBlocks, version, journal, volumeLabel, inodeSize);
  }

  // Counts the distinct directories (root + every path prefix) the added files imply.
  private int CountDirectories() {
    var dirs = new HashSet<string> { "" };
    foreach (var entry in _files) {
      var segments = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      var prefix = "";
      for (var s = 0; s < segments.Length - 1; ++s) {
        prefix = prefix.Length == 0 ? segments[s] : prefix + "/" + segments[s];
        dirs.Add(prefix);
      }
    }
    return dirs.Count;
  }

  // A node in the directory tree assembled from the added file paths. Each node
  // becomes one directory inode whose entries span one or more data blocks.
  private sealed class DirNode {
    public uint Inode;
    public uint Parent;
    public string Name = "";
    public int Block;
    public readonly Dictionary<string, DirNode> Subdirs = [];
    public readonly List<(string Name, uint Inode)> Files = [];
  }

  // Depth-first walk that yields the root first, then each subdirectory, so the
  // root is laid out before its children (mirroring how mkfs orders inodes).
  private static void CollectDirs(DirNode node, List<DirNode> into) {
    into.Add(node);
    foreach (var child in node.Subdirs.Values)
      CollectDirs(child, into);
  }

  private static int WriteDirEntry(byte[] dirData, int pos, uint inode, string name, byte fileType, int usableEnd, bool isLast) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = (8 + nameBytes.Length + 3) & ~3;
    // The last record of a block claims the rest of it, which on a summed volume
    // stops short of the tail rather than at the block's end.
    var recLen = isLast ? usableEnd - pos : entrySize;

    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4), (ushort)recLen);
    dirData[pos + 6] = (byte)nameBytes.Length;
    dirData[pos + 7] = fileType;
    nameBytes.CopyTo(dirData, pos + 8);
    return pos + entrySize;
  }

  // Marks a block as "used" in the block bitmap. The bitmap's bit 0 refers to
  // block s_first_data_block (1 on 1 KiB filesystems, 0 otherwise), so the
  // caller-supplied absolute block number must be biased by firstDataBlock
  // before indexing — otherwise every bit is off by one and e2fsck reports a
  // block-bitmap difference plus a wrong free-block count.

  /// <summary>
  /// Maps <paramref name="dataBlocks" /> into an inode's block map: twelve direct
  /// pointers, then single-, double- and triple-indirect blocks, allocating each
  /// pointer block from the free pool and marking it used.
  /// </summary>
  /// <returns>
  /// How many pointer blocks were consumed. They count toward i_blocks, which
  /// e2fsck tallies in 512-byte sectors.
  /// </returns>
  /// <remarks>
  /// Files were previously limited to direct + single-indirect, capping one file
  /// at 12 + blockSize/4 blocks -- 274 KB at a 1 KB block. The classic map is valid
  /// on ext4 too: the EXTENTS feature permits per-inode extent maps, it does not
  /// require them.
  /// </remarks>
  private static int WriteInodeBlockMap(SparseBlockImage img, long inodeOffset, IReadOnlyList<int> dataBlocks,
      int blockSize, Func<int> allocBlock) {
    const int directCount = 12;
    var ptrs = blockSize / 4;
    var meta = 0;

    for (var i = 0; i < directCount && i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 40 + i * 4, 4), (uint)dataBlocks[i]);
    if (dataBlocks.Count <= directCount) return meta;

    int AllocPointerBlock() {
      var b = allocBlock();
      ++meta;
      return b;
    }

    // Single indirect: i_block[12] at inode offset 88.
    var ind = AllocPointerBlock();
    var indOff = (long)ind * blockSize;
    for (var i = 0; i < ptrs && directCount + i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(indOff + i * 4, 4), (uint)dataBlocks[directCount + i]);
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 88, 4), (uint)ind);

    var dindFirst = directCount + ptrs;
    if (dataBlocks.Count <= dindFirst) return meta;

    // Double indirect: i_block[13] at inode offset 92.
    var dind = AllocPointerBlock();
    var dindOff = (long)dind * blockSize;
    var dindCapacity = (long)ptrs * ptrs;
    var dindBlocks = (int)Math.Min(dataBlocks.Count - dindFirst, dindCapacity);
    var dindGroups = (dindBlocks + ptrs - 1) / ptrs;
    for (var k = 0; k < dindGroups; ++k) {
      var lvl2 = AllocPointerBlock();
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(dindOff + k * 4, 4), (uint)lvl2);
      var lvl2Off = (long)lvl2 * blockSize;
      for (var i = 0; i < ptrs; ++i) {
        var idx = dindFirst + k * ptrs + i;
        if (idx >= dindFirst + dindBlocks) break;
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(lvl2Off + i * 4, 4), (uint)dataBlocks[idx]);
      }
    }
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 92, 4), (uint)dind);

    var tindFirst = dindFirst + (int)dindCapacity;
    if (dataBlocks.Count <= tindFirst) return meta;

    // Triple indirect: i_block[14] at inode offset 96.
    var tind = AllocPointerBlock();
    var tindOff = (long)tind * blockSize;
    var remaining = dataBlocks.Count - tindFirst;
    var lvl2Groups = (int)(((long)remaining + (long)ptrs * ptrs - 1) / ((long)ptrs * ptrs));
    for (var g = 0; g < lvl2Groups; ++g) {
      var mid = AllocPointerBlock();
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(tindOff + g * 4, 4), (uint)mid);
      var midOff = (long)mid * blockSize;
      for (var k = 0; k < ptrs; ++k) {
        var groupStart = tindFirst + (g * ptrs + k) * ptrs;
        if (groupStart >= dataBlocks.Count) break;
        var leaf = AllocPointerBlock();
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(midOff + k * 4, 4), (uint)leaf);
        var leafOff = (long)leaf * blockSize;
        for (var i = 0; i < ptrs; ++i) {
          var idx = groupStart + i;
          if (idx >= dataBlocks.Count) break;
          BinaryPrimitives.WriteUInt32LittleEndian(img.At(leafOff + i * 4, 4), (uint)dataBlocks[idx]);
        }
      }
    }
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 96, 4), (uint)tind);
    return meta;
  }
}
