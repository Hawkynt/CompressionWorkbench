#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SquashFs;

/// <summary>
/// Offline R/W descriptor for SquashFS images ("hsqs" magic). Linux mounts
/// SquashFS read-only by design; the workbench nevertheless supports editing an
/// existing image by verified rebuild, plus guarded physical re-layout where
/// compressed metadata can be repointed safely. The writer emits gzip-compressed
/// images.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://dr-emann.github.io/squashfs/</c> — community-written binary-format specification</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/squashfs.html</c> — kernel documentation</description></item>
///   <item><description><c>https://github.com/plougher/squashfs-tools</c> — canonical mksquashfs/unsquashfs tooling</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/SquashFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class SquashFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The only writer-honoured knob is the data block size: it is split into the
  /// superblock's <c>block_size</c> / <c>block_log</c> fields and drives how each
  /// file's payload is chunked into compressed data blocks. SquashFS stores no
  /// volume label, and this writer always compresses with gzip (zlib), so no label
  /// or compression-method knob is published.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "BlockSize", displayName: "Data block size",
      min: 4096, max: 1048576, defaultLabel: "128 KB",
      description: "Compressed data block size. SquashFS allows powers of two from 4 KB to 1 MB; larger blocks compress better but waste more on small files."),
  ];

  public string Id => "SquashFs";
  public string DisplayName => "SquashFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W describes the supported existing-image edit API. It does not imply that
  // the Linux kernel can mount this filesystem writable or that every edit is
  // byte-local: Add/Remove may perform a complete verified re-layout.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".sqfs";
  public IReadOnlyList<string> Extensions => [".sqfs", ".squashfs", ".snap", ".appimage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'h', (byte)'s', (byte)'q', (byte)'s'], Confidence: 0.95),
    new([(byte)'s', (byte)'q', (byte)'s', (byte)'h'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squashfs", "SquashFS", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux compressed read-only-on-mount filesystem with offline R/W and layout optimization";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SquashFsReader(stream);
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size, -1,
      "squashfs", e.IsDirectory, false, e.ModifiedTime,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.SymlinkTarget)).ToList();
    return SymlinkResolver.Resolve(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SquashFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new SquashFsReader(archive);
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

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var blockSize = ResolveBlockSize(options);
    using var w = new SquashFsWriter(output, leaveOpen: true, blockSize: blockSize);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName.TrimEnd('/'));
      } else {
        var data = input.ReadContent();
        w.AddFile(input.ArchiveName, data);
      }
    }
  }

  private static uint ResolveBlockSize(FormatCreateOptions? options) {
    var parsed = FilesystemSchemaPresets.ParseSize(options?.GetOption("BlockSize", "Auto"));
    return parsed > 0 ? (uint)parsed : SquashFsWriter.DefaultBlockSize;
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (archive.CanSeek && archive.Length <= PlannerImageCap) {
      var planned = false;
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    this.DefragmentWithRebuild(archive, options);
  }

  private const long PlannerImageCap = 256L * 1024 * 1024;

  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new SquashFsReader(stream, leaveOpen: true);
    return reader.Entries
      .Where(e => !e.IsDirectory && !e.IsSymlink)
      .OrderBy(e => e.FullPath, StringComparer.Ordinal)
      .Select(reader.Extract)
      .ToList();
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new SquashFsBlockMover();
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

    mover.SettleInodeTable(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SquashFsReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SquashFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  /// <summary>
  /// A canonical SquashFS image is fully packed. The real extent map marks all
  /// non-file bytes as metadata-reserved, so there is no proven dead space to
  /// scrub and this format-specific implementation is intentionally a no-op.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    return 0;
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new SquashFsReader(image, leaveOpen: true);

      var owned = new List<(long Start, long End)>();
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.IsSymlink) continue;
        var (offset, length) = entry.DataExtent;
        if (length <= 0 || offset < 0 || offset + length > image.Length) continue;
        result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, entry.FullPath));
        owned.Add((offset, offset + length));
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      // SquashFS is packed: everything not owned by a file data extent belongs
      // to the superblock, fragment store, inode/directory tables or indexes.
      var cursor = 0L;
      foreach (var (start, end) in owned) {
        if (start > cursor)
          result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.MetadataReserved,
            cursor == 0 ? "superblock" : "fragments and tables"));
        cursor = Math.Max(cursor, end);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor,
          DefragBlockKind.MetadataReserved, "fragments and tables"));
    } catch {
      // Fail closed: an image we cannot walk claims no free space.
      return [];
    }
    return result;
  }
}
