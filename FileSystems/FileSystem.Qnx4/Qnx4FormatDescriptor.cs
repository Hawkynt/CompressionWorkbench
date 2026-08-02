#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Qnx4;

/// <summary>
/// R/W descriptor for QNX4 filesystem images. QNX4 has no fixed magic
/// at the start of the image — detection relies on the inode status byte
/// pattern in the root directory cluster (block 1).
///
/// <para>Add / Remove are routed through <see cref="Qnx4Modifier"/>, which
/// mutates the root cluster (LBA 1-4) and the <c>.bitmap</c> (LBA 5) in
/// place. Scope stays flat-root (29 user files) — past that Add throws
/// <see cref="NotSupportedException"/>, matching the WORM writer's capacity
/// guard. Subdirectory emission is still out of scope.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/qnx4_fs.h</c> — canonical on-disk structures</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/qnx4</c> — Linux reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/QNX</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Qnx4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Qnx4";
  public string DisplayName => "QNX4 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".qnx4";
  public IReadOnlyList<string> Extensions => [".qnx4", ".qnx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // QNX4 has no fixed superblock magic. Detection looks for any of the
    // recognised "live inode" status bytes at offset 0x23D (= block 1, first
    // inode entry's di_status field). Status bytes accepted:
    //   0x01 = QNX4_FILE_USED  (Linux-friendly short-name file)
    //   0x08 = QNX4_FILE_LINK  (long-name continuation marker — historical QNX4-utils)
    //   0x09 = QNX4_FILE_USED|LINK (root self-reference emitted by our writer)
    new([0x01], Offset: 0x23D, Confidence: 0.35),
    new([0x08], Offset: 0x23D, Confidence: 0.35),
    new([0x09], Offset: 0x23D, Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QNX4 filesystem image (1991-2001, QNX Software Systems) — R/W (flat root, max 29 user files).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qnx4Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qnx4Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an entry may be larger than a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Qnx4Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Qnx4Writer();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing QNX4 image. Routed
  /// through <see cref="Qnx4Modifier.AddFile"/>: the root cluster + bitmap
  /// are mutated in place, the new file's data extent is allocated from the
  /// bitmap, and the inode lands in the first free slot (entries 3..31).
  /// </summary>
  /// <exception cref="NotSupportedException">Root cluster full (29 user
  /// files). The flat-root scope matches the WORM writer's capacity guard.</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in — and where it can still edit, it has no room
    // to grow a full volume. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      Qnx4Modifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing QNX4 image. Routed through
  /// <see cref="Qnx4Modifier.RemoveFile"/>: the dirent is located in the
  /// root cluster, the extent is freed in the bitmap, data blocks are
  /// zero-wiped, and the inode slot is cleared.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Qnx4Modifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Every QNX4 file is one contiguous extent recorded in its inode, so the map
  /// is the boot/root/bitmap region as structure plus one run per live entry.
  /// Blocks no live inode names are what a removal left behind.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new Qnx4Reader(image);
      // Boot block, root directory cluster and the system inodes that follow it.
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (e.FirstExtentBlock == 0 || e.ExtentBlockCount == 0) continue;
        var offset = (long)e.FirstExtentBlock * Qnx4Reader.BlockSize;
        if (offset < 0 || offset >= image.Length) continue;
        var length = Math.Min((long)e.ExtentBlockCount * Qnx4Reader.BlockSize, image.Length - offset);
        if (length <= 0) continue;
        result.Add(new DefragBlockInfo(offset, length,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
        first = Math.Min(first, offset);
      }
      if (first == long.MaxValue) first = Math.Min(image.Length, 64L * Qnx4Reader.BlockSize);
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;

    // A file's extent is whole blocks; the tail past its recorded size is slack.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new Qnx4Reader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizes[e.Name] = e.Size;
        fileSizeLookup = n => sizes.TryGetValue(n, out var v) ? v : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, fileSizeLookup);
  }


  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Moves only the files that are out of place, repointing each one's inode
  /// extent as its blocks arrive. A file here is one contiguous extent named by
  /// its inode, so a move is the copy and one four-byte write — where a rebuild
  /// would read and rewrite every file to fix a handful of runs.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back. It can
    // refuse partway — an inode it cannot find leaves the volume with bytes
    // moved and nothing pointing at them — and a rebuild is the honest answer
    // when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => {
        using var reader = new Qnx4Reader(stream);
        return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
      },
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => {
          using var reader = new Qnx4Reader(stream);
          return reader.Entries.Where(e => !e.IsDirectory)
                               .Select(e => (e.Name, reader.Extract(e))).ToList();
        },
        buildImage: files => {
          var writer = new Qnx4Writer();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          using var built = new MemoryStream();
          writer.WriteTo(built);
          return built.ToArray();
        }));
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Qnx4BlockMover();

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // A file across several extents keeps the later ones in an extent block
    // this pass does not rewrite, so repointing the inode alone would leave the
    // rest of the file behind. Those volumes are refused.
    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used || extent.FileName is not { } owner) continue;
      runsPerOwner.TryGetValue(owner, out var count);
      runsPerOwner[owner] = count + 1;
    }
    var fragmented = runsPerOwner.Count(kv => kv.Value > 1);
    if (fragmented > 0)
      throw new NotSupportedException(
        $"QNX4: {fragmented} file(s) span more than one extent, which this pass cannot restate.");

    // The mover reads inodes from the root directory cluster, so a file living
    // in a subdirectory has no inode it can find. Refusing before the first
    // byte moves keeps a half-moved volume from being the answer — the check
    // used to happen inside the mover, which is after the moving has begun.
    var nested = runsPerOwner.Keys.Count(name => name.Contains('/'));
    if (nested > 0)
      throw new NotSupportedException(
        $"QNX4: {nested} file(s) live in subdirectories, whose inodes this pass does not reach.");

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

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

}
