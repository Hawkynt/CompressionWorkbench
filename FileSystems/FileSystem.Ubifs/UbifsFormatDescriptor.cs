#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ubifs;

/// <summary>
/// UBIFS (Unsorted Block Image File System) descriptor.
/// Read path: triage artifacts (passthrough, node-counts metadata, flat inode +
/// dentry tables) plus real per-file extraction via linear log scan with zlib /
/// stored DATA-node support.
/// Write path (R/W): emits a flat sequence of superblock + master + inode +
/// dentry + zlib-compressed data nodes for Create, and appends fresh INO /
/// DENT / DATA nodes at the journal head for Add / Replace / Remove. Committed
/// nodes stay byte-identical at their original offsets — the kernel-style
/// log-structured invariant (no in-place rewrites until commit-merge) is
/// preserved. Full TNC / LPT commit pipeline (required for kernel mount) is
/// multi-week work and remains out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.linux-mtd.infradead.org/doc/ubifs.html</c> — MTD project UBIFS documentation — the canonical design doc</description></item>
///   <item><description><c>https://github.com/torvalds/linux/blob/master/fs/ubifs/ubifs-media.h</c> — canonical on-disk node formats</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/ubifs.html</c> — kernel documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/UBIFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
/// <summary>
/// Why this volume is laid out again by rebuilding rather than by moving.
/// </summary>
/// <remarks>
/// Nothing here reads the index. A file's data nodes are found by scanning the
/// image for node magic, not by walking the tree that records where they are —
/// the TNC that indexes them and the LPT that accounts for each erase block are
/// not decoded at all. So there is no field to repoint: what would have to be
/// rewritten for a moved node to be found again is a structure this
/// implementation cannot yet read, let alone write.
/// </remarks>
public sealed class UbifsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap {

  /// <summary>
  /// Largest image the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  // ── IFilesystemExtentMap ────────────────────────────────────────────────

  /// <summary>
  /// Where the log keeps its nodes: the ones carrying a file's bytes under its
  /// inode number, and the ones describing the volume as structure.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    List<UbifsLayout.Node> nodes;
    try {
      nodes = UbifsLayout.Nodes(image);
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      yield break;
    }

    if (nodes.Count == 0) yield break;

