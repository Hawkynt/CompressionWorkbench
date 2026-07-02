#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Atari8;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.atarimax.com/jindroush.atari.org/afmtatr.html</c> — ATR file format description (Jindroush archive); the header layout defined by Nick Kennedy's SIO2PC</description></item>
///   <item><description>Atari DOS 2.0S/2.5 Reference Manual (Atari, Inc.) — VTOC + directory sector layout on the SS/SD 720-sector disk</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Atari_DOS</c> — Wikipedia overview of the Atari 8-bit DOS family</description></item>
/// </list>
/// </summary>
public sealed class Atari8FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for ATR creation. AtariDOS 2.x has no concept of a volume
  /// label and this writer emits only SS/SD geometry (720 × 128 = 92 160 bytes
  /// of data plus a 16-byte ATR header), so the only meaningful knob is the
  /// ATR header's write-protect flag at offset 15.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "WriteProtect",
      DisplayName: "Write protect",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Sets the ATR header flags byte (offset 15, bit 0). Emulators that " +
        "honour the flag (Atari800, Altirra, …) will refuse to write the image."),
  ];

  /// <summary>
  /// Walks the ATR header + VTOC + directory + per-file sector chains
  /// and yields the actual on-disk byte layout. Header / VTOC / directory
  /// sectors become <see cref="DefragBlockKind.MetadataReserved"/>, file
  /// chains coalesce into contiguous-run extents, and un-attributed
  /// sectors are emitted as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Atari8ExtentMap.Enumerate(image);

  // Writer emits SS/SD (92 176 bytes). Declared ceiling matches Atari8Writer.ImageSize.
  public long? MaxTotalArchiveSize => Atari8Writer.ImageSize;
  public string AcceptedInputsDescription =>
    "Atari 8-bit AtariDOS 2.x disk (SS/SD 92 176, SS/ED 133 136, or DS/DD 183 936 bytes).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical ATR sizes: SS/SD (92 176) is the one this WORM writer emits.</summary>
  public IReadOnlyList<long> CanonicalSizes => [Atari8Writer.ImageSize];

  public string Id => "Atari8";
  public string DisplayName => "ATR (Atari 8-bit)";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Atari8 image.
  /// Uses <c>Atari8Modifier</c> for true O(touched bytes) random-access
  /// I/O — only the VTOC, the touched directory sector, and the file's
  /// data sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      Atari8Modifier.RemoveFile(archive, name, wipeData: true);
      Atari8Modifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Atari8 image. Uses
  /// <c>Atari8Modifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      Atari8Modifier.RemoveFile(archive, name, wipeData: true);
  }


  public string DefaultExtension => ".atr";
  public IReadOnlyList<string> Extensions => [".atr"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // ATR magic 0x0296, stored little-endian as 96 02 at offset 0.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x96, 0x02], Offset: 0, Confidence: 0.90)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari 8-bit AtariDOS 2.x floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new Atari8Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Atari8Reader(stream);
    foreach (var e in r.Entries) {
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
    var r = new Atari8Reader(archive);
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
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"AtariDOS: combined input size {total} bytes exceeds SS/SD capacity ({cap} bytes).");

    var w = new Atari8Writer();
    w.WriteProtected = options?.GetOptionBool("WriteProtect", false) ?? false;
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new Atari8BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new Atari8BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware AtariDOS defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new Atari8Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new Atari8Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = Atari8ExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new Atari8BlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 128, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an Atari 8-bit ATR (AtariDOS 2) image: the ATR
  /// header is preserved, and every sector not claimed by the VTOC, the
  /// directory, or a live file's sector chain is zeroed. Driven by the generic
  /// <see cref="UnusedSpaceWiper"/> over the Atari8 extent map.
  ///
  /// <para>Per-file cluster-tip wiping is <em>not</em> applied: AtariDOS stores
  /// a 3-byte link trailer (file number, next sector, byte count) at the end of
  /// every data sector, so each sector mixes data with metadata and the file's
  /// logical bytes are not a flat <c>offset..offset+size</c> region. Treating a
  /// run's tail as slack would clobber a sector's link bytes, so tip wiping is
  /// N/A here; only genuinely free sectors are zeroed.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = Atari8ExtentMap.Enumerate(image);
    // Tips are N/A for AtariDOS (each sector ends with a 3-byte link trailer
    // interleaving data and metadata); wipe free sectors only.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
