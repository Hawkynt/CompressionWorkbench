#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.HfsPlus;

public sealed class HfsPlusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// HFS+ creation knobs: HFSX case-sensitivity toggle, journal enable +
  /// journal-size selector, volume name and allocation block size. The block
  /// size dropdown offers Auto (slack + table-overhead minimisation) plus the
  /// power-of-two sizes 4 KB … 64 KB that the writer supports; the
  /// journal-size knob is gated on Journal=true via DependsOn.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "CaseSensitive", DisplayName: "Case-sensitive (HFSX)", Kind: FormatOptionKind.Boolean, Default: "false",
      Description: "Make filename comparison case-sensitive (emit the HFSX 'HX' signature + binary comparator)."),
    new FormatOptionDescriptor(
      Key: "Journal", DisplayName: "Enable journal", Kind: FormatOptionKind.Boolean, Default: "true",
      Description: "Enable the volume journal."),
    new FormatOptionDescriptor(
      Key: "JournalSize", DisplayName: "Journal size", Kind: FormatOptionKind.Integer, Default: "8388608",
      AllowedValues: ["8388608", "16777216", "33554432", "67108864"],
      Description: "Journal size in bytes (8/16/32/64 MiB).",
      DependsOn: "Journal=true"),
    FilesystemSchemaPresets.VolumeLabel(),
    FilesystemSchemaPresets.ClusterSize(
      key: "BlockSize",
      displayName: "Allocation block size",
      min: 4096, max: 65536,
      description: "HFS+ allocation block size (power of two, 4 KB … 64 KB). " +
        "Auto picks the size that minimises slack + allocation-bitmap and B-tree overhead."),
  ];

  /// <summary>
  /// Walks the HFS+ catalog B-tree leaf chain and yields the actual on-disk
  /// byte layout — reserved boot region + volume header + allocation file +
  /// catalog file as <see cref="DefragBlockKind.MetadataReserved"/>, every
  /// file record's first data-fork extent
  /// (<c>HFSPlusForkData.extents[0]</c>) as
  /// <see cref="DefragBlockKind.Used"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HfsPlusExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the HFS+ image: free allocation blocks, gaps
  /// between files and the block-tip slack between a file's logical size and
  /// the end of its last allocated block. The catalog extent map clamps each
  /// file's first-fork run to its logical byte length, so trailing slack
  /// inside the final block presents as a free gap that the generic
  /// <see cref="UnusedSpaceWiper"/> zero-fills. The size lookup is keyed by the
  /// reader's full path, matching the extent map's FileName.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new HfsPlusReader(image, leaveOpen: true);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.FullPath] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = HfsPlusExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public string Id => "HfsPlus";
  public string DisplayName => "HFS+";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing HFS+ image via
  /// <see cref="HfsPlusModifier.AddFile"/>. The modifier mutates the catalog
  /// leaf, allocation bitmap, and volume header in place; on leaf overflow it
  /// transparently falls back to a writer-driven rebuild so the call always
  /// succeeds.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs))
      HfsPlusModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing HFS+ image via
  /// <see cref="HfsPlusModifier.RemoveFile"/>. File data blocks are wiped and
  /// the catalog records are excised from the leaf node; missing names are
  /// silently ignored.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      HfsPlusModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".dmg";
  public IReadOnlyList<string> Extensions => [".dmg", ".hfsx", ".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x48, 0x2B], Offset: 1024, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hfsplus", "HFS+")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Apple HFS+ filesystem image. Writer emits full 248-byte TN1150
  /// HFSPlusCatalogFile records with HFSPlusForkData at offsets 88/168.
  /// </summary>
  public string Description => "Apple HFS+ filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
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
    var r = new HfsPlusReader(archive, leaveOpen: true);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var caseSensitive = options.GetOptionBool("CaseSensitive", false);
    var journal = options.GetOptionBool("Journal", true);
    var journalSize = options.GetOptionInt("JournalSize", 8 * 1024 * 1024);
    // Honour VolumeLabel (preset key) first; fall back to VolumeName for the
    // earlier stash schema; finally default to "Untitled".
    var volumeName = options.GetOption("VolumeLabel", options.GetOption("VolumeName", "Untitled"));

    var w = new HfsPlusWriter(caseSensitive, journal, journalSize, volumeName);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    // "BlockSize" → bytes (0 = Auto). The writer's optimizer confirms or bumps.
    var blockSize = FilesystemSchemaPresets.ParseSize(
      options.FormatSpecific?.GetValueOrDefault("BlockSize"));
    output.Write(w.BuildAutoSized(blockSize));
  }

  /// <summary>
  /// Streaming creation: drains each
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput"/> via
  /// its bounded <c>OpenStream</c> factory and feeds the HFS+ writer one
  /// file at a time.
  /// </summary>
  /// <remarks>
  /// The current <see cref="HfsPlusWriter"/> requires every file's bytes
  /// up-front (it sizes the catalog + extents B-tree after all files are
  /// known), so this override one-pass-on-writer / two-pass-on-caller.
  /// The bound is enforced at the SOURCE: each input's <c>OpenStream</c>
  /// returns a bounded stream so cluster/extent slack can never enter the
  /// pipeline. TODO: refactor HfsPlusWriter to true two-pass streaming.
  /// </remarks>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var caseSensitive = options.GetOptionBool("CaseSensitive", false);
    var journal = options.GetOptionBool("Journal", true);
    var journalSize = options.GetOptionInt("JournalSize", 8 * 1024 * 1024);
    var volumeName = options.GetOption("VolumeLabel", options.GetOption("VolumeName", "Untitled"));
    var w = new HfsPlusWriter(caseSensitive, journal, journalSize, volumeName);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      using var src = input.OpenStream();
      using var ms = new MemoryStream(checked((int)input.Size));
      src.CopyTo(ms);
      w.AddFile(input.Name, ms.ToArray());
    }
    var blockSize = FilesystemSchemaPresets.ParseSize(
      options.FormatSpecific?.GetValueOrDefault("BlockSize"));
    output.Write(w.BuildAutoSized(blockSize));
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware HFS+ defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a contiguous,
  /// start-packed allocation block layout, so all four <see cref="DefragMode"/>
  /// values converge on a clean repack.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HfsPlusReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsPlusWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