    // Everything before the first node is the superblock and the master nodes,
    // which are found where they are.
    var first = nodes.Min(n => n.Offset);
    if (first > 0)
      yield return new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved, "UBIFS head");

    foreach (var node in nodes.OrderBy(n => n.Offset))
      yield return UbifsLayout.IsData(node.Type)
        ? new DefragBlockInfo(node.Offset, node.Length, DefragBlockKind.Used,
            $"inode {node.InodeNumber}")
        : new DefragBlockInfo(node.Offset, node.Length, DefragBlockKind.MetadataReserved,
            "UBIFS node");
  }

  // ── IArchiveDefragmentable ──────────────────────────────────────────────

  /// <summary>
  /// Lays the image out again by moving nodes. Nothing records where a node is:
  /// the log is walked by looking for the magic at the head of each one, so a
  /// move repoints nothing and only has to leave nothing behind.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (archive.CanSeek && archive.Length <= PlannerImageCap) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException(
        $"UBIFS can only rebuild an image packed from the start; got {options.Mode}.");

    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new UbifsFileReader(stream);
    return reader.Entries
      .Where(e => !e.IsDirectory)
      .OrderBy(e => e.Name, StringComparer.Ordinal)
      .Select(reader.Extract)
      .ToList();
  }

  /// <summary>Plans the new layout and moves the nodes into it.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new UbifsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
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

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The only writer-honoured knob is the LEB (logical erase block) size: it is
  /// written into the superblock's <c>leb_size</c> field and each LEB in the image
  /// is padded to it (nodes never straddle an LEB boundary). UBIFS carries no
  /// volume-label field in this writer, so no label knob is published; DATA-node
  /// compression is fixed to zlib-or-stored and is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "LebSize", displayName: "LEB size",
      min: 4096, max: 1048576, defaultLabel: "64 KB",
      description: "Logical erase-block size. Written to the superblock and used to pad each LEB; 64 KB matches common NAND flash."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Ubifs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "UBIFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".ubifs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".ubifs", ".ubi", ".img"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x06101831 LE = 31 18 10 06
    new([0x31, 0x18, 0x10, 0x06], Offset: 0, Confidence: 0.35),
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
  public string Description => "Unsorted Block Image File System (Linux raw-flash) — linear log scan w/ zlib data nodes; Create emits superblock+master+inode+dentry+data, Add/Replace/Remove append journal-style nodes at the journal head (committed nodes byte-identical; self-round-trip only — full TNC/LPT commit out of scope).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ubifs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // triage entries alongside them made every rebuild (shrink, defrag) fold them
    // back in as real files, which is why defrag refused the round-trip; they stay
    // on the carver path — images that hold no readable file.
    List<UbifsFileReader.FileEntry> real = [];
    try {
      real = [.. new UbifsFileReader(image).Entries.Where(e => !e.IsDirectory)];
    } catch {
      // best-effort: triage-only surface
    }

    if (real.Count > 0) {
      foreach (var e in real)
        entries.Add(new ArchiveEntryInfo(entries.Count, e.Name, e.Size, e.Size, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.ubifs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    var scan = UbifsScanner.Scan(image);
    if (scan.Inodes.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "inodes.txt", 0, 0, "stored", false, false, null));
    if (scan.Dentries.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "dentries.txt", 0, 0, "stored", false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    // A volume that carries files extracts exactly those, mirroring List.
    try {
      var withFiles = new UbifsFileReader(image);
      if (withFiles.Entries.Any(e => !e.IsDirectory)) {
        foreach (var e in withFiles.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          WriteFile(outputDir, e.Name, withFiles.Extract(e));
        }
        return;
      }
    } catch {
      // fall through to the triage surface
    }

    WriteIfMatch(outputDir, "FULL.ubifs", image, files);

    UbifsScanner.ScanResult scan;
    try {
      scan = UbifsScanner.Scan(image);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(scan), files);

    if (scan.Inodes.Count > 0)
      WriteIfMatch(outputDir, "inodes.txt", BuildInodesTable(scan), files);
    if (scan.Dentries.Count > 0)
      WriteIfMatch(outputDir, "dentries.txt", BuildDentriesTable(scan), files);

    // Real per-file extraction via the on-disk reader, when parseable.
    try {
      var reader = new UbifsFileReader(image);
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        var data = reader.Extract(e);
        WriteFile(outputDir, e.Name, data);
      }
    } catch {
      // best-effort: triage-only extraction
    }
  }

  /// <summary>
  /// Opens a single file entry as a bounded stream over its reassembled
  /// (and optionally zlib-decompressed) DATA blocks. Reads past the entry's
  /// logical size return 0 (EOF). Unknown names return an empty bounded stream.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    try {
      var reader = new UbifsFileReader(archive);
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
        var data = reader.Extract(e);
        return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
      }
    } catch {
      // fall through to empty
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

  /// <summary>
  /// Emits a self-contained UBIFS image (superblock + master + linear log of
  /// inode/dentry/data nodes) over <paramref name="output"/>. Round-trips through
  /// this descriptor's reader; kernel-mount round-trip requires the full
  /// wandering-tree commit pipeline which is out of scope here.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new UbifsWriter(ResolveLebSize(options));
    foreach (var (name, data) in FilesOnly(inputs))
      writer.AddFile(name, data);
    writer.WriteTo(output);
  }

  /// <summary>
  /// Resolves the writer's LEB size from the schema. "Auto"/absent keeps
  /// <see cref="UbifsWriter.DefaultLebSize"/>; a pinned power-of-two size label is
  /// parsed back to bytes.
  /// </summary>
  private static int ResolveLebSize(FormatCreateOptions? options) {
    var parsed = FilesystemSchemaPresets.ParseSize(options?.GetOption("LebSize", "Auto"));
    return parsed > 0 ? parsed : UbifsWriter.DefaultLebSize;
  }

  // ── IArchiveModifiable ────────────────────────────────────────────────

  /// <summary>
  /// Appends fresh INO + DENT + DATA nodes at the journal head for each input.
  /// Existing entries with the same leaf name are replaced (same inode #, new
  /// sqnum on a fresh INO + DATA), preserving the kernel UBIFS invariant that
  /// committed nodes are never overwritten until commit-merge. Every byte of
  /// every previously written node stays byte-identical at its original offset
  /// after Add — only the trailing 0xFF padding of the journal-head LEB is
  /// overwritten (and the image grows if the appended nodes spill past it).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    UbifsInPlaceModifier.AddFiles(archive, inputs);
  }

  /// <summary>
  /// Appends tombstone DENT nodes (inum=0) for each named entry at the journal
  /// head. Reader's last-sqnum-wins drops the entry from the listing; old DENT
  /// + INO + DATA nodes stay byte-identical at their original offsets.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    UbifsInPlaceModifier.RemoveFiles(archive, entryNames);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(scan.ParseOk ? "ok" : "partial")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_nodes={scan.TotalNodes}\n");
    sb.Append(CultureInfo.InvariantCulture, $"superblock_found={scan.SuperblockFound}\n");
    sb.Append(CultureInfo.InvariantCulture, $"leb_size_if_known={scan.LebSizeIfKnown}\n");
    sb.Append("[node_counts_by_type]\n");
    foreach (var kv in scan.NodeCountsByType.OrderBy(p => p.Key))
      sb.Append(CultureInfo.InvariantCulture, $"{NodeTypeName(kv.Key)}={kv.Value}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildInodesTable(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var i in scan.Inodes)
      sb.Append(CultureInfo.InvariantCulture, $"{i.InodeNum}\t{i.Size}\t{i.Flags}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDentriesTable(UbifsScanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var d in scan.Dentries)
      sb.Append(CultureInfo.InvariantCulture, $"{d.ParentInode}\t{d.Name}\t{d.Type}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string NodeTypeName(byte t) => t switch {
    0 => "inode",
    1 => "data",
    2 => "dentry",
    3 => "xentry",
    4 => "trun",
    5 => "pad",
    6 => "sb",
    7 => "master",
    8 => "ref",
    9 => "idx",
    10 => "cs",
    11 => "orph",
    _ => $"type_{t}",
  };

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
