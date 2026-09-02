#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D81;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/d81.html</c> — Peter Schepers' D81 format specification (header block, BAM, directory)</description></item>
///   <item><description>Commodore 1581 Disk Drive User's Guide (Commodore, 1987) — original vendor documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Commodore_1581</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class D81FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for D81 creation. The Commodore 1581 stores a 16-char
  /// PETSCII disk name plus a 2-char disk ID in the header block (track 40
  /// sector 0); both appear in the C128 directory header. Geometry is fixed
  /// at the 1581 size (819 200 bytes).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    new FormatOptionDescriptor(
      Key: "DiskId",
      DisplayName: "Disk ID",
      Kind: FormatOptionKind.String,
      Default: "00",
      Description: "Two-character disk ID at 1581 header offset 0x16."),
  ];

  /// <summary>
  /// Zeros all unused space in the D81 image: every sector not claimed by a
  /// live file chain or by the header/BAM/directory metadata is overwritten
  /// with zeros.
  /// <para>
  /// Cluster-tip wiping is not applicable to the 1581 layout: files are stored
  /// as a chain of 256-byte sectors carrying a 2-byte track/sector link header
  /// plus 254 payload bytes, so the directory-entry size is expressed in
  /// 254-byte units that do not map onto a contiguous, cluster-aligned tail.
  /// This method clears only whole free sectors.
  /// </para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = D81ExtentMap.Enumerate(image);
    // Linked-sector layout — no cluster-aligned tail to wipe per file.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Walks track 40 (header + BAM1 + BAM2 + directory chain) and yields
  /// the actual on-disk byte layout — header/BAM/directory sectors as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file sector
  /// chain as one or more contiguous-run extents, and the un-attributed
  /// sectors as <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => D81ExtentMap.Enumerate(image);

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => 819200;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Commodore 1581 D81 disk; any file up to 819 200 bytes total.";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// Gets the canonical sizes.
  /// </summary>
public IReadOnlyList<long> CanonicalSizes => [819200];
  /// <summary>
  /// Performs the shrink operation.
  /// </summary>
public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "D81";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "D81";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D81 image.
  /// Uses <c>D81Modifier</c> for true O(touched bytes) random-access
  /// I/O — only the BAM (2 sectors), the directory chain, and the file's
  /// data sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D81Modifier.RemoveFile(archive, truncated, wipeData: true);
      D81Modifier.AddFile(archive, truncated, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D81 image. Uses
  /// <c>D81Modifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D81Modifier.RemoveFile(archive, truncated, wipeData: true);
    }
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".d81";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".d81"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
public string Description => "Commodore 1581 3.5\" disk image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D81Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D81Reader(stream);
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
    var r = new D81Reader(archive);
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

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D81Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var diskId = options?.GetOption("DiskId", "00") ?? "00";
    output.Write(w.Build(label, diskId));
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new D81BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new D81BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware D81 defragmentor. Tries the planner-driven in-place path first,
  /// falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
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
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new D81Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new D81Writer();
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
    var extents = D81ExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new D81BlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
