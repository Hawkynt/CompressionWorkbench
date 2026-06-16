#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixFs;

public sealed class MinixFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty {
  public string Id => "MinixFs";
  public string DisplayName => "Minix FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".minix";
  public IReadOnlyList<string> Extensions => [".minix", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5A, 0x4D], Offset: 1048, Confidence: 0.80f),  // v3: magic 0x4D5A at sb+24
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 30-char names
    new([0x68, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 14-char names
    new([0x78, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("minixfs", "Minix FS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix file system image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MinixFsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MinixFsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive the Minix v3
  /// zone allocation in pass 1; pass 2 writes the metadata image with each
  /// file's data zones left zero, then streams each input's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its first allocated zone via 64 KB chunks. The output is
  /// byte-identical to <see cref="Create"/> for the same inputs (Minix has no
  /// data checksums). Falls back to the buffered default when the target stream
  /// is not seekable. Note: Minix v3 caps a file at 7 direct zones (7168 bytes),
  /// so large-file streaming is bounded by that ceiling.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var inputList = inputs.ToList();
    if (!output.CanSeek) {
      ((IArchiveCreatable)this).CreateFromStreams(output, inputList, options);
      return;
    }

    using var w = new MinixFsWriter(output, leaveOpen: true);
    foreach (var input in inputList) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing MinixFs image using
  /// <see cref="MinixFsInPlaceModifier"/> for TRUE in-place
  /// O(touched bytes) random-access I/O across V1, V2 and V3 superblock
  /// variants — only the inode bitmap byte, zone bitmap byte, the new
  /// inode slot, the affected directory zone and the file's data zones
  /// are written; every other byte of the image stays identical.
  /// Falls back to whole-image rebuild only when the image has no free
  /// inode/zone or the file exceeds the direct-pointer ceiling.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        MinixFsInPlaceModifier.RemoveFile(archive, name, wipeData: true);
        MinixFsInPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new MinixFsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: files => {
          using var ms = new MemoryStream();
          using var w = new MinixFsWriter(ms, leaveOpen: true);
          foreach (var (n, d) in files) w.AddFile(n, d);
          w.Finish();
          return ms.ToArray();
        });
    }
  }

  /// <summary>
  /// Removes the named entries from an existing MinixFs image using
  /// <see cref="MinixFsInPlaceModifier"/> for true in-place
  /// O(touched bytes) random-access I/O across V1/V2/V3.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      MinixFsInPlaceModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new MinixFsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new MinixFsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware MinixFs defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    try {
      DefragmentWithPlanner(archive, options);
      return;
    } catch {
      archive.Position = 0;
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = EnumerateExtents(new MemoryStream(imageData)).ToList();
    var mover = new MinixFsBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 1024, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MinixFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using var w = new MinixFsWriter(ms, leaveOpen: true);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }

  /// <summary>
  /// Walks the superblock, inode table and per-inode zone pointers to yield the
  /// real on-disk byte layout — metadata region, directory zones, and each
  /// file's data-zone runs at their true offsets. See
  /// <see cref="MinixFsExtentMap"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => MinixFsExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in a Minix image: free zones and the cluster-tip
  /// slack between a file's logical size (i_size) and the end of its last
  /// 1024-byte zone. Data zones are reached through the inode's zone pointers
  /// and the writer allocates them contiguously per file, so a size lookup keyed
  /// by file name lets the generic <see cref="UnusedSpaceWiper"/> trim each tip
  /// precisely without touching the inode table, bitmaps or directory zones.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new MinixFsReader(image);
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
    var extents = MinixFsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }
}
