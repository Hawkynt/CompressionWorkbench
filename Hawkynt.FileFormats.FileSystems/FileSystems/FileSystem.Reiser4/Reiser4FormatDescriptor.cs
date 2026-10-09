#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Reiser4;

/// <summary>
/// Descriptor for Reiser4 filesystem images (successor to ReiserFS 3.6 — completely
/// different on-disk layout). Volumes whose node40 tree the reader walks completely list
/// and extract their files and directories, and are created and edited through staged
/// rebuilds that carry the volume's identity and every object's metadata across; other
/// images surface the master superblock at offset 65536, the format40 superblock that
/// follows it, a structured metadata bundle and the raw image.
///
/// Magic:
/// <list type="bullet">
///   <item><description><c>"ReIsEr4"</c> at offset 65536 — master superblock <c>ms_magic[16]</c>.</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://archive.kernel.org/oldwiki/reiser4.wiki.kernel.org/</c> — archived Reiser4 wiki (format40 layout, plugin system)</description></item>
///   <item><description>reiser4progs (<c>mkfs.reiser4</c> / <c>debugfs.reiser4</c>) — canonical userspace tooling</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Reiser4</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
[FilesystemBlockMover(typeof(Reiser4BlockMover))]
public sealed class Reiser4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Knobs the empty-filesystem writer actually honours. <c>VolumeLabel</c> is
  /// written into the master superblock label field (and the backup record) and
  /// surfaces through <c>fsck.reiser4</c> / our metadata readback;
  /// <c>ImageSize</c> drives <see cref="Reiser4Writer.BlockCount"/> (4&#160;KB
  /// blocks, clamped to the writer minimum). The 4&#160;KB block size is fixed —
  /// the embedded mkfs.reiser4 templates are byte-exact 4096-byte captures — so
  /// it is intentionally not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB"]),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Reiser4";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Reiser4";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".reiser4";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".reiser4"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "ReIsEr4" at byte offset 65536 (= 16 * 4096). Confidence 0.9: the 7-byte
    // magic is highly unlikely to land at exactly this offset by chance, but
    // it shares the "ReIsEr" prefix with the older ReiserFS 3.6 magic
    // ("ReIsErFs", "ReIsEr2Fs", "ReIsEr3Fs") which lives at offset 65536+52.
    // The disambiguation is unambiguous (different offsets, different suffixes)
    // so 0.9 is appropriate — slightly below the 0.95 used for unique 4+ byte
    // magics that live at position 0.
    new("ReIsEr4"u8.ToArray(), Offset: (int)Reiser4MasterSb.MasterOffset, Confidence: 0.9),
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
  public string Description => "Reiser4 filesystem image — native node40 traversal and multi-leaf writing with nested directories and metadata-preserving rebuilds.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      // Stream blew up before we got anywhere. Surface the irreducible minimum.
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    var payload = ReadPayload(stream);
    var idx = 0;
    if (payload.Count > 0) {
      foreach (var e in payload)
        entries.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "stored", e.IsDirectory, false,
          e.Metadata?.LastModified));
      return entries;
    }

    if (stream.CanSeek) stream.Position = 0;
    using (var native = new Reiser4Reader(stream))
      if (native.NativeTreeValid) return entries;

    entries.Add(new ArchiveEntryInfo(idx++, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "master_superblock.bin", sb.MasterRaw.LongLength, sb.MasterRaw.LongLength, "stored", false, false, null));
    if (sb.Format40Present)
      entries.Add(new ArchiveEntryInfo(idx++, "format40_superblock.bin", sb.Format40Raw.LongLength, sb.Format40Raw.LongLength, "stored", false, false, null));
    return entries;
  }

  /// <summary>Files the workbench-layout payload area holds. Never throws — empty when there are none.</summary>
  private static IReadOnlyList<Reiser4Reader.Entry> ReadPayload(Stream stream) {
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new Reiser4Reader(stream);
      return reader.Entries;
    } catch {
      return [];
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.reiser4", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    // A volume that carries files extracts exactly those, mirroring List.
    if (stream.CanSeek) stream.Position = 0;
    using (var reader = new Reiser4Reader(stream)) {
      if (reader.NativeTreeValid || reader.Entries.Count > 0) {
        foreach (var e in reader.Entries) {
          if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
          var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
          using var output = File.Create(target);
          reader.ExtractTo(e, output);
        }
        return;
      }
    }

    WriteIfMatch(outputDir, "FULL.reiser4", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "master_superblock.bin", sb.MasterRaw, files);
    if (sb.Format40Present)
      WriteIfMatch(outputDir, "format40_superblock.bin", sb.Format40Raw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Reiser4MasterSb sb) {
    var b = new StringBuilder();
    b.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    b.Append(CultureInfo.InvariantCulture, $"master_offset={Reiser4MasterSb.MasterOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"blocksize={sb.BlockSize}\n");
    b.Append(CultureInfo.InvariantCulture, $"disk_plugin_id={sb.DiskPluginId}\n");
    b.Append(CultureInfo.InvariantCulture, $"uuid_hex={sb.UuidHex}\n");
    b.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    b.Append(CultureInfo.InvariantCulture, $"format40_present={sb.Format40Present}\n");
    if (sb.Format40Present) {
      b.Append(CultureInfo.InvariantCulture, $"block_count={sb.BlockCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"free_blocks={sb.FreeBlocks}\n");
      b.Append(CultureInfo.InvariantCulture, $"root_block={sb.RootBlock}\n");
      b.Append(CultureInfo.InvariantCulture, $"file_count={sb.FileCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"mkfs_id=0x{sb.MkfsId:X8}\n");
      b.Append(CultureInfo.InvariantCulture, $"tree_height={sb.TreeHeight}\n");
      b.Append(CultureInfo.InvariantCulture, $"tail_policy={sb.Policy}\n");
      b.Append(CultureInfo.InvariantCulture, $"format40_version={sb.Format40Version}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // ── IArchiveCreatable ────────────────────────────────────────────────
  // A captured mkfs prefix supplies the format defaults; native stat40, cde40
  // and extent40 items describe the supported namespace across native nodes.
  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => CreateCore(output, inputs, options);

  private static void CreateCore(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options,
      RebuildMetadata? saved = null) {
    ArgumentNullException.ThrowIfNull(output);
    var w = new Reiser4Writer();
    if (saved is not null) {
      w.Label = saved.Label;
      w.Uuid = Convert.FromHexString(saved.UuidHex);
      w.MkfsId = saved.MkfsId;
      w.BlockCount = (ulong)(saved.Length / Reiser4Writer.BlockSize);
      w.AddDirectory("", saved.Root);
    }

    // Volume label: prefer the schema knob, falling back to the legacy
    // password-slot mapping (kept for callers that pre-date the schema).
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(options?.Password))
      label = options.Password;
    if (!string.IsNullOrEmpty(label))
      w.Label = label;

    // Image size: the writer counts 4 KB blocks. "Auto (fit to files)" and any
    // unset/unparsable value leave the default (and writer minimum) in place.
    var sizes = new List<long>();
    if (inputs != null)
      foreach (var i in inputs) {
        var name = i.ArchiveName.Replace('\\', '/').TrimEnd('/');
        var metadata = saved?.Entries.GetValueOrDefault(name)?.Metadata;
        if (i.IsDirectory) { w.AddDirectory(name, metadata); continue; }
        var length = i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
        sizes.Add(length);
        if (i.InMemoryContent is { } bytes) {
          w.AddFile(i.ArchiveName, bytes, metadata);
          continue;
        }
        var path = i.FullPath;
        w.AddStreamingFile(i.ArchiveName, length, () => File.OpenRead(path), metadata);
      }

    // The requested size is a floor: the volume has to be at least large enough
    // for the payload's directory chain, data blocks and bitmaps.
    var sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    var requested = sizeBytes > 0 ? (ulong)Math.Max(1, sizeBytes / Reiser4Writer.BlockSize) : 0UL;
    w.BlockCount = Math.Max(w.BlockCount, Math.Max(requested, Reiser4Writer.EstimateBlockCount(sizes)));

    w.Write(output);
  }

  /// <summary>
  /// Creates the supported native node40 profile from input streams. Each source
  /// is consumed once into a temporary file, then copied in bounded chunks by
  /// <see cref="Reiser4Writer"/>; no complete file is staged in memory. Directory
  /// inputs retain empty directories and nested paths.
  /// </summary>
  public void CreateFromStreams(Stream target,
      IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new Reiser4Writer();
    var staged = new List<string>();
    var sizes = new List<long>();
    try {
      foreach (var input in inputs) {
        if (input.IsDirectory) { writer.AddDirectory(input.Name); continue; }
        if (input.Size < 0) throw new ArgumentOutOfRangeException(nameof(inputs), "Input sizes must be non-negative.");
        var path = Path.Combine(Path.GetTempPath(), "cwb-reiser4-" + Guid.NewGuid().ToString("N"));
        staged.Add(path);
        using (var spool = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                 FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan)) {
          using var source = input.OpenStream();
          var buffer = new byte[64 * 1024];
          var remaining = input.Size;
          while (remaining > 0) {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
              throw new EndOfStreamException($"Reiser4: '{input.Name}' ended before its declared {input.Size:N0}-byte size.");
            spool.Write(buffer, 0, read);
            remaining -= read;
          }
          spool.Flush();
        }
        sizes.Add(input.Size);
        var capturedPath = path;
        writer.AddStreamingFile(input.Name, input.Size, () => new FileStream(capturedPath,
          FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
          FileOptions.SequentialScan));
      }

      var label = options?.GetOption("VolumeLabel", "") ?? "";
      if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(options?.Password))
        label = options.Password;
      if (!string.IsNullOrEmpty(label)) writer.Label = label;

      var requestedBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
      var requestedBlocks = requestedBytes > 0
        ? (ulong)Math.Max(1, requestedBytes / Reiser4Writer.BlockSize)
        : 0UL;
      writer.BlockCount = Math.Max(requestedBlocks, Reiser4Writer.EstimateBlockCount(sizes));
      writer.Write(target);
    } finally {
      foreach (var path in staged) {
        try { File.Delete(path); } catch { /* best-effort cleanup of staging data */ }
      }
    }
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new Reiser4Reader(stream, leaveOpen: true);
    return reader.Entries.Where(e => !e.IsDirectory && e.Size > 0).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing at the end.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Reiser4BlockMover();
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

    // The directory is written once every run has landed: a file's position is
    // one field, and what it means depends on where all of its blocks are.
    mover.SettleDirectory(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Rewrites the volume with every file laid out contiguously from the start of
  /// the payload area. Each entry is spilled to scratch and the writer pulls it
  /// back, so the rebuild is not bounded by what a byte[] can hold.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    var saved = CaptureMetadata(archive);

    using (var reader = new Reiser4Reader(archive)) {
      var plannerProfile = reader.NativeNodeBlocks.Count == 2 && reader.NativeNodeBlocks.Contains(23UL) &&
        reader.NativeNodeBlocks.Contains(24UL) && reader.Entries.All(static entry => !entry.IsDirectory);
      if (plannerProfile && archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
        var planned = false;
        DefragContentGuard.RunOrRebuild(archive,
          readContents: ReadPayloadsForGuard,
          inPlace: () => {
            DefragmentWithPlanner(archive, options);
            var moved = CaptureMetadata(archive);
            if (saved.Root is { } root && !MetadataMatches(root, moved.Root) ||
                saved.Entries.Any(pair => !moved.Entries.TryGetValue(pair.Key, out var entry) ||
                  pair.Value.Metadata is { } metadata && !MetadataMatches(metadata, entry.Metadata)))
              throw new InvalidDataException("Reiser4: in-place defrag changed native metadata.");
            planned = true;
          },
          rebuild: () => planned = false);
        if (planned) return;
        archive.Position = 0;
      }
    }

    if (options.Mode is not (DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy))
      throw new NotSupportedException(
        $"Reiser4 defragmentation supports ConsolidateAtStart and FillHolesLazy; got {options.Mode}.");

    using var source = new RebuildSource(this, archive);
    RebuildVerb.RebuildInPlace(archive, source, new MetadataCreator(saved),
      onProgress: options.OnProgress, cancellationToken: options.CancellationToken);
  }

  /// <summary>Rebuilds tightly while retaining native metadata, or copies through on failure.</summary>
  public void Shrink(Stream input, Stream output) {
    using var staged = RebuildVerb.CreateScratchStream();
    var useStaged = false;
    try {
      var saved = CaptureMetadata(input) with { Length = 0 };
      using var source = new RebuildSource(this, input);
      RebuildVerb.RebuildToStream(input, staged, source, new MetadataCreator(saved));
      useStaged = staged.Length < input.Length;
    } catch { /* retain the original when the supported writer cannot represent it */ }
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    if (useStaged) { staged.Position = 0; staged.CopyTo(output); }
    else input.CopyTo(output);
  }

  /// <summary>Rebuilds the supported native profile while carrying existing metadata.</summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.UnitSize is not (0 or Reiser4Writer.BlockSize) || options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("Reiser4: this writer uses 4096-byte blocks without sparse or shared-file transforms.");
    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (options.ImageSize > 0) parameters["ImageSize"] = options.ImageSize.ToString(CultureInfo.InvariantCulture);
    if (options.Parameters is not null)
      foreach (var pair in options.Parameters) {
        if (!pair.Key.Equals("ImageSize", StringComparison.OrdinalIgnoreCase))
          throw new NotSupportedException($"Reiser4: unsupported rebuild parameter '{pair.Key}'.");
        parameters[pair.Key] = pair.Value;
      }
    var saved = CaptureMetadata(source) with { Length = 0 };
    using var sourceOps = new RebuildSource(this, source);
    RebuildVerb.RebuildToStream(source, target, sourceOps, new MetadataCreator(saved), parameters);
  }

  private static string SafeMemberPath(string root, string name) {
    var components = name.Replace('\\', '/').TrimEnd('/').Split('/');
    if (components.Length == 0 || components.Any(static component => component.Length == 0 ||
        component is "." or ".." || component.Contains(':') || component.Contains('\0')))
      throw new InvalidDataException($"Reiser4: invalid member path '{name}'.");
    return Path.Combine(root, Path.Combine(components));
  }

  // One native walk for a rebuild; opening every member must not reparse a wide tree.
  private sealed class RebuildSource : IArchiveFormatOperations, IDisposable {
    private readonly Reiser4FormatDescriptor _owner;
    private readonly Stream _source;
    private readonly Reiser4Reader _reader;
    private readonly Dictionary<string, Reiser4Reader.Entry> _entries;

    internal RebuildSource(Reiser4FormatDescriptor owner, Stream source) {
      this._owner = owner;
      this._source = source;
      this._reader = new Reiser4Reader(source);
      if (!this._reader.NativeTreeValid) throw new NotSupportedException("Reiser4: unsupported rebuild source.");
      this._entries = this._reader.Entries.ToDictionary(static entry => entry.Name, StringComparer.Ordinal);
    }

    public List<ArchiveEntryInfo> List(Stream stream, string? password)
      => ReferenceEquals(stream, this._source)
        ? this._reader.Entries.Select(static (entry, index) => new ArchiveEntryInfo(index, entry.Name,
          entry.Size, entry.Size, "stored", entry.IsDirectory, false, entry.Metadata?.LastModified)).ToList()
        : this._owner.List(stream, password);

    public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
      if (!ReferenceEquals(stream, this._source)) { this._owner.Extract(stream, outputDir, password, files); return; }
      foreach (var entry in this._reader.Entries) {
        if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;
        var destination = SafeMemberPath(outputDir, entry.Name);
        if (entry.IsDirectory) { Directory.CreateDirectory(destination); continue; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = File.Create(destination);
        if (this._reader.ExtractTo(entry, output) != entry.Size)
          throw new EndOfStreamException($"Reiser4: truncated source member '{entry.Name}'.");
      }
    }

    public Stream OpenEntry(Stream archive, string entryName, string? password) {
      if (!ReferenceEquals(archive, this._source))
        return ((IArchiveFormatOperations)this._owner).OpenEntry(archive, entryName, password);
      if (!this._entries.TryGetValue(entryName, out var entry) || entry.IsDirectory)
        throw new FileNotFoundException("Reiser4: source member was not found.", entryName);
      if (entry.Size == 0) return new MemoryStream([], writable: false);
      var spool = RebuildVerb.CreateScratchStream();
      try {
        if (this._reader.ExtractTo(entry, spool) != entry.Size)
          throw new EndOfStreamException($"Reiser4: truncated source member '{entryName}'.");
        spool.Position = 0;
        return spool;
      } catch { spool.Dispose(); throw; }
    }

    public void Dispose() => this._reader.Dispose();
  }

  private sealed record RebuildMetadata(string Label, string UuidHex, uint MkfsId, long Length,
      Reiser4Reader.FileMetadata? Root, Dictionary<string, Reiser4Reader.Entry> Entries);

  /// <summary>
  /// Everything a rebuild has to carry across, taken before anything is written. A tree the
  /// reader cannot walk completely, or fixed blocks the writer would not reproduce, are
  /// refused here, so the source is never touched.
  /// </summary>
  private static RebuildMetadata CaptureMetadata(Stream archive) {
    using var reader = new Reiser4Reader(archive);
    if (!reader.NativeTreeValid)
      throw new NotSupportedException("Reiser4: refusing to rebuild an incomplete or unsupported native tree.");
    var blocks = new Dictionary<int, byte[]>();
    byte[] Block(int number) {
      if (blocks.TryGetValue(number, out var cached)) return cached;
      var buffer = new byte[Reiser4Writer.BlockSize];
      archive.Position = (long)number * Reiser4Writer.BlockSize;
      archive.ReadExactly(buffer);
      return blocks[number] = buffer;
    }
    if (Reiser4Writer.ProfileMismatch(Block) is { } reason)
      throw new NotSupportedException($"Reiser4: refusing to rebuild: {reason}.");
    var mkfsId = BinaryPrimitives.ReadUInt32LittleEndian(Block(17).AsSpan(48, 4));
    archive.Position = 0;
    return new(reader.Label, reader.UuidHex, mkfsId, reader.Length, reader.RootMetadata,
      reader.Entries.ToDictionary(static entry => entry.Name, StringComparer.Ordinal));
  }

  private sealed class MetadataCreator(RebuildMetadata saved) : IArchiveCreatable {
    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      var directoryNames = inputs.Where(static input => input.IsDirectory)
        .Select(static input => input.ArchiveName.Replace('\\', '/').TrimEnd('/')).ToArray();
      uint DirectoryLinks(string name) => checked((name.Length == 0 ? 3U : 2U) + (uint)directoryNames.Count(path => {
        var slash = path.LastIndexOf('/');
        return (slash < 0 ? "" : path[..slash]) == name;
      }));
      var entries = saved.Entries.ToDictionary(static pair => pair.Key, pair => pair.Value.IsDirectory && pair.Value.Metadata is { } metadata
        ? pair.Value with { Metadata = metadata with { LinkCount = DirectoryLinks(pair.Key) } } : pair.Value, StringComparer.Ordinal);
      var effective = saved with {
        Entries = entries, Root = saved.Root is { } rootMetadata ? rootMetadata with { LinkCount = DirectoryLinks("") } : null,
      };
      CreateCore(output, inputs, options, effective);
      using var rebuilt = new Reiser4Reader(output);
      if (!rebuilt.NativeTreeValid || rebuilt.Label != saved.Label || rebuilt.UuidHex != saved.UuidHex)
        throw new InvalidDataException("Reiser4: rebuilt native tree or volume identity failed verification.");
      var expectedNames = inputs.Select(static input => input.ArchiveName.Replace('\\', '/').TrimEnd('/'))
        .Order(StringComparer.Ordinal).ToArray();
      if (!rebuilt.Entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal)
          .SequenceEqual(expectedNames, StringComparer.Ordinal))
        throw new InvalidDataException("Reiser4: rebuilt namespace failed verification.");
      if (effective.Root is { } root && !MetadataMatches(root, rebuilt.RootMetadata))
        throw new InvalidDataException("Reiser4: root metadata failed verification.");
      foreach (var entry in rebuilt.Entries) {
        if (effective.Entries.TryGetValue(entry.Name, out var original) && original.Metadata is { } before &&
            !MetadataMatches(before, entry.Metadata))
          throw new InvalidDataException($"Reiser4: metadata was lost for '{entry.Name}'.");
      }
    }
  }

  private static bool MetadataMatches(Reiser4Reader.FileMetadata expected, Reiser4Reader.FileMetadata? actual)
    => actual is not null && expected.ObjectId == actual.ObjectId && expected.StatLocality == actual.StatLocality &&
      expected.Mode == actual.Mode && expected.LinkCount == actual.LinkCount &&
      expected.UserId == actual.UserId && expected.GroupId == actual.GroupId &&
      expected.ModifiedTime == actual.ModifiedTime && expected.AccessedTime == actual.AccessedTime &&
      expected.ChangedTime == actual.ChangedTime && expected.RawStatData.Length == actual.RawStatData.Length &&
      expected.RawStatData.AsSpan(0, 2).SequenceEqual(actual.RawStatData.AsSpan(0, 2)) &&
      expected.RawStatData.AsSpan(44).SequenceEqual(actual.RawStatData.AsSpan(44));

  /// <summary>Updates a native object's Unix fields and stat-data extensions through a staged rebuild.</summary>
  /// <param name="archive">The readable, writable, seekable volume to update.</param>
  /// <param name="name">The member path, or an empty string for the root directory.</param>
  /// <param name="metadata">Unix fields and stat-data extension bytes to retain.</param>
  public void UpdateMetadata(Stream archive, string name, Reiser4Reader.FileMetadata metadata) {
    ArgumentNullException.ThrowIfNull(metadata);
    var saved = CaptureMetadata(archive);
    var normalized = name.Replace('\\', '/').TrimEnd('/');
    var original = normalized.Length == 0 ? saved.Root : saved.Entries.GetValueOrDefault(normalized)?.Metadata;
    if (original is null) throw new FileNotFoundException("Reiser4: native object was not found.", name);
    if ((metadata.Mode & 0xF000) != (original.Mode & 0xF000))
      throw new NotSupportedException("Reiser4: metadata updates cannot change an object's kind.");
    var updated = metadata with {
      Size = original.Size, ObjectId = original.ObjectId, StatLocality = original.StatLocality,
      RawStatData = metadata.RawStatData.ToArray(),
    };
    if (normalized.Length == 0) saved = saved with { Root = updated };
    else saved.Entries[normalized] = saved.Entries[normalized] with { Metadata = updated };
    using var source = new RebuildSource(this, archive);
    RebuildVerb.EditViaRebuild(archive, source, new MetadataCreator(saved), static _ => { });
  }

  /// <summary>Adds or replaces members while retaining existing stat-data extensions.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var saved = CaptureMetadata(archive);
    using var source = new RebuildSource(this, archive);
    RebuildVerb.EditViaRebuild(archive, source, new MetadataCreator(saved), directory => {
      foreach (var input in inputs) {
        var destination = SafeMemberPath(directory, input.ArchiveName);
        if (input.IsDirectory) { Directory.CreateDirectory(destination); continue; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (input.InMemoryContent is { } bytes) File.WriteAllBytes(destination, bytes);
        else File.Copy(input.FullPath, destination, overwrite: true);
      }
    });
  }

  /// <summary>Removes members and directory subtrees through a metadata-preserving rebuild.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var saved = CaptureMetadata(archive);
    using var source = new RebuildSource(this, archive);
    RebuildVerb.EditViaRebuild(archive, source, new MetadataCreator(saved), directory => {
      foreach (var name in entryNames) {
        var destination = SafeMemberPath(directory, name);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        else if (File.Exists(destination)) File.Delete(destination);
      }
    });
  }

  // ── IArchiveWriteConstraints ─────────────────────────────────────────
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = Reiser4Writer.RejectName(input.IsDirectory ? input.ArchiveName.TrimEnd('/', '\\') : input.ArchiveName);
    return reason is null;
  }
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
  public long? MinTotalArchiveSize => Reiser4Writer.BlockSize * (long)Reiser4Writer.MinBlockCount; // 16 MB
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "Reiser4 image; regular files and nested directories named by the native tree.";

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. Master SB is at 65536, format40 SB at 65536+blocksize
  // (4 KB typical). Cap at 96 KB so the format40 block at 69632..70112 always
  // fits even when blocksize is 4 KB.
  private const int HeaderReadCap = 96 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>
  /// Reports the volume's layout: the reserved blocks and the payload directory
  /// chain as metadata, then each file's blocks. A file's blocks are consecutive
  /// apart from the block-allocator bitmaps they step over.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Reiser4Reader(image);
      if (!reader.NativeTreeValid) return [];

      var blockSize = reader.BlockSize;
      // Everything before the first file's blocks is reserved or directory.
      var firstData = reader.Length;
      List<List<DefragBlockInfo>> files = [];
      foreach (var e in reader.Entries) {
        if (e.Size <= 0) continue;
        // A file is not one run: the allocator bitmaps inside it are stepped
        // over, so its bytes continue past where its length alone would end.
        List<DefragBlockInfo> runs = [];
        foreach (var (offset, length) in reader.EnumerateRuns(e)) {
          if (offset < firstData) firstData = offset;
          runs.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
        }
        if (runs.Count > 0) files.Add(runs);
      }

      var metadataEnd = files.Count > 0 ? firstData : Math.Min(reader.Length, 25L * blockSize);
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Reserved blocks and the payload directory"));

      foreach (var node in reader.NativeNodeBlocks)
        if ((long)node * blockSize >= metadataEnd)
          result.Add(new DefragBlockInfo((long)node * blockSize, blockSize,
            DefragBlockKind.MetadataReserved, "Native tree node"));

      // The bitmaps sit at stride boundaries inside the payload area.
      for (var block = Reiser4Writer.BlocksPerBitmap;
           (long)block * blockSize + blockSize <= reader.Length;
           block += Reiser4Writer.BlocksPerBitmap)
        result.Add(new DefragBlockInfo((long)block * blockSize, blockSize,
          DefragBlockKind.MetadataReserved, "Block-allocator bitmap"));

      // A map is read as a walk over the volume: each file keeps its own runs in
      // chain order, but the files themselves follow the bytes, not the
      // directory. The native tree lists names in key order, which is not where
      // their blocks are, and a map in that order makes the defrag planner shuffle
      // an already-consolidated volume.
      foreach (var runs in files.OrderBy(static runs => runs[0].Offset))
        result.AddRange(runs);
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>Zeros every byte no live file and no metadata block occupies.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
