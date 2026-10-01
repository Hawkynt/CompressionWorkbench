#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ocfs2;

/// <summary>
/// R/W descriptor for OCFS2 (Oracle Cluster Filesystem 2).
/// Supports: list, extract, create, true in-place modify (Add/Replace/Remove via
/// <see cref="Ocfs2InPlaceModifier"/>), defragment, extent map.
///
/// <para><b>Reading</b> is spec-correct against <c>fs/ocfs2/ocfs2_fs.h</c> (see
/// <see cref="Ocfs2Reader"/>): INODE01 dinode signatures, the real
/// <c>ocfs2_dinode</c> field offsets, the 8-byte <c>ocfs2_inline_data</c> header,
/// and the 16-byte extent-list header. It reads images produced by the reference
/// <c>mkfs.ocfs2</c> as well as the toolkit's own writer (verified by an external
/// conformance test that reads a real <c>mkfs.ocfs2 -M local</c> volume).</para>
///
/// <para><b>Writing</b> produces a single-node ("local", no DLM) volume with 4 KB
/// blocks/clusters, the full system-file suite (chain allocators split into
/// cluster groups, a JBD2 journal of at least the kernel's 4 MiB minimum, slot
/// map, local alloc, truncate log, orphan dir), inline or extent-backed
/// directories and files. The Linux kernel driver mounts it and reads the tree
/// back byte for byte, and <c>fsck.ocfs2 -fn</c> passes it before and after a
/// kernel read-write mount (see <c>Ocfs2KernelMountTests</c>).</para>
///
/// <para><b>Editing</b> is in place or refused: Add/Replace/Remove work on regular
/// files in an inline root directory, on this package's volumes and on
/// <c>mkfs.ocfs2</c>'s; defragmentation moves single-run files in place. Anything
/// else — a nested path, an extent-backed root, a file sharing extents — raises
/// <see cref="NotSupportedException"/> and leaves the image as it was. Nothing is
/// rebuilt, because a rebuild would draw a new UUID and reset every timestamp.
/// DLM/heartbeat lockdown and multi-node cluster semantics are out of scope by
/// design.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/fs/ocfs2/ocfs2_fs.h</c> — canonical on-disk header</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/ocfs2.html</c> — kernel documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OCFS2</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
[FilesystemBlockMover(typeof(Ocfs2BlockMover))]
public sealed class Ocfs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty,
      IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The tunables the writer honours: the volume label written into
  /// <c>s_label</c> (64-byte superblock field) via <see cref="Ocfs2Writer.SetLabel"/>
  /// and read back as <c>Ocfs2Superblock.Label</c>, and the image size — free
  /// space left for files added in place later, which never grow the volume. The
  /// 4&#160;KB block/cluster size is fixed by the single-node layout, so it is not
  /// exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 63),
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB", "256 MB", "512 MB"]),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Ocfs2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "OCFS2 (Oracle Cluster Filesystem 2)";
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
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".ocfs2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".ocfs2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ocfs2Superblock.SignatureBytes, Offset: (int)Ocfs2Superblock.DefaultSuperBlockOffset, Confidence: 0.85),
  ];
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
  public string Description =>
    "OCFS2 (Oracle Cluster Filesystem 2) — spec-correct reader that parses real "
    + "mkfs.ocfs2 volumes as well as our own; single-node (local) writer whose volumes "
    + "the Linux kernel mounts and fsck.ocfs2 passes, before and after a kernel "
    + "read-write mount. In-place Add/Replace/Remove of root-directory files and "
    + "in-place defragmentation; nested paths, extent-backed roots and shared extents "
    + "are refused rather than rebuilt. Single-node only — DLM/heartbeat lockdown and "
    + "multi-node cluster semantics are out of scope.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    // Try the writer-produced image path first (has real file entries)
    try {
      var image = ReadAllFull(stream);
      var files = ReadFilesFromImage(image);
      if (files.Count > 0)
        return files.Select((f, i) => new ArchiveEntryInfo(
          i, f.Name, f.Data.LongLength, f.Data.LongLength, "stored", false, false, null
        )).ToList();
    } catch {
      // Fall through to triage path
    }

    // Reset stream position for triage path
    if (stream.CanSeek) stream.Position = 0;
    // Triage path: superblock surface only
    return ListTriage(stream);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // A volume too large to buffer is walked from its metadata head and each
    // file streamed out of the extent its dinode records.
    if (stream.CanSeek && stream.Length > MaxBufferedImageBytes) {
      ExtractStreaming(stream, outputDir, files);
      return;
    }

    // Try the writer-produced image path first
    try {
      var image = ReadAllFull(stream);
      var fileEntries = ReadFilesFromImage(image);
      if (fileEntries.Count > 0) {
        foreach (var f in fileEntries) {
          if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
          WriteFile(outputDir, f.Name, f.Data);
        }
        return;
      }
    } catch {
      // Fall through to triage path
    }

    // Reset stream position for triage path
    if (stream.CanSeek) stream.Position = 0;
    ExtractTriage(stream, outputDir, files);
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Ocfs2Writer();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      w.SetLabel(label);
    w.SetMinimumSize(FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", "")));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      // The full archive path: the writer builds the directories it names.
      var name = info.ArchiveName;
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (true in-place R/W) ────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files in the root directory of an existing
  /// OCFS2 volume using <see cref="Ocfs2InPlaceModifier"/>, which touches only
  /// the blocks the change moves. A nested path, an extent-backed root, a full
  /// inline area or a volume without room is refused before anything is written:
  /// relaying the volume out instead would give it a new UUID and new timestamps.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      try {
        Ocfs2InPlaceModifier.AddOrReplaceFile(archive, name.Replace('\\', '/').Trim('/'), data);
      } catch (IOException ex) {
        throw new NotSupportedException($"OCFS2: '{name}' cannot be added in place: {ex.Message}", ex);
      }
    }
  }

  /// <summary>
  /// Removes files from the root directory of an existing OCFS2 volume using
  /// <see cref="Ocfs2InPlaceModifier"/>: clusters and inode bits go back to their
  /// allocators and the blocks are zeroed. A name that is not a regular file in
  /// the root directory is refused.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var paths = entryNames.Select(n => n.Replace('\\', '/').Trim('/')).ToArray();
    if (paths.FirstOrDefault(p => p.Contains('/')) is { } nested)
      throw new NotSupportedException($"OCFS2: '{nested}' is not in the root directory; only root-directory files are removed in place.");
    foreach (var (name, path) in entryNames.Zip(paths)) {
      if (!Ocfs2InPlaceModifier.RemoveFile(archive, path, wipeData: true))
        throw new FileNotFoundException($"OCFS2: the root directory has no '{name}'.", name);
    }
  }

  // ── IArchiveDefragmentable ────────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The layout is changed by moving what is out of place: a movable file is
    // one extent record in its own dinode, so a move is the copy, eight bytes,
    // and the bitmap bits. Files with several runs, holes or an extent tree are
    // pinned where they are.
    //
    // The pass is kept only if every payload still reads back; otherwise the
    // image is restored and the request refused. A rebuild is not an answer: it
    // would draw a new UUID and reset every timestamp on the volume.
    if (!archive.CanSeek || archive.Length > MaxBufferedImageBytes)
      throw new NotSupportedException(
        $"OCFS2: in-place defragmentation holds the volume in memory to verify it, which stops at {MaxBufferedImageBytes:N0} bytes.");

    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadFileEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => DefragmentWithPlanner(archive, options),
      rebuild: () => throw new NotSupportedException(
        "OCFS2: the requested layout cannot be reached by moving single-run files in place; the volume is unchanged."));
  }

  // ── IFilesystemExtentMap ──────────────────────────────────────────────

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    byte[] head;
    try {
      // The head carries the superblock, dinodes and directory blocks. Buffering
      // the whole volume capped the wipe at the array limit, and the empty list
      // that came back on failure reads as "the volume is entirely free".
      image.Position = 0;
      head = new byte[(int)Math.Min(image.Length, MaxBufferedImageBytes)];
      image.ReadExactly(head, 0, head.Length);
      image.Position = 0;
    } catch {
      return [];
    }

    try {
      return EnumerateExtentsCore(image, head);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      // A map that cannot be trusted must not read as "everything is free".
      return [new DefragBlockInfo(0, image.Length, DefragBlockKind.MetadataReserved, "Unmapped")];
    } finally {
      image.Position = 0;
    }
  }

  /// <summary>
  /// The volume's extent map. A single-run file surfaces as a Used extent clamped
  /// to its logical size, so its cluster tip reads as a gap; every other cluster
  /// the global bitmap marks allocated — in any of its groups — is reserved:
  /// system metadata, group descriptors, directory blocks, and the data of files
  /// that are not one run (several runs, holes, an extent tree), which stay where
  /// they are.
  /// </summary>
  private static List<DefragBlockInfo> EnumerateExtentsCore(Stream image, byte[] head) {
    const int blockSize = Ocfs2Writer.BlockSize;
    var result = new List<DefragBlockInfo>();
    if (head.Length < (Ocfs2Writer.SuperBlockBlkno + 1) * blockSize) return result;

    var movable = new Dictionary<long, (long Size, long Blocks, string Name)>();
    foreach (var f in Ocfs2Reader.ReadFilePlacements(head))
      if (f.IsSingleRun && f.Size > 0)
        movable[f.DataBlkno] = (f.Size, f.Extents[0].Blocks, f.Name);

    var volume = Ocfs2Allocators.Open(image);
    var total = Math.Min(volume.TotalClusters, image.Length / blockSize);
    var used = volume.ReadClusterBitmap(total);

    for (long cluster = 0; cluster < total;) {
      if (movable.TryGetValue(cluster, out var file)) {
        // Used portion clamped to logical size; the tip of its last cluster is a gap.
        result.Add(new DefragBlockInfo(cluster * blockSize, file.Size, DefragBlockKind.Used, file.Name));
        var sized = (file.Size + blockSize - 1) / blockSize;
        if (file.Blocks > sized)
          result.Add(new DefragBlockInfo((cluster + sized) * blockSize, (file.Blocks - sized) * blockSize,
            DefragBlockKind.MetadataReserved, "Allocated"));
        cluster += file.Blocks;
        continue;
      }
      if (!used[(int)cluster]) { ++cluster; continue; }
      var runStart = cluster;
      while (cluster < total && used[(int)cluster] && !movable.ContainsKey(cluster)) ++cluster;
      result.Add(new DefragBlockInfo(runStart * blockSize, (cluster - runStart) * blockSize,
        DefragBlockKind.MetadataReserved, runStart < Ocfs2Writer.FirstFileBlkno ? "SystemMetadata" : "Allocated"));
    }
    return result;
  }

  // ── IWipeEmpty ────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the OCFS2 image: unallocated clusters and the
  /// cluster-tip slack between a file's logical size and the end of its last
  /// 4 KB cluster. The extent map already clamps each Used data extent to the
  /// file's logical length, so the cluster tip surfaces as a free gap and is
  /// zeroed by the generic wiper without a size lookup. Small directories are
  /// stored inline inside their dinode (no data cluster), so they have no tip.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = EnumerateExtents(image);

    // No fileSizeLookup is needed: the OCFS2 extent map reports each file's
    // Used data extent at its logical length, leaving the cluster tip exposed
    // as a free gap that Wipe zero-fills.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup: null);
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Ocfs2BlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var descriptor = new Ocfs2FormatDescriptor();
    var extents = descriptor.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);
    mover.CommitAllocation(archive);

    archive.Position = 0;
    var postExtents = descriptor.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    // A volume too large to buffer is walked from its metadata head, with each
    // file's bytes read straight out of the extent its dinode records.
    if (stream.CanSeek && stream.Length > MaxBufferedImageBytes)
      return ReadFileEntriesStreaming(stream);

    var image = ReadAllFull(stream);
    return ReadFilesFromImage(image);
  }

  private static List<(string Name, byte[] Data)> ReadFileEntriesStreaming(Stream stream) {
    stream.Position = 0;
    var head = new byte[(int)Math.Min(stream.Length, MaxBufferedImageBytes)];
    stream.ReadExactly(head, 0, head.Length);

    var blockSize = Ocfs2Reader.ReadBlockSize(head);
    if (blockSize <= 0) return [];

    stream.Position = 0;
    using var accessor = new Compression.Core.DiskImage.ImageAccessor(stream, leaveOpen: true);
    var result = new List<(string Name, byte[] Data)>();
    foreach (var p in Ocfs2Reader.ReadFilePlacements(head)) {
      if (p.Size <= 0) { result.Add((p.Name, [])); continue; }
      if (p.Size > Array.MaxLength)
        throw new InvalidOperationException(
          $"OCFS2: '{p.Name}' is {p.Size:N0} bytes, more than a byte[] can hold.");
      using var bytes = new MemoryStream((int)p.Size);
      CopyFile(accessor, p, blockSize, bytes);
      result.Add((p.Name, bytes.ToArray()));
    }
    return result;
  }

  /// <summary>
  /// Streams a file's bytes out of the volume: inline from its dinode, or run by
  /// run at each extent's logical position — a file larger than a cluster group
  /// is several runs with group descriptors between them, and a hole or an
  /// unwritten run reads as zeros.
  /// </summary>
  private static void CopyFile(Compression.Core.DiskImage.ImageAccessor accessor, Ocfs2Reader.FilePlacement p, int blockSize, Stream target) {
    if (p.Size <= 0) return;
    if (p.Inline) {
      accessor.CopyTo(p.DinodeBlkno * blockSize + Ocfs2Reader.Id2Offset + Ocfs2Reader.InlineHeaderLen, target, p.Size);
      return;
    }
    long written = 0;
    void Zeros(long upTo) {
      var zero = new byte[64 * 1024];
      while (written < upTo) {
        var n = (int)Math.Min(zero.Length, upTo - written);
        target.Write(zero, 0, n);
        written += n;
      }
    }
    foreach (var e in p.Extents.OrderBy(e => e.LogicalBlock)) {
      var logical = e.LogicalBlock * blockSize;
      if (logical >= p.Size) break;
      Zeros(logical);
      var length = Math.Min(e.Blocks * blockSize, p.Size - logical);
      if (e.Unwritten) Zeros(logical + length);
      else {
        accessor.CopyTo(e.Blkno * blockSize, target, length);
        written = logical + length;
      }
    }
    Zeros(p.Size);
  }

  /// <summary>Largest volume this descriptor will hold in memory.</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>
  /// Extraction for a volume too large to buffer: only the metadata head is read,
  /// and each file's bytes are streamed straight out of the volume at the extent
  /// its dinode records.
  /// </summary>
  private static void ExtractStreaming(Stream stream, string outputDir, string[]? files) {
    stream.Position = 0;
    var head = new byte[(int)Math.Min(stream.Length, MaxBufferedImageBytes)];
    stream.ReadExactly(head, 0, head.Length);

    var placements = Ocfs2Reader.ReadFilePlacements(head);
    stream.Position = 0;
    using var accessor = new Compression.Core.DiskImage.ImageAccessor(stream, leaveOpen: true);
    var blockSize = Ocfs2Reader.ReadBlockSize(head);

    foreach (var p in placements) {
      if (files != null && files.Length > 0 && !MatchesFilter(p.Name, files)) continue;
      using var target = CreateEntryFile(outputDir, p.Name);
      CopyFile(accessor, p, blockSize, target);
    }
  }

  /// <summary>
  /// Reads file entries from an OCFS2 image. Delegates to <see cref="Ocfs2Reader"/>,
  /// which is spec-correct against <c>fs/ocfs2/ocfs2_fs.h</c> (INODE01 dinode
  /// signature, the real dinode field offsets, the 8-byte inline-data header, and
  /// the 16-byte extent-list header) so it reads images produced by the reference
  /// <c>mkfs.ocfs2</c> as well as the toolkit's own writer. Files are surfaced at
  /// their full nested path.
  /// </summary>
  private static List<(string Name, byte[] Data)> ReadFilesFromImage(byte[] image)
    => Ocfs2Reader.ReadFiles(image);

  // ── Triage path (for non-writer-produced images) ──────────────────────

  private List<ArchiveEntryInfo> ListTriage(Stream stream) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ocfs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Ocfs2Superblock sb;
    try {
      sb = Ocfs2Superblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ocfs2", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.ocfs2", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", sb.HeaderRaw.LongLength, sb.HeaderRaw.LongLength, "stored", false, false, null));
    return entries;
  }

  private void ExtractTriage(Stream stream, string outputDir, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    Ocfs2Superblock sb;
    try {
      sb = Ocfs2Superblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.ocfs2", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.ocfs2", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Ocfs2Superblock sb) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    if (sb.Valid) {
      b.Append(ic, $"superblock_offset={sb.SuperBlockOffset}\n");
      b.Append(ic, $"detected_blocksize={sb.DetectedBlockSize}\n");
      b.Append(ic, $"version_major={sb.MajorRev}\n");
      b.Append(ic, $"version_minor={sb.MinorRev}\n");
      b.Append(ic, $"version={sb.MajorRev}.{sb.MinorRev}\n");
      b.Append(ic, $"blocksize_bits={sb.BlocksizeBits}\n");
      b.Append(ic, $"clustersize_bits={sb.ClustersizeBits}\n");
      b.Append(ic, $"blocksize={(sb.BlocksizeBits is >= 9 and <= 16 ? 1u << (int)sb.BlocksizeBits : 0)}\n");
      b.Append(ic, $"clustersize={(sb.ClustersizeBits is >= 12 and <= 24 ? 1u << (int)sb.ClustersizeBits : 0)}\n");
      b.Append(ic, $"max_slots={sb.MaxSlots}\n");
      b.Append(ic, $"root_blkno={sb.RootBlkno}\n");
      b.Append(ic, $"system_dir_blkno={sb.SystemDirBlkno}\n");
      b.Append(ic, $"first_cluster_group={sb.FirstClusterGroup}\n");
      b.Append(ic, $"label={sb.Label}\n");
      b.Append(ic, $"uuid_hex={sb.UuidHex}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  private static byte[] ReadAllFull(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
