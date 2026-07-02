#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D71;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/d71.html</c> — Peter Schepers' D71 format specification (double-sided BAM, directory layout)</description></item>
///   <item><description>Commodore 1571 Disk Drive User's Guide (Commodore, 1985) — original vendor documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Commodore_1571</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class D71FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for D71 creation. The Commodore 1571 stores a 16-char
  /// PETSCII disk name plus a 2-char disk ID in the BAM (track 18 sector 0);
  /// both appear in the C128 directory header. Geometry is fixed at the
  /// double-sided 1571 size (349 696 bytes).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    new FormatOptionDescriptor(
      Key: "DiskId",
      DisplayName: "Disk ID",
      Kind: FormatOptionKind.String,
      Default: "00",
      Description: "Two-character disk ID at BAM offset 0xA2 (1571 side 1)."),
  ];

  /// <summary>
  /// Zeros all unused space in the D71 image: every sector not claimed by a
  /// live file chain or by the directory/BAM metadata is overwritten with zeros.
  /// <para>
  /// Cluster-tip wiping is not applicable to the 1571 layout: files are stored
  /// as a chain of 256-byte sectors carrying a 2-byte track/sector link header
  /// plus 254 payload bytes, so the directory-entry size is expressed in
  /// 254-byte units that do not map onto a contiguous, cluster-aligned tail.
  /// The trailing slack inside a file's final sector is therefore left to the
  /// reader/writer; this method clears only whole free sectors.
  /// </para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = D71ExtentMap.Enumerate(image);
    // Linked-sector layout — no cluster-aligned tail to wipe per file.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Walks the directory chain on track 18 (and BAM mirror on track 53)
  /// and yields the actual on-disk byte layout — track 18 BAM+directory
  /// and the BAM mirror as <see cref="DefragBlockKind.MetadataReserved"/>,
  /// every per-file sector chain as one or more contiguous-run extents,
  /// and the un-attributed sectors as <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => D71ExtentMap.Enumerate(image);

  public long? MaxTotalArchiveSize => 349696;
  public string AcceptedInputsDescription =>
    "Commodore 1571 D71 disk; any file up to 349 696 bytes total.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  // D71 is double-sided; a payload that fits 174 848 bytes could step down to D64. Users
  // who want that flow should invoke the D64 descriptor directly; this format keeps its own
  // fixed size on shrink.
  public IReadOnlyList<long> CanonicalSizes => [349696];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "D71";
  public string DisplayName => "D71";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D71 image.
  /// Uses <see cref="D71Modifier"/> for true O(touched bytes) random-access
  /// I/O — only the BAM (2 sectors), the directory chain, and the file's
  /// data sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D71Modifier.RemoveFile(archive, truncated, wipeData: true);
      D71Modifier.AddFile(archive, truncated, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D71 image. Uses
  /// <see cref="D71Modifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D71Modifier.RemoveFile(archive, truncated, wipeData: true);
    }
  }

  public string DefaultExtension => ".d71";
  public IReadOnlyList<string> Extensions => [".d71"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 1571 double-sided disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D71Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D71Reader(stream);
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
    var r = new D71Reader(archive);
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
    var w = new D71Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var diskId = options?.GetOption("DiskId", "00") ?? "00";
    output.Write(w.Build(label, diskId));
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new D71BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new D71BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware D71 defragmentor. Tries the planner-driven in-place path first,
  /// falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
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
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new D71Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new D71Writer();
        foreach (var (n, d) in files)
          w.AddFile(n.Length > 16 ? n[..16] : n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = D71ExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new D71BlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }
}
