#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DragonFs;

/// <summary>
/// Read-only descriptor for DragonFS — the embedded read-only filesystem
/// used by Libdragon (open Nintendo 64 SDK) to bundle assets inside an
/// N64 ROM image. DragonFS is big-endian throughout, uses 32-byte
/// directory records starting at file offset 256 (Libdragon
/// DFS_ROOT_OFFSET), and lacks an unambiguous fixed magic in original
/// images — detection is by .dfs extension plus an optional "DragonFS"
/// ASCII tag at offset 0 for self-produced research images.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonMinded/libdragon</c> — Libdragon source, the origin of DragonFS (<c>dragonfs.c</c> / <c>mkdfs</c> define the format)</description></item>
///   <item><description><c>https://libdragon.dev</c> — official Libdragon documentation site</description></item>
/// </list>
/// </summary>
public sealed class DragonFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "DragonFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DragonFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".dfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".dfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Optional 8-byte "DragonFS" ASCII tag at offset 0 — only present in
    // images that opt into the explicit tag; canonical Libdragon DFS images
    // start straight with binary directory entries at offset 256.
    new("DragonFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
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
public string Description => "DragonFS embedded read-only filesystem (Libdragon / Nintendo 64).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DragonFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DragonFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Produces a fresh DragonFS image from scratch holding <paramref name="inputs"/>.
  /// DragonFS is a flat filesystem, so subdirectory paths are flattened to their
  /// leaf names via <see cref="DragonFsWriter.AddFile"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new DragonFsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing DragonFS image using
  /// <see cref="DragonFsModifier"/>. The modifier appends new records + data at
  /// the image tail and relinks the singly-linked chain, so existing files'
  /// data bytes stay byte-identical at their original offsets — a genuine
  /// in-place mutation (the image grows only at the end).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      DragonFsModifier.RemoveFile(archive, name, wipeData: true);
      DragonFsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes the named entries in place by blanking their directory
  /// records (the chain stays intact; the reader skips blank records).</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      DragonFsModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the volume out again. A file here is its directory record followed by
  /// its bytes — the record is what gives the bytes their address — so the pair
  /// moves together and what is rewritten is the pointer that reached it.
  /// </summary>
  /// <remarks>
  /// This used to refuse outright on the grounds that the volume was read-only
  /// and had no writer. It has had both a writer and an in-place modifier for
  /// some time; what it did not have was a way to say where anything is, which
  /// <see cref="DragonFsExtentMap" /> now does.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back: it can
    // refuse partway, and a rebuild is the honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadEntries(stream),
        buildImage: files => {
          var writer = new DragonFsWriter();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          using var built = new MemoryStream();
          writer.WriteTo(built);
          var bytes = built.ToArray();
          if (bytes.Length >= archive.Length) return bytes;
          var padded = new byte[archive.Length];
          Array.Copy(bytes, padded, bytes.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new DragonFsBlockMover();
    mover.Init(archive);

    var extents = DragonFsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = DragonFsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new DragonFsReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory)
                         .Select(e => (e.Name, reader.Extract(e))).ToList();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => DragonFsExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every byte no record and no file claims — which is where a
  /// removed file's bytes stay until something else takes them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = DragonFsExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;
    image.Position = 0;
    // A file's extent is its record plus exactly its bytes, so there is no
    // slack inside it for a tip wipe to trim.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }
}
