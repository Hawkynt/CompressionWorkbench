#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Sfs;

/// <summary>
/// Descriptor for Amiga Smart Filesystem (SFS) volume images. SFS is the
/// OFS/FFS replacement used by AmigaOS 4 and AROS, with the complete spec at
/// http://www.xs4all.nl/~hjohn/SFS/ (Amiga SFS spec). Surfaces the parsed root
/// block as a structured metadata bundle alongside the files the object
/// containers name.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/aros-development-team/AROS/tree/master/rom/filesys/SFS</c> — AROS SFS implementation — maintained open source</description></item>
///   <item><description>John Hendrikx's original SFS specification (the xs4all.nl page cited above; now web-archived)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Smart_File_System</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The walk to the files is implemented in <see cref="SfsVolume" /> and
/// the volumes are written by <see cref="SfsWriter" />, both following the
/// block structures in AROS's own SFS source. So the root-block surface above
/// is no longer all there is: files are listed, extracted, written and laid out
/// again.</para>
///
/// <para>There is no SFS driver or checker on Linux to hold a volume up
/// against, so what stands in for one is the format's own arithmetic. Every
/// block that carries a header records its own block number and is checksummed
/// by its longwords summing to zero, and a volume that failed either would be
/// rejected by any reader — including this one, which checks both before it
/// believes a block is what it claims.</para>
///
/// <para>What is written is the simplest shape the structures allow: one object
/// container for a flat root directory, one leaf of extents, one node
/// container. Hash tables, soft links, sub-directories and multi-level trees
/// are shapes the format has and this does not produce.</para>
///
/// <para>An existing volume is edited by reading its files out and laying the
/// volume out again through the same writer, so an add or remove costs the whole
/// volume rather than the bytes that changed.</para>
/// </remarks>
public sealed class SfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap {

  /// <summary>Entries that describe the volume rather than live in it.</summary>
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.Ordinal) { "FULL.sfs", "metadata.ini", "root_block.bin" };
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Sfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Amiga SFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest
    | FormatCapabilities.CanCreate | FormatCapabilities.CanModify;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "SFS\0" at offset 0 of the root block — unique enough that 0.95 confidence
    // is honest. We probe offsets 0 / 512 / 1024 in the parser to be lenient on
    // partitioned dumps but the canonical detection point is 0.
    new([0x53, 0x46, 0x53, 0x00], Offset: 0, Confidence: 0.95),
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
  public string Description =>
    "Amiga Smart Filesystem volume — files, and a layout pass over them.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    SfsRootBlock root;
    try {
      root = SfsRootBlock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.sfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    var idx = 2;
    if (root.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "root_block.bin", root.RawBytes.LongLength, root.RawBytes.LongLength, "stored", false, false, null));

    // And the files themselves, when the walk to them lands.
    using (var full = new MemoryStream(image, writable: false)) {
      var volume = new SfsVolume(full);
      if (volume.Valid)
        foreach (var file in volume.Files)
          entries.Add(new ArchiveEntryInfo(idx++, file.Name, file.Size, file.Size, "stored", false, false, null));
    }

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

    SfsRootBlock root;
    try {
      root = SfsRootBlock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.sfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.sfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(root), files);
    if (root.Valid)
      WriteIfMatch(outputDir, "root_block.bin", root.RawBytes, files);

    using var full = new MemoryStream(image, writable: false);
    var volume = new SfsVolume(full);
    if (!volume.Valid) return;
    foreach (var file in volume.Files)
      WriteIfMatch(outputDir, file.Name, volume.Read(file), files);
  }

  /// <summary>Writes a volume holding the given files.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var image = BuildImage(FilesOnly(inputs).ToList());
    output.Write(image, 0, image.Length);
    output.Flush();
  }

  /// <summary>
  /// Adds or replaces files: the volume's files are read out, the inputs merged
  /// in by name, and the volume laid out again.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ModifyRebuilder.Add(archive, inputs, ReadEntries, BuildImage, StringComparer.Ordinal);
  }

  /// <summary>
  /// Removes the named files and lays the volume out again without them, so
  /// nothing of their bytes remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ModifyRebuilder.Remove(archive, entryNames, ReadEntries, BuildImage, StringComparer.Ordinal);
  }

  /// <summary>The files a volume holds — never the entries that describe the volume.</summary>
  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    stream.Position = 0;
    var volume = new SfsVolume(stream);
    if (!volume.Valid)
      throw new InvalidDataException($"SFS: {volume.Status}.");
    return volume.Files.Select(f => (f.Name, volume.Read(f))).ToList();
  }

  /// <summary>A fresh volume holding exactly the given files.</summary>
  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var writer = new SfsWriter();
    foreach (var (name, data) in files) {
      if (SyntheticNames.Contains(Path.GetFileName(name))) continue;
      writer.AddFile(name, data);
    }
    return writer.Build();
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => SfsExtentMap.Enumerate(image);

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(SfsRootBlock root) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(root.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_block_offset={root.RootBlockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"checksum=0x{root.Checksum:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"own_block={root.OwnBlock}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={root.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sequence_number={root.SequenceNumber}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"date_created={root.DateCreated}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_blocks={root.TotalBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={root.BlockSize}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Moves the blocks that are out of place and rewrites the extent tree.</summary>
  /// <remarks>
  /// <para>An extent's key is the block it starts at, so a move renames it as
  /// well as relocating it, and everything that referred to it by that name has
  /// to follow. The volume's own structures — the root block and its copy, the
  /// bitmap, the admin space, the node table, the extent tree and the root
  /// directory — stay exactly where they are: each records its own block number
  /// and is checksummed over its whole block.</para>
  ///
  /// <para>The tree is written once the pass is over. One run's old key is
  /// routinely another's new one, and a tree rewritten halfway through would
  /// name two extents the same thing.</para>
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (!archive.CanSeek || archive.Length > PlannerImageCap)
      throw new NotSupportedException(
        "SFS defragmentation needs a seekable volume small enough to verify by reading it back.");

    var planned = false;
    // The pass is kept only if every file still reads back: a mover can refuse
    // partway, and leaving the volume as it was is the honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: ReadPayloadsForGuard,
      inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
      rebuild: () => planned = false);

    if (!planned)
      throw new NotSupportedException(
        "SFS defragmentation could not lay this volume out in place, and there is no rebuild to " +
        "fall back on: a file's blocks must stay clear of the structures the volume describes " +
        "itself with.");
  }

  /// <summary>Largest volume held in memory twice for the guarded pass.</summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var volume = new SfsVolume(stream);
    if (!volume.Valid)
      throw new InvalidDataException($"SFS: {volume.Status}.");
    return volume.Files.Select(volume.Read).ToList();
  }

  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    var mover = new SfsBlockMover();
    archive.Position = 0;
    mover.Init(archive);

    archive.Position = 0;
    var extents = SfsExtentMap.Enumerate(archive).ToList();
    if (extents.Count == 0) return;

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
    mover.Settle(archive);

    archive.Position = 0;
    var postExtents = SfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  // Bounded — SFS root block is at offset 0 with magic "SFS\0"; we only need the
  // first few KB for header surfacing.
  private const int HeaderReadCap = (int)PlannerImageCap;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
