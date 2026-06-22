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
/// </summary>
public sealed class UbifsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable {

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

  public string Id => "Ubifs";
  public string DisplayName => "UBIFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ubifs";
  public IReadOnlyList<string> Extensions => [".ubifs", ".ubi", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x06101831 LE = 31 18 10 06
    new([0x31, 0x18, 0x10, 0x06], Offset: 0, Confidence: 0.35),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unsorted Block Image File System (Linux raw-flash) — linear log scan w/ zlib data nodes; Create emits superblock+master+inode+dentry+data, Add/Replace/Remove append journal-style nodes at the journal head (committed nodes byte-identical; self-round-trip only — full TNC/LPT commit out of scope).";

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

    entries.Add(new ArchiveEntryInfo(0, "FULL.ubifs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    var scan = UbifsScanner.Scan(image);
    if (scan.Inodes.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "inodes.txt", 0, 0, "stored", false, false, null));
    if (scan.Dentries.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "dentries.txt", 0, 0, "stored", false, false, null));

    // Real per-file entries from the on-disk reader, when parseable.
    try {
      var reader = new UbifsFileReader(image);
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        entries.Add(new ArchiveEntryInfo(entries.Count, e.Name, e.Size, e.Size, "stored", false, false, null));
      }
    } catch {
      // best-effort: triage-only surface
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
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
