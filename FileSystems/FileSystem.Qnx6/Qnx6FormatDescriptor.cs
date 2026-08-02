#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Qnx6;

/// <summary>
/// Descriptor for QNX6 (Neutrino) filesystem images. Magic 0x68191122 (LE) at
/// file offset 0x2000. Read + R/W (Add/Remove): the writer (<see cref="Qnx6Writer"/>)
/// emits paired superblocks (primary at 0x2000 + identical secondary mirror at
/// the tail of the volume) — the power-safe contract — alongside a flat 128-byte
/// inode array and 32-byte directory entries. The modifier (<see cref="Qnx6Modifier"/>)
/// mutates that layout in place and re-mirrors the superblock to the new tail
/// after each Add/Remove so the dual-superblock pairing remains byte-identical.
/// Self-round-trips through <see cref="Qnx6Reader"/>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/qnx6.html</c> — kernel documentation of the on-disk layout (dual superblocks)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/qnx6</c> — Linux reference implementation</description></item>
///   <item><description>QNX Neutrino <c>fs-qnx6.so</c> documentation (QNX Software Systems)</description></item>
/// </list>
/// </summary>
public sealed class Qnx6FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Qnx6";
  public string DisplayName => "QNX6 Neutrino FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".qnx6";
  public IReadOnlyList<string> Extensions => [".qnx6"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x22, 0x11, 0x19, 0x68], Offset: 0x2000, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QNX6 Neutrino filesystem — R/W (paired superblocks; reader walks a single-block directory and direct-extent files; Add/Remove mutate in place with synchronous dual-superblock mirror).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qnx6Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qnx6Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Qnx6Reader(archive);
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

  /// <summary>
  /// Emits a fresh QNX6 image containing <paramref name="inputs"/>. Files are
  /// flattened to leaf names (directory components dropped) — the Stage-1
  /// reader walks a single-block root directory, so a flat layout matches what
  /// it can read back. The output is a complete image: boot region, primary
  /// superblock, inode table, root dir block, file data extents, and a mirror
  /// secondary superblock at the tail.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = new List<(string Name, Compression.Core.DiskImage.FilePayload Payload)>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      files.Add((name, info.InMemoryContent is { } bytes
        ? Compression.Core.DiskImage.FilePayload.FromBytes(bytes)
        : Compression.Core.DiskImage.FilePayload.FromStream(
            new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath))));
    }
    Qnx6Writer.WriteTo(output, files);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing QNX6 image. The
  /// modifier locates a free inode slot, lays down a contiguous data extent
  /// past the current high-water mark, writes the dirent into the single-block
  /// root directory, and re-mirrors the primary superblock to the new tail —
  /// the dual-superblock pairing is updated synchronously so the power-safe
  /// contract holds across the whole sequence.
  /// </summary>
  /// <exception cref="NotSupportedException">When the root directory is full
  /// (the Stage-2 modifier preserves the single-block root limit of 32 dirents).</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier reads the volume into an array to walk its
    // structures, which a volume past two gigabytes does not fit in. Above that
    // the edit is applied by unpacking and relaying the volume out instead.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      Qnx6Modifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing QNX6 image. Data blocks are
  /// zeroed (wipe contract), the inode slot is cleared, and trailing dirents
  /// are compacted into the freed slot so reads see no gap. The secondary
  /// superblock mirror is refreshed afterwards.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Qnx6Modifier.RemoveFiles(archive, entryNames);
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a rebuild
    // reads and rewrites every file to fix a handful of runs. A file here is one
    // contiguous run of blocks and its inode's first direct pointer says where
    // that run starts, so a move is the copy plus four bytes. The in-place pass
    // is kept only if every payload still reads back afterwards — it can refuse
    // partway, and a rebuild is the honest answer when it does.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => {
          stream.Position = 0;
          using var reader = new Qnx6Reader(stream);
          return reader.Entries.Where(e => !e.IsDirectory)
                               .Select(e => reader.Extract(e)).ToList();
        },
        inPlace: () => this.DefragmentWithPlanner(archive, options),
        rebuild: () => DefragmentByRebuild(archive, options));
      return;
    }

    DefragmentByRebuild(archive, options);
  }

  /// <summary>Reads every file out and writes a fresh volume in the asked-for order.</summary>
  private static void DefragmentByRebuild(Stream archive, DefragOptions options) {
    var sourceLength = archive.Length;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        stream.Position = 0;
        using var reader = new Qnx6Reader(stream);
        return reader.Entries.Where(e => !e.IsDirectory)
                             .Select(e => (e.Name, reader.Extract(e))).ToList();
      },
      buildImage: files => {
        var built = Qnx6Writer.Build(files.ToList());
        // The defrag contract keeps the volume the size it was; the writer sizes
        // an image to what the files need, and the mirror superblock has to end
        // up at the tail the volume actually has.
        if (built.Length >= sourceLength) return built;
        const int MirrorSize = 512;
        var padded = new byte[sourceLength];
        Array.Copy(built, padded, built.Length);
        if (sourceLength - MirrorSize >= built.Length)
          Array.Clear(padded, built.Length - MirrorSize, MirrorSize);
        Array.Copy(built, built.Length - MirrorSize, padded, sourceLength - MirrorSize, MirrorSize);
        return padded;
      });
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Qnx6BlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
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

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// The superblocks, inode table and directory blocks are structure; each file
  /// is the contiguous run its inode points at. Blocks no live inode points at
  /// are what a removal left behind.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Qnx6Reader(image);
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (!reader.TryGetDataExtent(e, out var offset, out var length)) continue;
        result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
        first = Math.Min(first, offset);
      }
      if (first == long.MaxValue) first = Math.Min(image.Length, 64L * reader.BlockSize);
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));

      // The secondary superblock mirrors the primary in the volume's last 512
      // bytes. Leaving it unclaimed invited anything laying files out against
      // the tail to write straight over it, which costs the volume the copy
      // that a power-safe mount falls back on.
      const int MirrorSize = 512;
      if (image.Length > first + MirrorSize)
        result.Add(new DefragBlockInfo(image.Length - MirrorSize, MirrorSize,
          DefragBlockKind.MetadataReserved, "<superblock mirror>"));
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

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new Qnx6Reader(image);
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

}
