#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.F2fs;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/f2fs.html</c> — Linux kernel F2FS documentation (on-disk layout: SB/CP/SIT/NAT/SSA/main area)</description></item>
///   <item><description><c>https://www.usenix.org/conference/fast15/technical-sessions/presentation/lee</c> — Lee et al., "F2FS: A New File System for Flash Storage" (USENIX FAST '15), the design paper</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/F2FS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class F2fsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // A F2FS segment is 2 MiB; image size in bytes = segment count × 2 MiB.
  private const long SegmentSizeBytes = 2L * 1024 * 1024;

  // ── IFormatOptionsSchema ────────────────────────────────────────────────
  // Image-size presets all map to a segment count (MB / 2 = segments). The smallest
  // offered preset (64 MB = 32 segments) is well above the writer's 16-segment floor.
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(["64 MB", "128 MB", "256 MB", "512 MB", "1 GB", "2 GB"]),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  public string Id => "F2fs";
  public string DisplayName => "F2FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".f2fs";
  public IReadOnlyList<string> Extensions => [".f2fs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x10, 0x20, 0xF5, 0xF2], Offset: 1024, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// F2FS flash-friendly filesystem image — R/W via log-structured append.
  /// Add/Remove mutate in place: writes land in the open WARM_DATA/WARM_NODE
  /// current segments (no full image rebuild) and advance to fresh main-area
  /// segments of the right CURSEG_* type when the open one fills. On-disk NAT
  /// and SIT entries are always updated; the NAT/SIT journals in the compact
  /// summary block are mirrored when there is room and silently fall through
  /// to disk when full (the on-disk entry is authoritative — f2fs-tools
  /// treats the journal as overrides over disk). When the root inline-dentry
  /// region is full the directory is converted in place to a regular
  /// block-based dentry directory whose entries live in HOT_DATA blocks. The
  /// checkpoint version + CRC are advanced into the alternate pack so the
  /// prior pack stays as a roll-back. Genuinely out of scope: subdirectory
  /// creation, nested removal, growing the main-area segment count, and
  /// multi-level indirect inode trees.
  /// </summary>
  public string Description => "F2FS flash-friendly filesystem image (R/W via log-structured append; "
    + "full NAT/SIT block rewrite + regular dentry blocks on overflow)";

  // --- WORM write constraints ---
  // F2FS minimum image = ~30 MB in the real-world mkfs.f2fs tool; our writer emits 64 MB by
  // default. No per-file ceiling is imposed at the descriptor level — the writer rejects
  // individual files > 923 × 4096 ≈ 3.6 MB (single-extent direct-block limit).
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 64L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "F2FS filesystem image (flat root directory, inline dentries; per-file max ≈ 3.6 MB).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = null; return true; }
    try {
      var length = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      if (length > 923L * 4096L) {
        reason = $"F2FS writer supports only direct-pointer files (max {923 * 4096} bytes per file).";
        return false;
      }
    } catch {
      // If we can't stat it, let Create fail with the real reason.
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new F2fsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new F2fsReader(stream);
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
    var r = new F2fsReader(archive);
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
    var specific = options.FormatSpecific;
    var segments = ParseImageSizeSegments(specific?.GetValueOrDefault("ImageSize"));
    var label = specific?.GetValueOrDefault("VolumeLabel");

    var w = new F2fsWriter();
    w.SetVolumeLabel(label);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    var image = segments > 0 ? w.Build(segments) : w.BuildAutoSized();
    output.Write(image, 0, image.Length);
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive the F2FS
  /// segment geometry in pass 1; pass 2 emits the metadata image with each
  /// file's WARM_DATA blocks left zero, then streams each input's bytes from
  /// its <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its first allocated data block via 64 KB chunks. The output is
  /// byte-identical to <see cref="Create"/> for the same inputs (F2FS has no
  /// per-block content checksum). Falls back to the buffered default when the
  /// target stream is not seekable.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var inputList = inputs.ToList();
    if (!output.CanSeek) {
      // Non-seekable target: fall back to the buffered default (two-pass needs seek).
      ((IArchiveCreatable)this).CreateFromStreams(output, inputList, options);
      return;
    }

    var specific = options.FormatSpecific;
    var segments = ParseImageSizeSegments(specific?.GetValueOrDefault("ImageSize"));
    var label = specific?.GetValueOrDefault("VolumeLabel");

    var w = new F2fsWriter();
    w.SetVolumeLabel(label);
    foreach (var input in inputList) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output, segments);
  }

  // Maps an image-size preset label to a F2FS segment count (2 MiB per segment).
  // "Auto (fit to files)" / unknown → 0, signalling BuildAutoSized().
  private static int ParseImageSizeSegments(string? s) => s?.Trim() switch {
    "64 MB"  => (int)(64L * 1024 * 1024 / SegmentSizeBytes),    // 32
    "128 MB" => (int)(128L * 1024 * 1024 / SegmentSizeBytes),   // 64
    "256 MB" => (int)(256L * 1024 * 1024 / SegmentSizeBytes),   // 128
    "512 MB" => (int)(512L * 1024 * 1024 / SegmentSizeBytes),   // 256
    "1 GB"   => (int)(1024L * 1024 * 1024 / SegmentSizeBytes),  // 512
    "2 GB"   => (int)(2L * 1024 * 1024 * 1024 / SegmentSizeBytes), // 1024
    _        => 0, // Auto (fit to files)
  };

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware F2FS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start multi-segment image (SIT/NAT journals, checkpoint
  /// pack, inline-dentry root).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveModifiable (in-place log-structured mutation) ──────────────
  // F2FS is a log-structured FS: Add appends new data + node blocks to the
  // open WARM_DATA/WARM_NODE current segments, promotes a fresh free segment
  // of the right CURSEG_* type when the open one fills, updates on-disk
  // NAT/SIT (the source of truth), best-effort mirrors NAT/SIT updates in
  // the compact summary block's journals (skipping silently when full —
  // f2fs-tools treats the journal as overrides over on-disk), and stamps a
  // fresh checkpoint (version + CRC) into the older of the two CP packs so
  // the previous pack stays as a roll-back snapshot. When the root
  // inline-dentry region fills the directory is converted in place to a
  // regular block-based dentry directory and Adds continue against
  // HOT_DATA blocks. Remove clears the dentry (inline or regular block),
  // invalidates the NAT entry, clears SIT valid_map bits, and wipes the
  // inode + data block bytes.
  //
  // Scope: root-level files only. Subdirectory creation, nested removal,
  // multi-level indirect inode trees, and main-area-segment growth are
  // genuinely out of scope.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    if (!archive.CanSeek)
      throw new NotSupportedException("F2fs: Add requires a seekable stream.");

    var image = ReadAll(archive);
    var files = inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent())).ToList();
    // Replace-by-name: drop any existing entry of the same name first so an update
    // overwrites rather than leaving a duplicate directory entry.
    image = F2fsModifier.RemoveFiles(image, files.Select(f => f.ArchiveName).ToList());
    var updated = F2fsModifier.AddFiles(image, files);
    WriteAll(archive, updated);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    if (!archive.CanSeek)
      throw new NotSupportedException("F2fs: Remove requires a seekable stream.");

    var image = ReadAll(archive);
    var updated = F2fsModifier.RemoveFiles(image, entryNames);
    WriteAll(archive, updated);
  }

  private static byte[] ReadAll(Stream s) {
    s.Position = 0;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteAll(Stream s, byte[] data) {
    s.Position = 0;
    s.Write(data, 0, data.Length);
    s.SetLength(data.Length);
    s.Position = 0;
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new F2fsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
