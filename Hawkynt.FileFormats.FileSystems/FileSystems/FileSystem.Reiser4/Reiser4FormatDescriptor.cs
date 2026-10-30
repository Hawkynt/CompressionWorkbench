#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Reiser4;

/// <summary>
/// Descriptor for Reiser4 filesystem images (successor to ReiserFS 3.6 — completely
/// different on-disk layout). Volumes whose node40/node41 tree the reader walks completely list
/// and extract their files, directories, links and special files — after replaying a pending
/// journal — and are created and edited through staged rebuilds that carry the volume's identity,
/// plugin profile and every object's keys, metadata and body across; other images surface the
/// master superblock at offset 65536, the format40 superblock that follows it, a structured
/// metadata bundle and the raw image.
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
  /// Knobs the writer honours. <c>VolumeLabel</c> is written into the master superblock label
  /// field (and the backup record); <c>ImageSize</c> drives <see cref="Reiser4Writer.BlockCount"/>
  /// (4&#160;KB blocks, clamped to the writer minimum); <c>FileBody</c> picks how regular files are
  /// stored (<see cref="Reiser4FileBody" />). The 4&#160;KB block size is fixed — the embedded
  /// mkfs.reiser4 templates are byte-exact 4096-byte captures — so it is intentionally not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    FilesystemSchemaPresets.ImageSize(["16 MB", "32 MB", "64 MB", "128 MB"]),
    new FormatOptionDescriptor("FileBody", "File body", FormatOptionKind.Enum, nameof(Reiser4FileBody.Extents),
      AllowedValues: Enum.GetNames<Reiser4FileBody>(),
      Description: "How regular files are stored: extents, tails, the kernel's smart policy (tails up to four blocks), " +
        "compressed cryptcompress clusters, or whatever the volume's plugin profile says.",
      IsOptimizationAxis: false),
  ];

  public string Id => "Reiser4";
  public string DisplayName => "Reiser4";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".reiser4";
  public IReadOnlyList<string> Extensions => [".reiser4"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "ReIsEr4" at byte offset 65536 (= 16 * 4096). Confidence 0.9: the 7-byte
    // magic is highly unlikely to land at exactly this offset by chance, but
    // it shares the "ReIsEr" prefix with the older ReiserFS 3.6 magic
    // ("ReIsErFs", "ReIsEr2Fs", "ReIsEr3Fs") which lives at offset 65536+52.
    new("ReIsEr4"u8.ToArray(), Offset: (int)Reiser4MasterSb.MasterOffset, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Reiser4 filesystem image — every node40/node41 plugin profile, journal replay, " +
    "extents, tails, compressed clusters, symlinks, special files and hard links, edited through metadata-preserving rebuilds.";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
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
    var idx = 0;
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new Reiser4Reader(stream);
      if (reader.NativeTreeValid || reader.Entries.Count > 0) {
        foreach (var e in reader.Entries)
          entries.Add(ToInfo(idx++, e));
        return entries;
      }
    } catch {
      // fall through to the carver view
    }

    entries.Add(new ArchiveEntryInfo(idx++, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "master_superblock.bin", sb.MasterRaw.LongLength, sb.MasterRaw.LongLength, "stored", false, false, null));
    if (sb.Format40Present)
      entries.Add(new ArchiveEntryInfo(idx++, "format40_superblock.bin", sb.Format40Raw.LongLength, sb.Format40Raw.LongLength, "stored", false, false, null));
    return entries;
  }

  private static ArchiveEntryInfo ToInfo(int index, Reiser4Reader.Entry e) => e.Kind switch {
    Reiser4Reader.EntryKind.Symlink => new ArchiveEntryInfo(index, e.Name, 0, 0, "stored", false, false,
      e.Metadata?.LastModified, IsSymlink: true, LinkTarget: e.LinkTarget),
    Reiser4Reader.EntryKind.CharacterDevice or Reiser4Reader.EntryKind.BlockDevice or Reiser4Reader.EntryKind.Fifo or Reiser4Reader.EntryKind.Socket =>
      new ArchiveEntryInfo(index, e.Name, 0, 0, "stored", false, false, e.Metadata?.LastModified, Kind: e.Kind.ToString()),
    _ => new ArchiveEntryInfo(index, e.Name, e.Size, e.Size, e.Body is "ctail" ? "compressed" : "stored", e.IsDirectory, false,
      e.Metadata?.LastModified),
  };

  /// <summary>Decodes the supplied input.</summary>
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

    if (stream.CanSeek) stream.Position = 0;
    using (var reader = new Reiser4Reader(stream)) {
      if (reader.NativeTreeValid || reader.Entries.Count > 0) {
        foreach (var e in reader.Entries) {
          if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
          var target = SafeMemberPath(outputDir, e.Name);
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
          // Links and special files have no bytes to extract; List names them and their targets.
          if (e.Kind != Reiser4Reader.EntryKind.File) continue;
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

  /// <summary>Creates a volume holding <paramref name="inputs" />.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var w = new Reiser4Writer();
    ApplyCreateOptions(w, options);

    var sizes = new List<long>();
    if (inputs != null)
      foreach (var i in inputs) {
        var name = i.ArchiveName.Replace('\\', '/').TrimEnd('/');
        if (i.IsDirectory) { w.AddDirectory(name, ToFileMetadata(i.Metadata)); continue; }
        var length = i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
        sizes.Add(length);
        if (i.InMemoryContent is { } bytes) {
          w.AddFile(i.ArchiveName, bytes, ToFileMetadata(i.Metadata));
          continue;
        }
        var path = i.FullPath;
        w.AddStreamingFile(i.ArchiveName, length, () => File.OpenRead(path), ToFileMetadata(i.Metadata));
      }

    var sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    var requested = sizeBytes > 0 ? (ulong)Math.Max(1, sizeBytes / Reiser4Writer.BlockSize) : 0UL;
    w.BlockCount = Math.Max(requested, Reiser4Writer.EstimateBlockCount(sizes));
    w.Write(output);
  }

  private static void ApplyCreateOptions(Reiser4Writer writer, FormatCreateOptions? options) {
    // Volume label: prefer the schema knob, falling back to the legacy
    // password-slot mapping (kept for callers that pre-date the schema).
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(options?.Password))
      label = options.Password;
    if (!string.IsNullOrEmpty(label)) writer.Label = label;
    writer.FileBody = ParseFileBody(options?.GetOption("FileBody", ""));
  }

  private static Reiser4FileBody ParseFileBody(string? value) {
    if (string.IsNullOrWhiteSpace(value)) return Reiser4FileBody.Extents;
    return Enum.TryParse<Reiser4FileBody>(value, ignoreCase: true, out var body)
      ? body
      : throw new NotSupportedException($"Reiser4: unknown FileBody '{value}'.");
  }

  /// <summary>Metadata an archive input carries, in the shape the writer applies to a new object.</summary>
  private static Reiser4Reader.FileMetadata? ToFileMetadata(ArchiveEntryMetadata? metadata) {
    if (metadata is null) return null;
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static uint Seconds(DateTimeOffset? time, uint fallback) => time is { } t && t.ToUnixTimeSeconds() is >= 0 and <= uint.MaxValue ? (uint)t.ToUnixTimeSeconds() : fallback;
    var modified = Seconds(metadata.LastWriteTimeUtc, now);
    return new Reiser4Reader.FileMetadata(0, (ushort)((metadata.UnixMode ?? 0x1A4) & 0xFFF), 1,
      metadata.UnixUserId ?? 0, metadata.UnixGroupId ?? 0, modified, Seconds(metadata.LastAccessTimeUtc, modified),
      Seconds(metadata.StatusChangeTimeUtc, modified), []);
  }

  /// <summary>
  /// Creates the volume from input streams. Each source is consumed once into a temporary file,
  /// then copied in bounded chunks by <see cref="Reiser4Writer"/>; no complete file is staged in
  /// memory. Directory inputs retain empty directories and nested paths.
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

      ApplyCreateOptions(writer, options);
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

  // ── Rebuilds ─────────────────────────────────────────────────────────

  /// <summary>
  /// Reads the volume into a plan, applies <paramref name="edit" />, writes the result to scratch,
  /// reads it back and verifies it, and only then replaces <paramref name="archive" />. A volume
  /// outside the profile — or an edit the profile cannot represent — is refused before anything
  /// is written.
  /// </summary>
  private static void EditStaged(Stream archive, Action<Reiser4Plan> edit, Func<Reiser4Plan, ulong>? minimumBlocks = null) {
    ArgumentNullException.ThrowIfNull(archive);
    using var staged = RebuildVerb.CreateScratchStream();
    StageTo(archive, staged, edit, minimumBlocks);
    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.Flush();
  }

  /// <summary>Writes the edited plan of <paramref name="source" /> to <paramref name="target" /> and verifies it there.</summary>
  private static void StageTo(Stream source, Stream target, Action<Reiser4Plan> edit, Func<Reiser4Plan, ulong>? minimumBlocks) {
    source.Position = 0;
    using var volume = new Reiser4Volume(source);
    var plan = Reiser4Plan.FromVolume(volume);
    edit(plan);
    if (minimumBlocks is not null) plan.MinimumBlocks = minimumBlocks(plan);
    target.Position = 0;
    target.SetLength(0);
    plan.Write(target);
    Verify(plan, target, volume);
  }

  /// <summary>
  /// The staged volume must read back whole, keep the volume's identity, name exactly what the
  /// plan names, and hold every object's stat data as the plan wrote it.
  /// </summary>
  private static void Verify(Reiser4Plan plan, Stream staged, Reiser4Volume source) {
    staged.Position = 0;
    using var rebuilt = new Reiser4Volume(staged);
    if (!rebuilt.TreeValid)
      throw new InvalidDataException($"Reiser4: the rebuilt tree failed verification: {rebuilt.Problem}.");
    if (rebuilt.UuidHex != source.UuidHex || rebuilt.Label != source.Label)
      throw new InvalidDataException("Reiser4: the rebuilt volume lost its identity.");
    var expected = plan.Paths.ToDictionary(static p => p.Key, static p => p.Value.Entry.Target, StringComparer.Ordinal);
    if (rebuilt.Paths.Count != expected.Count)
      throw new InvalidDataException("Reiser4: the rebuilt namespace failed verification.");
    var objects = plan.Objects.ToDictionary(static o => o.StatKey);
    foreach (var (path, _, obj, _) in rebuilt.Paths) {
      if (!expected.TryGetValue(path, out var key) || key != obj.StatKey || !objects.TryGetValue(key, out var planned) ||
          !planned.Stat.AsSpan().SequenceEqual(obj.Stat.Raw))
        throw new InvalidDataException($"Reiser4: '{path}' failed verification after the rebuild.");
    }
  }

  /// <summary>Performs the defragment operation.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Largest volume the in-place pass is offered for. Its guard holds a copy of the image to compare payloads across the pass.</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new Reiser4Reader(stream, leaveOpen: true);
    return reader.Entries.Where(e => e.Kind == Reiser4Reader.EntryKind.File && e.Size > 0).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing every extent at the end.</summary>
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

    // The tree is written once every run has landed: an extent unit is one field, and what it
    // means depends on where all of a file's blocks are.
    mover.SettleDirectory(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Defragments the volume: in place when the planner's moves can be written back as extent
  /// repoints (any tree height), otherwise by a staged rebuild that lays every file out
  /// contiguously at the same volume size.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    archive.Position = 0;
    Dictionary<string, byte[]> before;
    bool inPlace;
    using (var volume = new Reiser4Volume(archive)) {
      if (!volume.TreeValid) throw new NotSupportedException($"Reiser4: refusing to defragment: {volume.Problem}.");
      _ = Reiser4Plan.FromVolume(volume);
      before = volume.Objects.ToDictionary(static pair => pair.Key.ToString(), static pair => pair.Value.Stat.Raw);
      inPlace = volume.ReplayedTransactions == 0 && archive.CanSeek && archive.Length <= MaxBufferedImageBytes;
    }
    if (inPlace) {
      var planned = false;
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => {
          this.DefragmentWithPlanner(archive, options);
          archive.Position = 0;
          using var moved = new Reiser4Volume(archive);
          if (!moved.TreeValid || moved.Objects.Count != before.Count ||
              moved.Objects.Any(pair => !before.TryGetValue(pair.Key.ToString(), out var raw) || !raw.AsSpan().SequenceEqual(pair.Value.Stat.Raw)))
            throw new InvalidDataException("Reiser4: in-place defrag changed native metadata.");
          planned = true;
        },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    if (options.Mode is not (DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy))
      throw new NotSupportedException(
        $"Reiser4 defragmentation supports ConsolidateAtStart and FillHolesLazy; got {options.Mode}.");

    options.OnProgress?.Invoke(new DefragProgressEvent("rebuilding", 0.1, -1, -1, archive.Length, null, "Rebuilding the volume"));
    EditStaged(archive, static _ => { });
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, null, "Rebuild committed successfully"));
  }

  /// <summary>Rebuilds tightly while retaining native metadata, or copies through when the volume cannot be rebuilt smaller.</summary>
  public void Shrink(Stream input, Stream output) {
    using var staged = RebuildVerb.CreateScratchStream();
    var useStaged = false;
    try {
      StageTo(input, staged, static _ => { }, static _ => Reiser4Writer.MinBlockCount);
      useStaged = staged.Length < input.Length;
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) {
      // the original is retained when the writer cannot represent it
    }
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    if (useStaged) { staged.Position = 0; staged.CopyTo(output); }
    else input.CopyTo(output);
  }

  /// <summary>Rebuilds the volume while carrying existing metadata, at the requested image size or the smallest that holds it.</summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.UnitSize is not (0 or Reiser4Writer.BlockSize) || options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("Reiser4: this writer uses 4096-byte blocks without sparse or shared-file transforms.");
    var imageSize = options.ImageSize;
    if (options.Parameters is not null)
      foreach (var pair in options.Parameters) {
        if (!pair.Key.Equals("ImageSize", StringComparison.OrdinalIgnoreCase))
          throw new NotSupportedException($"Reiser4: unsupported rebuild parameter '{pair.Key}'.");
        imageSize = FilesystemSchemaPresets.ParseSize(pair.Value);
      }
    var blocks = imageSize > 0 ? (ulong)(imageSize / Reiser4Writer.BlockSize) : Reiser4Writer.MinBlockCount;
    StageTo(source, target, static _ => { }, _ => Math.Max(Reiser4Writer.MinBlockCount, blocks));
  }

  private static string SafeMemberPath(string root, string name) {
    var components = name.Replace('\\', '/').TrimEnd('/').Split('/');
    if (components.Length == 0 || components.Any(static component => component.Length == 0 ||
        component is "." or ".." || component.Contains(':') || component.Contains('\0')))
      throw new InvalidDataException($"Reiser4: invalid member path '{name}'.");
    return Path.Combine(root, Path.Combine(components));
  }

  /// <summary>Updates a native object's mode, owner and timestamps through a staged rebuild.</summary>
  /// <param name="archive">The readable, writable, seekable volume to update.</param>
  /// <param name="name">The member path, or an empty string for the root directory.</param>
  /// <param name="metadata">The object's metadata with new mode, UID, GID or timestamps; its link
  /// count and stat-data extension bytes must be the ones the volume holds.</param>
  /// <exception cref="NotSupportedException">The update changes the object's kind, its link count or
  /// its extension bytes, or the volume is outside the supported profile; the volume is unchanged.</exception>
  public void UpdateMetadata(Stream archive, string name, Reiser4Reader.FileMetadata metadata) {
    ArgumentNullException.ThrowIfNull(metadata);
    EditStaged(archive, plan => {
      var target = plan.Find(name) ?? throw new FileNotFoundException("Reiser4: native object was not found.", name);
      var original = Reiser4Stat.Parse(target.Stat, out _)!;
      if ((metadata.Mode & 0xF000) != (original.Mode & 0xF000))
        throw new NotSupportedException("Reiser4: metadata updates cannot change an object's kind.");
      // The link count follows the namespace: a file has one name per link, a directory one more
      // than its sub-directories. Any other value is what fsck.reiser4 reports as wrong.
      if (metadata.LinkCount != original.Links)
        throw new NotSupportedException("Reiser4: the link count follows the namespace and cannot be set.");
      // Extension bytes past the Unix fields are carried as found because their meaning is not
      // verified here; accepting new ones would write a stat data nothing has checked.
      if (metadata.RawStatData.Length != original.Raw.Length ||
          !metadata.RawStatData.AsSpan(0, 2).SequenceEqual(original.Raw.AsSpan(0, 2)) ||
          !metadata.RawStatData.AsSpan(Math.Min(44, metadata.RawStatData.Length)).SequenceEqual(original.Raw.AsSpan(Math.Min(44, original.Raw.Length))))
        throw new NotSupportedException("Reiser4: stat-data extensions other than the Unix fields cannot be changed.");
      plan.ApplyMetadata(target, metadata);
    });
  }

  /// <summary>Adds or replaces members while retaining every existing object; new files are stored as extents.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => this.Add(archive, inputs, Reiser4FileBody.Extents);

  /// <summary>Adds or replaces members, storing new regular files as <paramref name="body" /> says.</summary>
  /// <remarks>A replaced file keeps its identity, metadata and plugin: a cryptcompress file stays one.</remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs, Reiser4FileBody body) {
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs)
      if (!this.CanAccept(input, out var reason)) throw new NotSupportedException("Reiser4: " + reason + ".");
    EditStaged(archive, plan => {
      plan.FileBody = body;
      foreach (var input in inputs) {
        var name = input.ArchiveName.Replace('\\', '/').TrimEnd('/');
        if (input.IsDirectory) { plan.AddDirectory(name, ToFileMetadata(input.Metadata)); continue; }
        var payload = input.InMemoryContent is { } bytes
          ? FilePayload.FromBytes(bytes)
          : FilePayload.FromStream(new FileInfo(input.FullPath).Length, () => File.OpenRead(input.FullPath));
        plan.AddFile(name, payload, ToFileMetadata(input.Metadata));
      }
    });
  }

  /// <summary>Adds a symbolic link at <paramref name="path" /> pointing at <paramref name="target" />.</summary>
  public void AddSymlink(Stream archive, string path, string target)
    => EditStaged(archive, plan => plan.AddSymlink(path, target));

  /// <summary>Adds <paramref name="path" /> as a further name of the existing file <paramref name="existing" />.</summary>
  public void AddHardLink(Stream archive, string path, string existing)
    => EditStaged(archive, plan => plan.AddHardLink(path, existing));

  /// <summary>Adds a device node (<paramref name="device" /> = major &lt;&lt; 20 | minor), FIFO or socket.</summary>
  public void AddSpecialFile(Stream archive, string path, ushort mode, ulong device = 0)
    => EditStaged(archive, plan => plan.AddSpecial(path, mode, device));

  /// <summary>Removes members and directory subtrees through a metadata-preserving rebuild.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    EditStaged(archive, plan => {
      foreach (var name in entryNames) plan.Remove(name);
    });
  }

  // ── IArchiveWriteConstraints ─────────────────────────────────────────
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = Reiser4Writer.RejectName(input.IsDirectory ? input.ArchiveName.TrimEnd('/', '\\') : input.ArchiveName);
    return reason is null;
  }
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => Reiser4Writer.BlockSize * (long)Reiser4Writer.MinBlockCount; // 16 MB
  public string AcceptedInputsDescription =>
    "Reiser4 image; regular files and nested directories named by the native tree.";

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. Master SB is at 65536, format40 SB at 65536+blocksize.
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
  /// Reports the volume's layout: the reserved blocks, the tree nodes and the allocator bitmaps as
  /// metadata, then each file's extent runs. A volume whose journal still holds transactions to
  /// replay reports nothing: its blocks are not yet where the tree says.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Reiser4Reader(image);
      if (!reader.NativeTreeValid || reader.ReplayedTransactions > 0) return [];

      var blockSize = reader.BlockSize;
      var firstData = reader.Length;
      List<List<DefragBlockInfo>> files = [];
      var seen = new HashSet<Reiser4Key>();
      foreach (var e in reader.Entries) {
        if (e.Kind != Reiser4Reader.EntryKind.File || e.Size <= 0 || e.Native is null || !seen.Add(e.Native.StatKey)) continue;
        List<DefragBlockInfo> runs = [];
        foreach (var (offset, length) in reader.EnumerateRuns(e)) {
          if (offset < firstData) firstData = offset;
          runs.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
        }
        if (runs.Count > 0) files.Add(runs);
      }

      var metadataEnd = Math.Min(reader.Length, 25L * blockSize);
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Reserved blocks"));

      foreach (var node in reader.NativeNodeBlocks)
        if ((long)node * blockSize >= metadataEnd)
          result.Add(new DefragBlockInfo((long)node * blockSize, blockSize,
            DefragBlockKind.MetadataReserved, "Native tree node"));

      for (var block = Reiser4Writer.BlocksPerBitmap;
           (long)block * blockSize + blockSize <= reader.Length;
           block += Reiser4Writer.BlocksPerBitmap)
        result.Add(new DefragBlockInfo((long)block * blockSize, blockSize,
          DefragBlockKind.MetadataReserved, "Block-allocator bitmap"));

      // A map is read as a walk over the volume: each file keeps its own runs in
      // logical order, but the files themselves follow the bytes, not the tree.
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
