#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ufs;

/// <summary>
/// R/W descriptor for UFS1 (Berkeley Fast File System) images at the
/// byte-exact <c>newfs -O1</c> layout.
///
/// References:
/// <list type="bullet">
///   <item><description>McKusick, Joy, Leffler, Fabry — "A Fast File System for UNIX" (ACM TOCS, 1984), the defining FFS paper</description></item>
///   <item><description><c>https://github.com/freebsd/freebsd-src/tree/main/sys/ufs</c> — canonical implementation (<c>ffs/fs.h</c> on-disk superblock)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Unix_File_System</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class UfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
                                          IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints,
                                          IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty,
                                          IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>UFS creation knob: the volume label written to the superblock
  /// <c>fs_volname</c> field (the <c>tunefs -L</c> / <c>dumpfs</c> "volume name").
  /// Block/fragment geometry stays at the byte-exact <c>newfs -O1</c> layout.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 31),
  ];

  /// <summary>
  /// Walks the UFS1 superblock, CG 0 inode table, and root directory tree;
  /// yields the actual on-disk byte layout — superblock + inode table as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file direct-
  /// block run (coalesced into contiguous extents) as
  /// <see cref="DefragBlockKind.Used"/>. Indirect blocks are not followed
  /// (single-CG profile our writer emits doesn't use them).
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => UfsExtentMap.Enumerate(image);

  /// <summary>
  /// Zeros all unused space in a UFS1 image: every block not claimed by the
  /// superblock, the CG inode table, a directory's data blocks, or a file's
  /// direct-block run. Driven by the generic <see cref="UnusedSpaceWiper"/>
  /// over the UFS extent map.
  ///
  /// <para>Cluster tips are wiped: each file's Used extent ends at its logical
  /// inode size (<c>di_size</c>), so the block/fragment padding between the
  /// file's last byte and the block boundary is left uncovered and zeroed as
  /// free space. A file-size lookup keyed on the entry name is also supplied so
  /// the wiper can trim the tail explicitly. Directory data blocks surface in
  /// the extent map with a trailing <c>"/"</c> in their <c>FileName</c> and so
  /// never match a file-size key — they (and all metadata) are preserved.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new UfsReader(image);
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
    var extents = UfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "UFS1 filesystem image; multiple cylinder groups, nested directories, " +
    "direct- plus single-indirect-block files (up to ~16 MB each).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }

  public string Id => "Ufs";
  public string DisplayName => "UFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ufs";
  public IReadOnlyList<string> Extensions => [".ufs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x54, 0x19, 0x01, 0x00], Offset: 8192 + 1372, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix File System (UFS1) image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new UfsReader(stream);
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget
    )).ToList();
    return SymlinkResolver.Resolve(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new UfsReader(stream);
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
    var r = new UfsReader(archive);
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
    var w = new UfsWriter { VolumeLabel = options?.GetOption("VolumeLabel", "") ?? "" };
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                           () => File.OpenRead(info.FullPath));
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive the UFS1
  /// cylinder-group geometry in pass 1; pass 2 emits the disk image with each
  /// file's data fragments left zero, then streams each input's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its first allocated fragment via 64 KB chunks. The output is
  /// byte-identical to <see cref="Create"/> for the same inputs (the UFS cs
  /// records are free-space summaries, not content checksums). Falls back to the
  /// buffered default when the target stream is not seekable.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var inputList = inputs.ToList();
    if (!output.CanSeek) {
      ((IArchiveCreatable)this).CreateFromStreams(output, inputList, options);
      return;
    }

    var w = new UfsWriter { VolumeLabel = options?.GetOption("VolumeLabel", "") ?? "" };
    foreach (var input in inputList) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing UFS1 image. Uses
  /// <see cref="UfsModifier"/> for true O(touched bytes) random-access I/O —
  /// only the superblock, CG header (with bitmaps), the affected inode slot,
  /// the root dir block, and the file's data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      // Replace-by-name semantics — drop any prior entry with the same name first.
      UfsModifier.RemoveFile(archive, name, wipeData: true);
      UfsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing UFS1 image. Data blocks are
  /// wiped during removal so no forensic trace remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      UfsModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new UfsBlockMover();
    mover.Init(image);
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new UfsBlockMover();
    mover.Init(image);
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware UFS1 defragmentor. Tries planner-driven in-place path first,
  /// falls back to rebuild path on error. The planner path is streaming
  /// throughout — no whole-image snapshot is taken.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }

    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;

    var mover = new UfsBlockMover();
    mover.Init(archive);

    var extents = UfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: extents, Status: "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.DataOrigin, imageSize, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
        ImageSize: imageSize, BlockMap: extents, Status: "Already defragmented"));
      return;
    }

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize, () => mover.Init(archive));

    var postExtents = UfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new UfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new UfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }
}
