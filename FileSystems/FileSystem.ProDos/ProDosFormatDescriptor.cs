#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ProDos;

/// <summary>
/// Descriptor for Apple II ProDOS volume images (140 KB / 800 KB) — volume
/// directory + bitmap layout with seedling/sapling/tree file storage tiers.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://prodos8.com/docs/techref/</c> — ProDOS 8 Technical Reference Manual — volume/directory/storage-tier spec</description></item>
///   <item><description><c>https://github.com/fadden/CiderPress2</c> — CiderPress II — maintained tooling for ProDOS volumes</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apple_ProDOS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class ProDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────
  // ProDOS uses fixed 512-byte blocks (no cluster-size knob), so the tunable
  // parameters are the volume size (only 140 KB or 800 KB are supported by the
  // writer) and the volume name.
  /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "ImageSize",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "Auto (fit to files)",
      AllowedValues: ["Auto (fit to files)", "140 KB (5.25\")", "800 KB (3.5\")"],
      Description: "ProDOS volume size. Auto uses 140 KB and promotes to 800 KB when the files don't fit."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume name",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "ProDOS volume name (max 15 chars; letters, digits and periods; must start with a letter)."),
  ];

  /// <summary>
  /// Walks the volume directory + bitmap + per-file storage tiers
  /// (seedling/sapling/tree) and yields the actual on-disk block layout.
  /// Boot, volume directory chain, bitmap, and subdir blocks are emitted
  /// as <see cref="DefragBlockKind.MetadataReserved"/>; data + index +
  /// master-index blocks are attributed to their owning file.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => ProDosExtentMap.Enumerate(image);

  // We cap at the 800 KB Mac-format floppy — the largest canonical size we emit.
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => ProDosWriter.Disk800KTotalBlocks * 512L;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Apple ProDOS block-ordered disk image (.po) or 2mg-wrapped ProDOS image.";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical ProDOS image sizes (5.25" floppy = 143 360, 800 KB floppy = 819 200).</summary>
  public IReadOnlyList<long> CanonicalSizes => [
    ProDosWriter.FloppyTotalBlocks * 512L,
    ProDosWriter.Disk800KTotalBlocks * 512L,
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "ProDos";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ProDOS";
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
  /// Adds (or replaces by name) files inside an existing ProDos image.
  /// Uses <c>ProDosModifier</c> for true O(touched bytes) random-access
  /// I/O — only the volume header, the directory chain, the bitmap, and
  /// the file's index + data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ProDosModifier.RemoveFile(archive, name, wipeData: true);
      ProDosModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ProDos image. Uses
  /// <c>ProDosModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ProDosModifier.RemoveFile(archive, name, wipeData: true);
  }


  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".po";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".po", ".2mg"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // .2mg files begin with "2IMG" at offset 0. A raw .po image has no header of
  // its own, but its volume directory does: block 2 opens with a zero
  // previous-block pointer, a next-block pointer of 3, and a storage type of
  // 0xF — the volume directory header. Without that, a ProDOS image shared the
  // .po extension with gettext catalogues and whichever descriptor came first
  // in the registry got the file.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("2IMG"u8.ToArray(), Offset: 0, Confidence: 0.95),
    new([0x00, 0x00, 0x03, 0x00, 0xF0], Offset: 0x400, Confidence: 0.85,
      Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0xF0]),
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
public string Description => "Apple II / Apple IIgs ProDOS filesystem image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ProDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ProDosReader(stream);
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
    var r = new ProDosReader(archive);
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
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"ProDOS: combined input size {total} bytes exceeds 800 KB disk capacity ({cap} bytes).");

    var w = new ProDosWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    // Honour an explicit volume size if the user picked one; otherwise use the
    // smaller floppy and auto-promote to 800 KB when the 140 KB floppy won't fit.
    var floppyCap = (ProDosWriter.FloppyTotalBlocks - 10) * 512L;  // rough free-space cap
    var totalBlocks = (options.FormatSpecific?.GetValueOrDefault("ImageSize")?.Trim()) switch {
      "140 KB (5.25\")" => ProDosWriter.FloppyTotalBlocks,
      "800 KB (3.5\")"  => ProDosWriter.Disk800KTotalBlocks,
      _ => total > floppyCap ? ProDosWriter.Disk800KTotalBlocks : ProDosWriter.FloppyTotalBlocks,
    };

    var label = options.FormatSpecific?.GetValueOrDefault("VolumeLabel");
    var volumeName = string.IsNullOrWhiteSpace(label) ? "WORM" : label!;
    output.Write(w.Build(volumeName, totalBlocks));
  }

  /// <summary>
  /// Zeros all unused space in the ProDOS image: unallocated blocks and the
  /// block-tip slack between a file's logical EOF and the end of its last
  /// 512-byte block. Cluster-tip wiping is applied only to seedling files
  /// (storage type 1, a single data block). Sapling and tree files interleave
  /// index and master-index blocks with their data inside one coalesced Used
  /// extent, so a logical-size lookup cannot tell data slack from a live index
  /// block — those files are omitted from the tip pass to avoid corrupting the
  /// block pointers; their free blocks are still zeroed.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = ProDosExtentMap.Enumerate(image).ToList();

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new ProDosReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries) {
          if (entry.IsDirectory) continue;
          // Only seedlings have a single data block with no index block sharing
          // its extent, so only their block tip can be wiped safely.
          if (entry.StorageType == 1)
            sizeMap[entry.FullPath] = entry.Size;
        }
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new ProDosBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new ProDosBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ProDOS defragmentor. Tries the planner-driven in-place path first,
  /// falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// Image total-block size is preserved by inferring it from the input stream length.
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
    var originalLength = archive.Length;
    var totalBlocks = (int)(originalLength / 512L);
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new ProDosReader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
          .Select(e => (e.FullPath, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new ProDosWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(totalBlocks: totalBlocks);
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = ProDosExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new ProDosBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 512, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
