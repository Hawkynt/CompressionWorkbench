#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Erofs;

/// <summary>
/// Offline R/W descriptor for EROFS images. Reading covers the uncompressed
/// FLAT_PLAIN/FLAT_INLINE inode layouts; creation emits the same conservative,
/// round-trippable subset through <see cref="ErofsWriter"/>. Linux mounts EROFS
/// read-only by design, but an existing supported-profile image can be edited by
/// verified rebuild. Compressed inode layouts remain readable as metadata only
/// until their data decoder/writer is implemented and are therefore rejected by
/// mutation rather than silently rewritten as placeholders.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/erofs.html</c> — Linux kernel EROFS documentation</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/erofs</c> — mainline implementation (<c>erofs_fs.h</c>)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EROFS</c> — overview</description></item>
/// </list>
/// </summary>
public sealed class ErofsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable,
  IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

  public string Id => "Erofs";
  public string DisplayName => "EROFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".erofs";
  public IReadOnlyList<string> Extensions => [".erofs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xE2, 0xE1, 0xF5, 0xE0], Offset: 1024, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored / flat inode")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Android/Linux read-only-on-mount filesystem; FLAT_PLAIN/FLAT_INLINE profile supports offline R/W and maintenance.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = OpenReader(stream);
    var result = new List<ArchiveEntryInfo>(reader.Entries.Count);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var e = reader.Entries[i];
      result.Add(new ArchiveEntryInfo(
        Index: i,
        Name: e.Path,
        OriginalSize: e.Size,
        CompressedSize: e.Size,
        Method: "stored/erofs",
        IsDirectory: e.IsDirectory,
        IsEncrypted: false,
        LastModified: null,
        IsSymlink: e.IsSymlink,
        LinkTarget: e.LinkTarget));
    }
    return SymlinkResolver.Resolve(result);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = OpenReader(stream);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Path, files))
        continue;
      try {
        var data = reader.ExtractFile(e);
        FormatHelpers.WriteFile(outputDir, e.Path, data);
      } catch (NotSupportedException) {
        // Browsing remains useful for unsupported compressed profiles. Mutation
        // never uses this lossy path; it goes through ReadWritableEntries below.
        FormatHelpers.WriteFile(outputDir, e.Path + ".compressed-unsupported", []);
      }
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var reader = OpenReader(archive);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = reader.ExtractFile(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    s.CopyTo(memory);
    return memory.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    BuildWriter(inputs, label).WriteTo(output);
  }

  /// <summary>
  /// Existing-image edit for the writer-compatible EROFS subset. Every live file
  /// is decoded before the rebuild starts; compressed layouts and symlinks are
  /// rejected up front so no placeholder or type-changing rewrite can occur.
  /// The volume label survives the rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var label = ReadVolumeName(archive);
    archive.Position = 0;
    ModifyRebuilder.Add(archive, inputs,
      readEntries: ReadWritableEntries,
      buildImage: files => BuildImage(files, label));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var label = ReadVolumeName(archive);
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: ReadWritableEntries,
      buildImage: files => BuildImage(files, label));
  }

  private static ErofsReader OpenReader(Stream stream) {
    if (stream.CanSeek) return new ErofsReader(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new ErofsReader(ms.ToArray());
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    var label = ReadVolumeName(archive);
    archive.Position = 0;
    // Validate the whole writable profile before any in-place attempt. In
    // particular this refuses compressed inode layouts rather than letting a
    // fallback rebuild discover them after bytes have moved.
    _ = ReadWritableEntries(archive).ToList();
    archive.Position = 0;

    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadWritableEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadWritableEntries(stream).ToList(),
        buildImage: files => {
          var built = BuildImage(files, label);
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ErofsBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing EROFS flat-layout extents"));

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
      "complete", 1, -1, -1, archive.Length, postExtents, "EROFS defragmentation complete"));
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadWritableEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var reader = new ErofsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (entry.IsSymlink)
        throw new NotSupportedException(
          $"EROFS mutation refuses symlink '{entry.Path}' until the writer can preserve symlink inode semantics.");
      byte[] data;
      try {
        data = reader.ExtractFile(entry);
      } catch (NotSupportedException ex) {
        throw new NotSupportedException(
          $"EROFS mutation requires FLAT_PLAIN/FLAT_INLINE data; '{entry.Path}' uses an unsupported compressed layout.", ex);
      }
      yield return (entry.Path, data);
    }
  }

  private static string ReadVolumeName(Stream archive) {
    if (archive.CanSeek) archive.Position = 0;
    return new ErofsReader(archive).VolumeName;
  }

  private static ErofsWriter BuildWriter(IReadOnlyList<ArchiveInputInfo> inputs, string label) {
    var writer = new ErofsWriter { VolumeName = label };
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (input.InMemoryContent is { } bytes)
        writer.AddFile(input.ArchiveName, bytes);
      else
        writer.AddStreamingFile(input.ArchiveName, new FileInfo(input.FullPath).Length,
          () => File.OpenRead(input.FullPath));
    }
    return writer;
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, string label) {
    var writer = new ErofsWriter { VolumeName = label };
    foreach (var (name, data) in files) writer.AddFile(name, data);
    return writer.Build();
  }

  /// <summary>
  /// Conservative physical extent map. Flat file runs are marked Used. Every
  /// byte not proven to belong to one of those runs is structural/reserved —
  /// EROFS has no allocator bitmap from which this implementation can prove an
  /// arbitrary hole is reusable. This deliberately sacrifices generic free-gap
  /// wiping for correctness on externally-produced images.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new ErofsReader(image);
      var owned = new List<(long Start, long End, string Name)>();
      foreach (var entry in reader.Entries) {
        if (!reader.TryGetDataExtent(entry, out var offset, out var length)) continue;
        if (offset < 0 || length <= 0 || offset + length > image.Length) continue;
        owned.Add((offset, offset + length, entry.Path));
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      var cursor = 0L;
      foreach (var run in owned) {
        if (run.Start > cursor)
          result.Add(new DefragBlockInfo(cursor, run.Start - cursor,
            DefragBlockKind.MetadataReserved, "$EROFS/metadata-or-unproven"));
        result.Add(new DefragBlockInfo(run.Start, run.End - run.Start,
          DefragBlockKind.Used, run.Name));
        cursor = Math.Max(cursor, run.End);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor,
          DefragBlockKind.MetadataReserved, "$EROFS/metadata-or-unproven"));
      if (result.Count == 0 && image.Length > 0)
        result.Add(new DefragBlockInfo(0, image.Length, DefragBlockKind.MetadataReserved,
          "$EROFS/unproven"));
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>
  /// No byte is considered wipeable without allocator evidence. The extent map
  /// intentionally reserves every unproven region, so this currently returns 0
  /// for valid EROFS images rather than risking metadata damage.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    return 0;
  }
}
