#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ext1;

/// <summary>
/// Descriptor for ext1 filesystem images — the 1992 predecessor of ext2 by Rémy Card.
/// ext1's on-disk superblock layout is identical to the GOOD_OLD-revision ext2 superblock
/// with one crucial difference: the s_magic field at offset 56 of the superblock
/// (file-relative offset 1080) reads <c>0xEF51</c> instead of ext2's <c>0xEF53</c>.
/// ext1 has no journal, no extents, and no FEATURE_INCOMPAT_FILETYPE — directory entries
/// are 8-byte fixed-header (with a 16-bit <c>name_len</c>) + name only. Detection,
/// structural surfacing and round-trip read+write of small WORM images are supported;
/// vintage pre-1993 Linux disk images and forensic tooling for early Linux installs
/// are the consumers.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://e2fsprogs.sourceforge.net/ext2intro.html</c> — Card/Ts'o/Tweedie, "Design and Implementation of the Second Extended Filesystem", which documents the original ext it replaced</description></item>
///   <item><description><c>https://mirrors.edge.kernel.org/pub/linux/kernel/Historic/</c> — historic kernel trees whose <c>fs/ext</c> is the primary source for the 1992 layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Extended_file_system</c> — Wikipedia article on the original ext</description></item>
/// </list>
/// </summary>
public sealed class Ext1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// The single tunable ext1 honours: the on-disk block size
  /// (<c>s_log_block_size</c>). 1024/2048/4096 bytes are the legal rev-0 values;
  /// the 4 MiB image footprint stays constant across the choice. ext1 is the
  /// GOOD_OLD revision and stores no volume name (<c>s_volume_name</c> only
  /// exists in the dynamic revision), so no label knob is offered.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BlockSize", DisplayName: "Block Size (bytes)", Kind: FormatOptionKind.Integer, Default: "1024",
      AllowedValues: ["1024", "2048", "4096"],
      Description: "ext1 block size (s_log_block_size). The 4 MiB image footprint is constant; larger blocks mean fewer total blocks."),
  ];

  /// <summary>
  /// Walks the rev-0 superblock + BGD table + inode tree and yields the
  /// actual on-disk byte layout — every metadata region (SB, BGDT, block +
  /// inode bitmaps, inode table) plus one extent per contiguous block run
  /// per file. Used by the defragment window's block-map preview.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Ext1ExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the ext1 image: free blocks, inter-file gaps and
  /// the block-tip slack between a file's logical size and the end of its last
  /// allocated 1024-byte block. The extent map clamps each file's run to its
  /// logical byte length, so any trailing slack inside the final block presents
  /// as a free gap that the generic <see cref="UnusedSpaceWiper"/> zero-fills.
  /// A directory-path-keyed size lookup makes the explicit cluster-tip pass
  /// exact for the (rare) case where an extent reports a block-rounded length.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new Ext1Reader(image);
        // Ext1 extent FileName is the full directory path; the reader keys
        // entries by the same full path — they match directly.
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = Ext1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new Ext1BlockMover();
    mover.Init(image);
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new Ext1BlockMover();
    mover.Init(image);
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ext1";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ext1";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Ext1 image. Uses
  /// <see cref="Ext1Modifier"/> for true O(touched bytes) random-access I/O —
  /// only the superblock, BGD entry, block + inode bitmaps, the affected inode
  /// slot, the root dir block, and the file's data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this, SyntheticNames);
      return;
    }

    foreach (var (name, data) in FilesOnly(inputs)) {
      // Replace-by-name semantics — drop any prior entry with the same name first.
      Ext1Modifier.RemoveFile(archive, name, wipeData: true);
      Ext1Modifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Ext1 image. Uses
  /// <see cref="Ext1Modifier"/> for O(touched bytes) random-access I/O — file
  /// data blocks are wiped during removal so no forensic trace remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this, SyntheticNames);
      return;
    }

    foreach (var name in entryNames)
      Ext1Modifier.RemoveFile(archive, name, wipeData: true);
  }

    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ext1";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ext1"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    // ext1 magic 0xEF51 (LE u16) at file offset 1080 = superblock_offset (1024)
    // + s_magic field offset (56). Same slot as ext2's 0xEF53.
    [new([0x51, 0xEF], Offset: 1080, Confidence: 0.9f)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "ext1 (1992) Linux filesystem image — round-trip WORM, no Linux mkfs.";

  // ext1 superblock geometry — identical layout to GOOD_OLD ext2.
  private const int SuperblockOffset = 1024;
  private const int SuperblockMinSize = 264;
  private const ushort Ext1Magic = 0xEF51;

  // --- IArchiveWriteConstraints ---
  // Default writer geometry: 1024-byte blocks × 4096 blocks = 4 MiB image.
  // 12 direct block pointers × 1024 = 12 KiB max per file; 117 user inodes
  // (128 - 11 reserved) bound the file count. The advertised cap reflects the
  // total image size — well under the WORM ceiling but matches what writer
  // produces.
    /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => 4L * 1024 * 1024;
    /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => 4L * 1024 * 1024;
    /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "ext1 WORM: flat root directory, each file ≤ 4 GiB (rev-0 layout; i_size is a uint32).";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = "ext1 writer is flat WORM — directory entries are not preserved.";
      return false;
    }
    try {
      var size = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      // The block map reaches far past the direct pointers now; what a rev-0
      // inode cannot express is a size that does not fit i_size, a uint32.
      if (size > uint.MaxValue) {
        reason = $"file '{input.ArchiveName}' exceeds the 4 GiB an ext1 inode can record.";
        return false;
      }
    } catch { /* size unknown — accept and let writer decide */ }
    reason = null;
    return true;
  }

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    // Same reason as Extract: ReadAll stops at HeaderReadCap, so a larger volume
    // has to be walked in place or its entries come back empty.
    if (stream.CanSeek && stream.Length > HeaderReadCap)
      return ListStreaming(stream);

    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Ext1Superblock sb;
    try {
      sb = Ext1Superblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));

    // Surface user files when the image is structurally walkable. This is best-effort
    // — synthetic test images or partial superblocks fall through silently.
    if (sb.Valid) {
      try {
        using var ms = new MemoryStream(image);
        using var reader = new Ext1Reader(ms);
        var idx = entries.Count;
        foreach (var e in reader.Entries) {
          if (e.IsDirectory) continue;
          entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, e.LastModified));
        }
      } catch { /* best-effort; reader failure leaves only surface entries */ }
    }

    return entries;
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Ext1Writer();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Names are flattened to their leaf: ext1 as written here has a flat root.
      // Only the length is needed to lay the volume out, so a large input is
      // handed over as a stream factory rather than read into a byte[].
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    var blockSize = options.GetOptionInt("BlockSize", 1024);
    // Sized to the payload rather than to the canonical 4 MiB footprint, which
    // WriteTo still emits: anything larger than that used to run the allocator dry.
    var totalBlocks = w.PlanTotalBlocks(blockSize);
    if (output.CanSeek)
      w.BuildTo(output, blockSize, totalBlocks);
    else
      output.Write(w.Build(blockSize, totalBlocks));
  }

  /// <summary>
  /// Streaming creation: each input's length settles the layout, then its bytes
  /// are copied into the blocks it was allocated, so an entry past what a byte[]
  /// can hold never has to be materialised.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs,
                                FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!output.CanSeek)
      throw new ArgumentException("ext1 streaming creation requires a seekable output.", nameof(output));

    var w = new Ext1Writer();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(Path.GetFileName(input.Name), input.Size, input.OpenStream);
    }
    var blockSize = options.GetOptionInt("BlockSize", 1024);
    w.BuildTo(output, blockSize, w.PlanTotalBlocks(blockSize));
  }

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ext1 defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer emits a fresh contiguous-from-start
  /// rev-0 layout at the source volume's own block size and block count -- the
  /// canonical 4 MiB footprint WriteTo produces only fits a few megabytes, so
  /// rebuilding into it dropped everything a larger volume held.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving the blocks that are in the wrong place beats writing the volume
    // out again: a rebuild reads and rewrites every file to fix a handful of
    // runs. The planner needs the layout and something that can relink a file
    // after its bytes have moved, and ext1 has both. Anything it refuses — a
    // volume fragmented past what it will resolve, a plan it cannot order
    // safely — falls through to the rebuild below.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        // A silent fallback looks exactly like a successful in-place
        // defragmentation from outside, so the reason is reported.
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{FirstLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }

    var (blockSize, totalBlocks) = ReadGeometry(archive);

    // A volume too large to materialise goes through the streaming rebuilder,
    // whichever mode was asked for: end-pack and carve-hole order their entries
    // from scratch inside it rather than falling back to a buffered rebuild.
    if (archive.CanSeek && archive.Length > int.MaxValue / 2) {
      Ext1Writer? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new Ext1Reader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new Ext1Writer(); target = s2; },
        writeEntry: (name, data) => streamWriter!.AddFile(name, data),
        finishWrite: () => streamWriter!.BuildTo(target!, blockSize, totalBlocks));
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new Ext1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new Ext1Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(blockSize, totalBlocks);
      });
  }

  /// <summary>Block size and block count declared by the volume's own superblock.</summary>
  private static (int BlockSize, int TotalBlocks) ReadGeometry(Stream archive) {
    var position = archive.Position;
    try {
      var sb = new byte[1024];
      archive.Position = 1024;
      archive.ReadExactly(sb);
      var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
      var blockSize = 1024 << (int)Math.Min(logBlockSize, 2);
      var totalBlocks = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4)));
      return (blockSize, Math.Max(totalBlocks, 4 * 1024 * 1024 / blockSize));
    } catch {
      return (1024, 4096);
    } finally {
      archive.Position = position;
    }
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // A seekable image is walked in place. ReadAll stops at HeaderReadCap, so
    // buffering silently truncated anything larger -- and FULL.ext1 would double
    // the image in memory on top of that.
    if (stream.CanSeek && stream.Length > HeaderReadCap) {
      ExtractStreaming(stream, outputDir, files);
      return;
    }

    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    Ext1Superblock sb;
    try {
      sb = Ext1Superblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.ext1", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.ext1", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);

    // Best-effort: extract user files when the image is walkable.
    if (sb.Valid) {
      try {
        using var ms = new MemoryStream(image);
        using var reader = new Ext1Reader(ms);
        foreach (var e in reader.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          WriteFile(outputDir, e.Name, reader.Extract(e));
        }
      } catch { /* synthetic / unwalkable image — surface entries are enough */ }
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Ext1Superblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{sb.Magic:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"inodes_count={sb.InodesCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_count={sb.BlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"r_blocks_count={sb.ReservedBlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"free_blocks_count={sb.FreeBlocksCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"free_inodes_count={sb.FreeInodesCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"first_data_block={sb.FirstDataBlock}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"log_block_size={sb.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={1024U << (int)sb.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_per_group={sb.BlocksPerGroup}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"inodes_per_group={sb.InodesPerGroup}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded read — superblock parsing only needs the first ~1.3 KiB, but we
  // give ourselves headroom while keeping carver scans capped. For round-trip
  // reads we want the full image — bumped to cover the 4 MiB writer default.
  private const int HeaderReadCap = 8 * 1024 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  /// <summary>
  /// Inlined ext1 superblock parser. Layout matches the GOOD_OLD ext2/3/4
  /// header byte-for-byte; only the magic differs (0xEF51 vs 0xEF53). We parse
  /// the fixed-header fields up through s_inodes_per_group at offset 40.
  /// </summary>
  internal sealed class Ext1Superblock {
    public bool Valid { get; init; }
    public ushort Magic { get; init; }
    public uint InodesCount { get; init; }
    public uint BlocksCount { get; init; }
    public uint ReservedBlocksCount { get; init; }
    public uint FreeBlocksCount { get; init; }
    public uint FreeInodesCount { get; init; }
    public uint FirstDataBlock { get; init; }
    public uint LogBlockSize { get; init; }
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public byte[] RawBytes { get; init; } = [];

    public static Ext1Superblock TryParse(ReadOnlySpan<byte> image) {
      if (image.Length < SuperblockOffset + SuperblockMinSize) return new Ext1Superblock();
      var sb = image.Slice(SuperblockOffset);
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56, 2));
      if (magic != Ext1Magic) return new Ext1Superblock();

      // Snapshot the canonical 1024-byte superblock for the surface entry.
      var rawLen = Math.Min(1024, sb.Length);
      var raw = sb.Slice(0, rawLen).ToArray();

      var inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0, 4));
      var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4, 4));
      var reserved = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(8, 4));
      var freeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(12, 4));
      var freeInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(16, 4));
      var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20, 4));
      var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24, 4));
      var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32, 4));
      var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40, 4));

      return new Ext1Superblock {
        Valid = true,
        Magic = magic,
        InodesCount = inodesCount,
        BlocksCount = blocksCount,
        ReservedBlocksCount = reserved,
        FreeBlocksCount = freeBlocks,
        FreeInodesCount = freeInodes,
        FirstDataBlock = firstData,
        LogBlockSize = logBlock,
        BlocksPerGroup = blocksPerGroup,
        InodesPerGroup = inodesPerGroup,
        RawBytes = raw,
      };
    }
  }
  /// <summary>
  /// Extraction for an image too large to buffer: FULL.ext1 is copied through,
  /// and each entry is streamed straight out of the volume.
  /// </summary>
  private static void ExtractStreaming(Stream stream, string outputDir, string[]? files) {
    // TryParse indexes from the start of the image, so hand it the leading 2 KB.
    var head = new byte[2048];
    stream.Position = 0;
    stream.ReadExactly(head);
    var parsed = Ext1Superblock.TryParse(head);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.ext1", files)) {
      stream.Position = 0;
      using var full = CreateEntryFile(outputDir, "FULL.ext1");
      stream.CopyTo(full);
    }
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(parsed), files);
    if (parsed.Valid)
      WriteIfMatch(outputDir, "superblock.bin", parsed.RawBytes, files);

    if (!parsed.Valid) return;
    try {
      stream.Position = 0;
      using var reader = new Ext1Reader(stream);
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        using var target = CreateEntryFile(outputDir, e.Name);
        reader.ExtractTo(e, target);
      }
    } catch { /* synthetic / unwalkable image — surface entries are enough */ }
  }

  /// <summary>Listing for an image too large to buffer: the volume is walked in place.</summary>
  private static List<ArchiveEntryInfo> ListStreaming(Stream stream) {
    var entries = new List<ArchiveEntryInfo>();
    var head = new byte[2048];
    stream.Position = 0;
    stream.ReadExactly(head);
    var parsed = Ext1Superblock.TryParse(head);

    entries.Add(new ArchiveEntryInfo(0, "FULL.ext1", stream.Length, stream.Length, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (parsed.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", parsed.RawBytes.LongLength, parsed.RawBytes.LongLength, "stored", false, false, null));
    if (!parsed.Valid) return entries;

    try {
      stream.Position = 0;
      using var reader = new Ext1Reader(stream);
      var idx = entries.Count;
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", false, false, e.LastModified));
      }
    } catch { /* best-effort; reader failure leaves only surface entries */ }
    return entries;
  }


  /// <summary>
  /// The entries this reader surfaces that are not files on the volume — the
  /// raw image and its triage sheets — so an image the parser cannot fully
  /// walk still yields something useful.
  /// </summary>
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.ext1", "metadata.ini", "superblock.bin" };

  /// <summary>
  /// Re-lays the volume out with the requested geometry. The generic default
  /// wrote the synthetic entries back as files, so the rebuilt volume listed
  /// more entries than the original and the rebuild was refused.
  /// </summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    if (options.Parameters != null)
      foreach (var kv in options.Parameters)
        parameters[kv.Key] = kv.Value;

    RebuildVerb.RebuildToStream(source, target, this, this,
      parameters.Count > 0 ? parameters : null, SyntheticNames);
  }


  /// <summary>
  /// Moves only what is out of place, relinking each file's inode as its blocks
  /// arrive at their new positions.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Ext1BlockMover();
    mover.Init(archive);

    // The map yields a run per block on a volume laid out block by block, and
    // the planner refuses anything near this many, so stop counting there
    // rather than materialising millions of entries first.
    var extents = Ext1ExtentMap.Enumerate(archive).Take(MaxPlannerExtents + 1).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves, archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = Ext1ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Extents past which the planner refuses; see <see cref="Compression.Core.Layout.DefragPlanner" />.</summary>
  private const int MaxPlannerExtents = 65536;


  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
