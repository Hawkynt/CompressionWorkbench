using System.Text;
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Vdfs;

/// <summary>
/// ZenGin VDF volumes (signature "PSVDSC_V2.00" at offset 256, .vdf/.mod) — the virtual
/// disk Piranha Bytes' Gothic games load their data from — stored, or zipped the way
/// Union zips them.
/// </summary>
/// <remarks>
/// Layout and the Union ZippedStream codec are documented in <c>docs/VDFS-ON-DISK.md</c>,
/// taken from GothicKit's ZenKit (MIT) and verified against a GothicVDFS volume, a
/// VDFSTool-zipped volume, and ZenKit reading what this writes.
/// </remarks>
[FilesystemBlockMover(typeof(VdfsBlockMover))]
public sealed class VdfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, ILayoutOptimizable {

  /// <summary>Method name of a stored volume.</summary>
  public const string MethodStored = "stored";

  /// <summary>Method name of a zipped (Union ZippedStream) volume.</summary>
  public const string MethodZipped = "zipped";

  /// <summary>Gets the id.</summary>
  public string Id => "Vdfs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "VDFS";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".vdf";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".vdf"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures: the signature follows the 256-byte comment.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("PSVDSC_V2.00"u8.ToArray(), Offset: VdfsLayout.SignatureOffset, Confidence: 0.95)];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new(MethodStored, "Stored"),
    new(MethodZipped, "Zipped (Union ZippedStream, zlib)"),
  ];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description => "Gothic (ZenGin) VDF volume, stored or Union-zipped (layout per GothicKit ZenKit)";

  /// <summary>
  /// Lists the volume. Never throws: a stream with no VDF signature lists as the whole image
  /// plus a <c>metadata.ini</c> saying so, and a damaged catalog lists what it still names.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    VdfsReader reader;
    try {
      reader = new VdfsReader(stream, leaveOpen: true);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [
        new(0, FullImageName, length, length, MethodStored, false, false, null),
        new(1, MetadataName, 0, 0, MethodStored, false, false, null),
      ];
    }

    using (reader) {
      var list = reader.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.IsDirectory ? 0 : e.StoredSize, e.IsZipped ? "Zipped" : "Stored", e.IsDirectory, false, reader.Timestamp
      )).ToList();
      if (reader.IsPartial)
        list.Add(new(list.Count, MetadataName, 0, 0, MethodStored, false, false, null));
      return list;
    }
  }

  /// <summary>Name the whole image lists under when it cannot be parsed.</summary>
  private const string FullImageName = "FULL.vdf";

  /// <summary>Name of the note saying parsing was partial.</summary>
  private const string MetadataName = "metadata.ini";

  /// <summary>Extracts the volume's files, keeping their directories.</summary>
  /// <exception cref="InvalidDataException">A ZippedStream block does not inflate.</exception>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    VdfsReader reader;
    try {
      reader = new VdfsReader(stream, leaveOpen: true);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) {
      if (files == null || MatchesFilter(FullImageName, files)) {
        using var full = CreateEntryFile(outputDir, FullImageName);
        stream.Position = 0;
        stream.CopyTo(full);
      }
      if (files == null || MatchesFilter(MetadataName, files))
        WriteFile(outputDir, MetadataName, Encoding.UTF8.GetBytes("parse_status=partial\nreason=no VDF signature at offset 256\n"));
      return;
    }

    using (reader) {
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        // Streamed, not buffered: an entry may be larger than a byte[] can hold.
        using var target = CreateEntryFile(outputDir, e.Name);
        reader.ExtractTo(e, target);
      }
      if (reader.IsPartial && (files == null || MatchesFilter(MetadataName, files)))
        WriteFile(outputDir, MetadataName, Encoding.UTF8.GetBytes("parse_status=partial\nreason=catalog damaged or pointing outside the volume\n"));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream of its decoded bytes.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new VdfsReader(archive, leaveOpen: true);
    var wanted = entryName.Replace('\\', '/');
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Writes a volume of <paramref name="inputs" />, keeping their directories. Method
  /// <c>zipped</c> writes a Union zipped volume; "+" deflates at best effort.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var zipped = string.Equals(options.MethodName, MethodZipped, StringComparison.OrdinalIgnoreCase);
    var w = new VdfsWriter {
      Zipped = zipped,
      Level = options.Optimize ? DeflateCompressionLevel.Best : DeflateCompressionLevel.Default,
    };
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed for the catalog; reading a large input into a byte[]
      // would cap the volume at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) files in place: new payloads go to the end of the volume and the
  /// catalog is rewritten at 296; surviving payloads keep their bytes. See
  /// <see cref="VdfsInPlaceModifier"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in — and where it can still edit, it has no room
    // to grow a full volume. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      VdfsInPlaceModifier.AddFile(archive, input.ArchiveName, input.ReadContent());
  }

  /// <summary>
  /// Removes the named files (or directories) and zeroes their payloads. See
  /// <see cref="VdfsInPlaceModifier"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      VdfsInPlaceModifier.RemoveFile(archive, name);
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file
    // here is one contiguous run named by its catalog entry, so a move is the
    // copy and one write. What the planner will not commit to falls through to
    // the rebuild below.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{PlannerFallbackLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }

    // A container too large to materialise goes through the streaming rebuilder.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      bool zipped;
      using (var probe = new VdfsReader(archive, leaveOpen: true)) zipped = probe.IsZippedVolume;
      archive.Position = 0;
      VdfsWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => ReadEntries(stream).ToList(),
        beginWrite: s2 => { streamWriter = new VdfsWriter { Zipped = zipped, UpperCaseNames = false }; target = s2; },
        // As a stream factory, not inline: an inline payload would go back into
        // the buffer this path exists to avoid.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.WriteTo(target!));
    }
  }

  /// <summary>Largest container a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  // ── IFilesystemExtentMap ───────────────────────────────────────────────

  /// <summary>
  /// The header, the catalog where the header says it is, and each payload as stored —
  /// the whole ZippedStream for a zipped file.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    using var r = new VdfsReader(image, leaveOpen: true);
    var entries = r.Entries;

    yield return new DefragBlockInfo(0, VdfsLayout.HeaderSize, DefragBlockKind.MetadataReserved, "header");

    var tableStart = r.EntryTableOffset;
    var tableLength = r.EntryTableLength;
    if (tableStart >= 0 && tableLength > 0 && tableStart + tableLength <= image.Length)
      yield return new DefragBlockInfo(tableStart, tableLength, DefragBlockKind.MetadataReserved, "catalog");

    foreach (var e in entries) {
      if (e.IsDirectory || e.StoredSize <= 0) continue;
      yield return new DefragBlockInfo(e.DataOffset, e.StoredSize, DefragBlockKind.Used, e.Name);
    }
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros every byte of a VDF volume not claimed by the header, the catalog or a live
  /// payload — dead bytes left behind by editing or truncation. Payloads are packed with no
  /// cluster grid, so there is no cluster-tip slack to scrub (<paramref name="wipeClusterTips"/>
  /// has no effect here).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    var extents = this.EnumerateExtents(image).ToList();
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // ── Shared helpers ─────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new VdfsReader(stream, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  /// <summary>
  /// Moves only the files that are out of place, repointing each one's
  /// catalog entry as its run arrives.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new VdfsBlockMover();
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

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
