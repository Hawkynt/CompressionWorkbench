#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Adf;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>http://lclevy.free.fr/adflib/adf_info.html</c> — Laurent Clévy's ADF / AmigaDOS (OFS/FFS) on-disk format reference, the de-facto ADF spec</description></item>
///   <item><description>ADFlib — the reference open-source ADF implementation built on that document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Amiga_Disk_File</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class AdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for ADF creation: AmigaDOS file-system flavour (OFS vs FFS)
  /// in the boot block and the AmigaDOS volume label written into the root
  /// block. The image geometry is fixed at the standard DD floppy size
  /// (880 KB, 1760 × 512-byte sectors) — Amiga DD ADF is the canonical
  /// emulator/preservation image and is the only size this writer emits.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "FileSystemType",
      DisplayName: "File system",
      Kind: FormatOptionKind.Enum,
      Default: "FFS",
      AllowedValues: ["FFS", "OFS"],
      Description: "AmigaDOS boot-block file-system tag: FFS (Fast File System, AmigaOS 2.0+) " +
        "or OFS (Original File System, Kickstart 1.x). Stored at boot-block offset 3."),
    FilesystemSchemaPresets.VolumeLabel(maxChars: 30),
  ];

  /// <summary>
  /// Walks the boot blocks + root block + bitmap blocks + per-file
  /// header/extension/data block chains, yielding the actual on-disk
  /// layout. Boot/root/bitmap and directory headers become
  /// <see cref="DefragBlockKind.MetadataReserved"/>; file header
  /// + extension blocks + data blocks attribute to their owning file
  /// (coalesced into contiguous runs).
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => AdfExtentMap.Enumerate(image);

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => 901120;  // standard DD (880 KB) — 11 sectors × 2 sides × 80 tracks × 512
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "Amiga DD ADF disk; any file up to 901 120 bytes total.";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// Gets the canonical sizes.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [901120];
  /// <summary>
  /// Performs the shrink operation.
  /// </summary>
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Adf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "ADF";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Adf image (FFS).
  /// Uses <c>AdfModifier</c> for true O(touched bytes) random-access I/O —
  /// only the root block, the bitmap, the optional hash-chain neighbour,
  /// and the new file's header + data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      AdfModifier.RemoveFile(archive, name, wipeData: true);
      AdfModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Adf image (FFS). Uses
  /// <c>AdfModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      AdfModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".adf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".adf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("DOS\0"u8.ToArray(), Confidence: 0.60)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("adf", "ADF")];
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
  public string Description => "Amiga Disk File";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AdfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AdfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
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
    var r = new AdfReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
    var w = new AdfWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var fsType = (options?.GetOption("FileSystemType", "FFS") ?? "FFS")
                   .Equals("OFS", StringComparison.OrdinalIgnoreCase) ? (byte)0 : (byte)1;
    output.Write(w.Build(label, fsType));
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new AdfBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new AdfBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ADF defragmentor. Tries the planner-driven in-place path first,
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
        var r = new AdfReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory)
          .Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        var w = new AdfWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = AdfExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new AdfBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 512, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an Amiga ADF image: every 512-byte sector not
  /// claimed by a boot/root/bitmap block, a directory or file header, a file
  /// extension block, or a file data block. Driven by the generic
  /// <see cref="UnusedSpaceWiper"/> over the ADF extent map.
  ///
  /// <para>Per-file cluster-tip wiping is <em>not</em> applied: an ADF file's
  /// extent is a coalesced run that interleaves the file header block, optional
  /// extension blocks and the data blocks (and, under OFS, every data block
  /// carries a 24-byte block header), so the file's logical bytes are not laid
  /// out as a flat <c>offset..offset+size</c> region. Treating the trailing
  /// bytes of that run as slack would clobber live metadata, so tip wiping is
  /// N/A here; only genuinely free sectors are zeroed.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = AdfExtentMap.Enumerate(image);
    // Tips are N/A for ADF (header/extension/data blocks interleave within a
    // file's run); wipe free sectors only, never per-extent tails.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
