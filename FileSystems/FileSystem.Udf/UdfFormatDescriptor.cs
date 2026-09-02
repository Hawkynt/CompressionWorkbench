#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Udf;

/// <summary>
/// R/W descriptor for UDF 2.01 (Universal Disk Format) volume images per
/// ECMA-167 and the OSTA UDF profile.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-167/</c> — ECMA-167 — the base volume/file structure standard</description></item>
///   <item><description>OSTA "Universal Disk Format Specification, revision 2.01" (osta.org) — the UDF profile of ECMA-167</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Universal_Disk_Format</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class UdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for UDF 2.01 creation. The natural per-volume knob is the
  /// PVD Volume Identifier (ECMA-167 §7.2.5) — Linux's udf driver surfaces
  /// this string as the volume label. Image geometry is auto-sized to fit
  /// the file content.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume identifier",
      Kind: FormatOptionKind.String,
      Default: "UDF Volume",
      Description: "ECMA-167 PVD Volume Identifier (dstring, max 31 ASCII chars). " +
        "Shown by file managers and the udf driver as the volume label."),
  ];

  /// <summary>
  /// Walks AVDP@LBA 256 → VDS → FSD → root FE, then recurses through
  /// directory File Entries and decodes short_ad / long_ad allocation
  /// descriptors. The 32 KiB system area, VRS, AVDP, every VDS sector, the
  /// FSD, and every File Entry sector surface as MetadataReserved; file
  /// data extents surface as Used. Adjacent same-run extents are coalesced.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => UdfExtentMap.Enumerate(image);

  /// <summary>
  /// Zeros all unused space in a UDF image: every sector not claimed by the
  /// system area, VRS, AVDP, VDS, FSD, a File Entry, or a file's allocated data
  /// run. Driven by the generic <see cref="UnusedSpaceWiper"/> over the UDF
  /// extent map.
  ///
  /// <para>Cluster tips are wiped: a UDF allocation descriptor records the
  /// file's logical byte length, so a file's Used extent ends exactly at its
  /// real size. The sector padding between that size and the next 2048-byte
  /// boundary is left uncovered and is zeroed as ordinary free space. A
  /// file-size lookup keyed on the entry name is also supplied so the wiper can
  /// trim any tail explicitly; only contiguous file-data extents (whose
  /// <c>FileName</c> matches a non-directory entry) are affected — metadata and
  /// directory File Entries are skipped, so live data and on-disk structures
  /// are never touched.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new UdfReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = UdfExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new UdfBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new UdfBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  // WORM write constraints — UDF has no inherent ceiling; minimum viable image ~1 MB.
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => 1 * 1024 * 1024;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription => "UDF 2.01 disc image; any files, flat directory.";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Udf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "UDF";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces) files at the root of an existing UDF image. Uses
  /// <see cref="UdfModifier"/> for true random-access I/O — only the
  /// Partition Descriptor sector, the root directory's File Entry sector,
  /// the FID extent, and the new file's FE + data sectors are touched.
  /// The 32 KiB system area, VRS, AVDP, LVD, and FSD are left untouched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs))
      UdfModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing UDF image. Uses
  /// <see cref="UdfModifier"/> for O(touched bytes) random-access I/O — the
  /// FID's deleted flag (ECMA-167 §14.4.3 bit 2) is set, its identifier
  /// bytes are zeroed, the tag is re-CRC'd, and the file's FE and data
  /// extents are zero-wiped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      UdfModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".udf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".udf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // The volume recognition sequence starts at 32 KB, one 2048-byte descriptor
    // per sector: BEA01, then NSR02/NSR03, then TEA01. Registering NSR at the
    // first sector only meant a standard sequence matched nothing at all and
    // the image fell through to a boot-sector heuristic.
    new("BEA01"u8.ToArray(), Offset: 0x8001, Confidence: 0.90),
    new("NSR02"u8.ToArray(), Offset: 0x8801, Confidence: 0.90),
    new("NSR03"u8.ToArray(), Offset: 0x8801, Confidence: 0.90),
    new("NSR02"u8.ToArray(), Offset: 0x8001, Confidence: 0.90),
    new("NSR03"u8.ToArray(), Offset: 0x8001, Confidence: 0.90),
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
public string Description => "Universal Disk Format";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new UdfReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new UdfReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Opens a single UDF file entry as a bounded read-only <see cref="Stream"/>.
  /// UDF stores file data in extents pointed to by File Entries — the reader's
  /// extract follows those allocation descriptors and returns the assembled
  /// bytes; they are wrapped in a <see cref="BoundedEntryStream"/> sized to
  /// the entry's logical size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new UdfReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // Spilled to scratch that deletes itself on close, so an entry larger than
      // memory still opens as an ordinary stream.
      var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 81920, FileOptions.DeleteOnClose);
      var written = r.ExtractTo(e, scratch);
      scratch.Position = 0;
      return new BoundedEntryStream(scratch, written, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new UdfWriter {
      VolumeIdentifier = options?.GetOption("VolumeLabel", "UDF Volume") ?? "UDF Volume",
    };
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Streaming creation. UDF descriptor CRCs (FID / File Entry / VDS tags) cover
  /// only the 16-byte tag bodies, NEVER file data, and the writer emits sectors
  /// strictly forward in LBN order — so each file's body can be streamed from
  /// <see cref="StreamingArchiveInput.OpenStream"/> in 64 KiB chunks straight
  /// into the sequential output when its data block is reached. No buffering of
  /// the body is required and the output is byte-identical to <see cref="Create"/>.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new UdfWriter {
      VolumeIdentifier = options?.GetOption("VolumeLabel", "UDF Volume") ?? "UDF Volume",
    };
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy
  /// of the image to compare payloads across the pass, so a volume past this
  /// takes the streaming path instead.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadEntriesForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new UdfReader(stream, leaveOpen: true);
    var contents = new List<byte[]>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      using var buffer = new MemoryStream();
      reader.ExtractTo(entry, buffer);
      contents.Add(buffer.ToArray());
    }

    return contents;
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing as it goes.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new UdfBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = UdfExtentMap.Enumerate(archive).ToList();
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
    var postExtents = UdfExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Mode-aware UDF 2.01 defragmentor via read-extract-rebuild dispatch
  /// through <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start image with system area + VRS + AVDP + VDS + FSD
  /// + root FE and a packed file-data region.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file's
    // extents are named by allocation descriptors in its file entry, so a move
    // is the copy plus those eight bytes.
    //
    // The mover reads the descriptor chain — anchor, volume descriptor
    // sequence, partition, file set, directories — once in Init and remembers
    // where each file entry sits. Walking it again per move is what kept this
    // path unused.
    // The guard below snapshots the image to compare payloads across the pass,
    // so it is only offered where a snapshot fits; a volume past the cap takes
    // the streaming path.
    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadEntriesForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "UDF defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    // Files go through scratch rather than a byte[] image, so a volume larger
    // than an array can hold still defragments.
    var tempPath = Path.GetTempFileName();
    var spill = new List<string>();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        var w = new UdfWriter();
        using (var r = new UdfReader(archive, leaveOpen: true)) {
          foreach (var e in r.Entries) {
            if (e.IsDirectory) continue;
            var path = Path.GetTempFileName();
            spill.Add(path);
            long size;
            using (var scratch = File.Create(path))
              size = r.ExtractTo(e, scratch);
            var captured = path;
            w.AddStreamingFile(e.Name, size, () => File.OpenRead(captured));
          }
        }
        w.WriteTo(temp);

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }
}
