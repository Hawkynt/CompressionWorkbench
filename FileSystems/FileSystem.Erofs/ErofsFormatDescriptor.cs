#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Erofs;

/// <summary>
/// Descriptor for EROFS images. Reading covers the uncompressed + inline inode layouts;
/// creation produces a minimal uncompressed (FLAT_PLAIN) image via <see cref="ErofsWriter"/>.
/// Full-fidelity, compressed images remain the job of <c>mkfs.erofs</c>; our writer targets
/// the round-trippable WORM subset (compact inodes, plain data, nested directories).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/erofs.html</c> — Linux kernel EROFS documentation (on-disk overview)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/erofs</c> — mainline implementation (<c>erofs_fs.h</c> defines the on-disk structures)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EROFS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ErofsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The one tunable the uncompressed writer honours: the volume label written
  /// into the superblock <c>volume_name</c> field (16 bytes) via
  /// <see cref="ErofsWriter.VolumeName"/> and read back as
  /// <c>ErofsReader.VolumeName</c>. The 4&#160;KB block size is fixed by the
  /// FLAT_PLAIN/FLAT_INLINE layout, so it is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Erofs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "EROFS";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".erofs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".erofs", ".img"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Magic sits at offset 1024 (start of superblock). Value is 0xE0F5E1E2 stored
    // little-endian, so the on-disk byte sequence is E2 E1 F5 E0.
    new([0xE2, 0xE1, 0xF5, 0xE0], Offset: 1024, Confidence: 0.95),
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
public string Description => "Android read-only compressed filesystem; uncompressed + inline inode layouts.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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
        Method: "stored",
        IsDirectory: e.IsDirectory,
        IsEncrypted: false,
        LastModified: null,
        IsSymlink: e.IsSymlink,
        LinkTarget: e.LinkTarget));
    }
    return SymlinkResolver.Resolve(result);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
        // Compressed-inode entry we can't decode yet; write an empty placeholder so
        // the user sees it exists but the content is unavailable.
        FormatHelpers.WriteFile(outputDir, e.Path + ".compressed-unsupported", []);
      }
    }
  }

  /// <summary>
  /// Opens a single EROFS file as a bounded read-only stream. The reader
  /// produces the decoded file bytes; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var reader = OpenReader(archive);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      byte[] bytes;
      try { bytes = reader.ExtractFile(e); }
      catch (NotSupportedException) { bytes = System.Array.Empty<byte>(); }
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
    var writer = new ErofsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      writer.VolumeName = label;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        writer.AddFile(info.ArchiveName, bytes);
      else
        writer.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                                () => File.OpenRead(info.FullPath));
    }
    writer.WriteTo(output);
  }

  private static ErofsReader OpenReader(Stream stream) {
    // Straight from the stream: copying the image into a byte[] capped the
    // reader at the array limit, which EROFS's block addresses do not.
    if (stream.CanSeek) return new ErofsReader(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new ErofsReader(ms.ToArray());
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the image out again. Moving what is out of place beats writing the
  /// image out anew: EROFS lays a file's blocks out contiguously from the raw
  /// block address in its inode, so a move is the copy plus four bytes. The
  /// default this replaces offered start-packing only, through a rebuild.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back: it can
    // refuse partway — a file laid out in a way the inode does not describe as
    // one run has nothing here to repoint — and a rebuild is the honest answer
    // when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadEntries(stream).ToList(),
        buildImage: files => {
          var writer = new ErofsWriter();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          var built = writer.Build();
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ErofsBlockMover();
    mover.Init(archive);

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

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    var reader = new ErofsReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory)
                         .Select(e => (e.Path, reader.ExtractFile(e))).ToList();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// The superblock, inode and directory region is structure; each file's full
  /// blocks are the run its inode addresses. A short file whose tail is stored
  /// inline with its inode has no run of its own, and needs none.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new ErofsReader(image);
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (!reader.TryGetDataExtent(e, out var offset, out var length)) continue;
        result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Path));
        first = Math.Min(first, offset);
      }
      if (first == long.MaxValue) first = image.Length;
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the wipe unused space operation.
  /// </summary>
public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // A file's last block is shared with nothing, but its slack belongs to the
    // file's own run; trimming it would need the size of the run, not the file.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
