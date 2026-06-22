#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AppleDos;

public sealed class AppleDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for Apple DOS 3.3 creation. The format has exactly one
  /// canonical geometry (35 tracks × 16 sectors × 256 bytes) and no concept
  /// of a volume name, so the only meaningful knob is the VTOC's disk
  /// volume number — used by DOS to disambiguate disks in a multi-volume
  /// session. Valid range 1..254 (0 = unset; 255 reserved).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeNumber",
      DisplayName: "Volume number",
      Kind: FormatOptionKind.Integer,
      Default: "254",
      Description: "Disk volume number stored at VTOC offset 0x06. Apple DOS uses this " +
        "to identify which physical floppy is in the drive. Range 1..254 (default 254)."),
  ];

  /// <summary>
  /// Walks the VTOC + catalog (track 17) and per-file T/S list chains,
  /// yielding the actual on-disk byte layout. Track 17 becomes metadata;
  /// every file's T/S list + data sectors collapse into contiguous-run
  /// extents; un-attributed sectors are emitted as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => AppleDosExtentMap.Enumerate(image);

  public long? MaxTotalArchiveSize => AppleDosReader.StandardSize;
  public string AcceptedInputsDescription =>
    "Apple DOS 3.3 disk (35 tracks x 16 sectors x 256 bytes = 143 360 bytes).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>The Apple DOS 3.3 format has exactly one canonical image size.</summary>
  public IReadOnlyList<long> CanonicalSizes => [AppleDosReader.StandardSize];

  public string Id => "AppleDos";
  public string DisplayName => "Apple DOS 3.3";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing AppleDos image.
  /// Uses <c>AppleDosModifier</c> for true O(touched bytes) random-access
  /// I/O — only the VTOC, the catalog chain, and the file's data + T/S
  /// list sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      AppleDosModifier.RemoveFile(archive, name, wipeData: true);
      AppleDosModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing AppleDos image. Uses
  /// <c>AppleDosModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      AppleDosModifier.RemoveFile(archive, name, wipeData: true);
  }


  public string DefaultExtension => ".dsk";
  public IReadOnlyList<string> Extensions => [".dsk", ".do"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // DOS 3.3 has no magic bytes — detection is extension + VTOC sanity (handled
  // by attempting a parse). We keep the magic list empty and let FormatDetector
  // fall back to extension matching.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple II DOS 3.3 floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AppleDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AppleDosReader(stream);
    foreach (var e in r.Entries) {
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
    var r = new AppleDosReader(archive);
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
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"AppleDOS: combined input size {total} bytes exceeds disk capacity ({cap} bytes).");

    var w = new AppleDosWriter();
    var volNum = options?.GetOptionInt("VolumeNumber", 254) ?? 254;
    if (volNum is >= 1 and <= 254) w.VolumeNumber = (byte)volNum;
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new AppleDosBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new AppleDosBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware Apple DOS 3.3 defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      // Snapshot first so a planner pass that mutates the image but leaves it
      // structurally invalid (some payloads make the catalog repatch write an
      // out-of-range track/sector) can be rolled back. We verify the result is
      // still readable with the same live-file count; only then keep it.
      archive.Position = 0;
      using var snapshot = new MemoryStream();
      archive.CopyTo(snapshot);
      var expected = CountLiveFiles(snapshot);
      try {
        DefragmentWithPlanner(archive, options);
        if (CountLiveFilesFromStream(archive) >= expected && expected > 0)
          return;
      } catch {
        // fall through to restore + rebuild
      }
      // Planner failed or corrupted the image — restore the original bytes and
      // use the always-safe rebuild path below.
      archive.Position = 0;
      archive.SetLength(0);
      snapshot.Position = 0;
      snapshot.CopyTo(archive);
      archive.Position = 0;
    }
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new AppleDosReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new AppleDosWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>Counts live (non-directory) files in a snapshot stream; 0 on any read error.</summary>
  private static int CountLiveFiles(Stream snapshot) {
    try {
      snapshot.Position = 0;
      using var copy = new MemoryStream();
      snapshot.CopyTo(copy);
      copy.Position = 0;
      return new AppleDosReader(copy).Entries.Count(e => !e.IsDirectory);
    } catch {
      return 0;
    }
  }

  /// <summary>Counts live files by reading the image stream in place; -1 if it can't be parsed.</summary>
  private static int CountLiveFilesFromStream(Stream image) {
    try {
      image.Position = 0;
      using var copy = new MemoryStream();
      image.CopyTo(copy);
      copy.Position = 0;
      return new AppleDosReader(copy).Entries.Count(e => !e.IsDirectory);
    } catch {
      return -1;
    }
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = AppleDosExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new AppleDosBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an Apple DOS 3.3 image: every 256-byte sector not
  /// claimed by the VTOC/catalog (track 17) or by a live file's track/sector
  /// list and data sectors. Driven by the generic <see cref="UnusedSpaceWiper"/>
  /// over the AppleDOS extent map.
  ///
  /// <para>Per-file cluster-tip wiping is <em>not</em> applied: an AppleDOS
  /// file's extent is a coalesced run that interleaves its track/sector-list
  /// sectors with the data sectors, so the file's logical bytes are not a flat
  /// <c>offset..offset+size</c> region. Treating the run's tail as slack would
  /// clobber a T/S-list sector or a neighbouring file, so tip wiping is N/A
  /// here; only genuinely free sectors are zeroed.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = AppleDosExtentMap.Enumerate(image);
    // Tips are N/A for AppleDOS (track/sector-list sectors interleave with data
    // in a file's run); wipe free sectors only, never per-extent tails.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
