#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.TFat;

/// <summary>
/// Transactional FAT (TFAT) — Microsoft Windows CE / Windows Embedded Compact
/// variant of FAT12/16/32 that uses dual FAT copies as a two-phase commit
/// log. The on-disk layout is identical to standard FAT; TFAT differs only
/// in (a) detection markers in the BPB and (b) the runtime protocol that
/// alternates which FAT is "active" on each transaction.
///
/// <para>This descriptor delivers read, WORM-create and true in-place
/// transactional update support via the alternating-FAT commit protocol
/// implemented in <see cref="TFatModifier"/>. Each Add or Remove is a single
/// transaction: writes go to the inactive FAT, then a single 4-byte
/// big-endian sequence-number write at the end of that FAT region commits
/// the transaction. A crash before the sequence write leaves the old FAT
/// (still active) untouched and the transaction is invisible.</para>
///
/// <para>Defragment is implemented via <see cref="DefragRebuilder"/> over
/// <see cref="TFatReader"/> + <see cref="TFatWriter"/>: the image is rebuilt
/// from scratch, then re-stamped with TFAT markers so both FAT copies stay
/// in lock-step. This is intentionally non-transactional (it rewrites the
/// whole image, not a single FAT) because defrag is an offline operation.</para>
///
/// <para><b>Limitation</b>: FAT12/16 with the fixed-area root directory is
/// fully supported for in-place modification. FAT32 root-cluster updates are
/// not supported — CE TFAT usage typically pins the root cluster, and
/// extending the transactional protocol to cover variable-size root
/// directories would require integrating dir-cluster allocation into the
/// commit point. WORM-create still works for FAT32.</para>
///
/// <para>Spec sources: TFAT marker layout from public Microsoft Windows CE /
/// Windows Embedded Compact documentation on the FAT transactional protocol,
/// supplemented by forensic-literature summaries. The runtime protocol
/// itself is documented in Microsoft's WinCE TFAT design notes.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>Microsoft Windows Embedded CE "TFAT Overview" documentation (archived MSDN)</description></item>
///   <item><description>Microsoft "FAT: General Overview of On-Disk Format" (fatgen103) — the base FAT layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Transaction-Safe_FAT_File_System</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class TFatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────
  //
  // TFAT is FAT with a transaction-safe dual-FAT marker, so it honours the same
  // geometry knobs FAT does: image size, cluster size, FAT type and volume
  // label. TFAT targets embedded / Windows CE devices, so the image-size presets
  // skew towards floppy + small-card sizes rather than optical media.
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(
      sizes: ["1.44 MB (3.5\" HD)", "32 MB", "128 MB", "512 MB", "1 GB", "2 GB", "4 GB"],
      description: "Total image capacity. Auto sizes the image to exactly hold the files (recommended). " +
        "Fixed presets match the floppy and embedded/WinCE card sizes TFAT is typically used on."),
    FilesystemSchemaPresets.ClusterSize(),
    FilesystemSchemaPresets.VolumeLabel(),
    new FormatOptionDescriptor(
      Key: "FatType",
      DisplayName: "FAT type",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "FAT12", "FAT16", "FAT32"],
      Description: "Auto selects FAT12/16/32 by cluster count. Force a type when the target device requires it."),
  ];

  public string Id => "TFat";
  public string DisplayName => "Transactional FAT (TFAT)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".tfat";
  public IReadOnlyList<string> Extensions => [".tfat"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Magic signatures: the 4-byte "TFAT" prefix appears at one of two offsets
  // in the BPB BS_FilSysType field — offset 54 for FAT12/16, offset 82 for
  // FAT32. The high confidence (0.92) reflects that the standalone "TFAT"
  // tag is unique to this format. The signatures are checked at fixed
  // offsets by the FormatDetector so detection is O(1).
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TFAT"u8.ToArray(), Offset: 54, Confidence: 0.92),
    new("TFAT"u8.ToArray(), Offset: 82, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows CE / Embedded Compact Transactional FAT (dual-FAT atomic commit)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TFatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TFatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new TFatWriter();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);

    var specific = options.FormatSpecific;
    var totalSectors  = ParseImageSizeSectors(specific?.GetValueOrDefault("ImageSize"));
    var clusterBytes  = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("ClusterSize"));
    var forcedFatType = ParseFatType(specific?.GetValueOrDefault("FatType"));
    var label         = specific?.GetValueOrDefault("VolumeLabel");

    // Fixed image size + cluster on Auto: optimise the cluster size *within* that
    // fixed size to minimise slack waste (mirrors FatFormatDescriptor.Create).
    if (totalSectors > 0 && clusterBytes == 0) {
      var picked = w.PickClusterForFixedImage(totalSectors, 512, forcedFatType, 0, enableLfn: true);
      if (picked > 0) clusterBytes = picked;
    }

    // A fixed size goes through the streaming writer whenever the target can seek:
    // Build() materialises the whole volume as one byte[] and so caps TFAT at the
    // ~2 GB array limit, while BuildTo leaves free space sparse.
    if (totalSectors > 0 && output.CanSeek) {
      w.BuildTo(output, totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label,
                forcedFatType: forcedFatType);
      return;
    }

    var disk = totalSectors > 0
      ? w.Build(totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label,
                forcedFatType: forcedFatType)
      : w.BuildAutoSized(requestedClusterSize: clusterBytes, volumeLabel: label,
                         forcedFatType: forcedFatType);
    output.Write(disk);
  }

  private static int ParseFatType(string? s) => s?.Trim() switch {
    "FAT12" => 12,
    "FAT16" => 16,
    "FAT32" => 32,
    _       => 0,  // Auto
  };

  private static int ParseImageSizeSectors(string? s) => s?.Trim() switch {
    "1.44 MB (3.5\" HD)" => 2880,
    "32 MB"   => 65536,
    "128 MB"  => 262144,
    "512 MB"  => 1048576,
    "1 GB"    => 2097152,
    "2 GB"    => 4194304,
    "4 GB"    => 8388608,
    _         => 0,  // "Auto (fit to files)" or anything else → auto-size
  };

  /// <summary>
  /// Adds files to an existing TFAT image using the alternating-FAT
  /// transactional commit protocol. Each file is added as a separate
  /// transaction (single seq bump per file) so a crash mid-batch leaves a
  /// consistent prefix of added files. Replace-by-name semantics: an Add of
  /// a file whose short name already exists frees the old chain and
  /// re-allocates within the same transaction.
  /// </summary>
  /// <summary>Largest volume the byte[]-based in-place editors and buffered rebuild can take.</summary>
  private const long MaxBufferedImageBytes = 1L << 31;

  /// <summary>
  /// Applies an edit by reading every surviving entry out of <paramref name="archive" />
  /// and writing a fresh volume of the same declared size back over it. Used when the
  /// in-place path declines the volume -- notably FAT32, which the TFAT modifier does
  /// not update in place, and which any volume past 4 GB necessarily is.
  /// </summary>
  private static void RebuildInPlaceStreaming(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> additions,
      ISet<string>? drop) {
    var totalSectors = (int)Math.Min(int.MaxValue, archive.Length / 512);
    var combined = new TFatWriter();

    archive.Position = 0;
    var reader = new TFatReader(archive, leaveOpen: true);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      if (drop != null && (drop.Contains(entry.Name) || drop.Contains(Path.GetFileName(entry.Name))))
        continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    foreach (var (name, data) in additions)
      combined.AddFile(name, data);

    archive.Position = 0;
    archive.SetLength(0);
    combined.BuildTo(archive, totalSectors);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var items = FilesOnly(inputs).ToList();
    try {
      foreach (var (name, data) in items)
        TFatModifier.AddFile(archive, name, data);
    } catch (NotSupportedException) {
      // The in-place modifier covers FAT12/16 only; rebuild instead.
      RebuildInPlaceStreaming(archive, items, drop: null);
    }
  }

  /// <summary>
  /// Removes named entries from a TFAT image using the alternating-FAT
  /// transactional commit protocol. Each removal is a separate transaction.
  /// Cluster data is wiped before the seq bump so no forensic trace of the
  /// removed bytes remains after commit.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    try {
      foreach (var name in entryNames)
        TFatModifier.RemoveFile(archive, name, wipeData: true);
    } catch (NotSupportedException) {
      // The in-place modifier covers FAT12/16 only; rebuild instead.
      RebuildInPlaceStreaming(archive, [], new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase));
    }
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware TFAT defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The underlying FAT writer always emits a
  /// fresh contiguous-from-start image and the TFAT post-pass re-stamps the
  /// transactional sequence markers so both FAT copies stay in lock-step.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Defrag preserves the outer size. Build() defaults to 2880 sectors, so omitting
    // the sector count silently rewrote any non-floppy volume as a 1.44 MB image.
    var totalSectors = (int)Math.Min(int.MaxValue, archive.Length / 512);

    // A volume too large to materialise goes through the streaming rebuilder; the
    // buffered path's buildImage returns a byte[] of the whole image.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes
        && options.Mode is DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy) {
      TFatWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new TFatReader(stream, leaveOpen: true);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new TFatWriter(); target = s2; },
        writeEntry: (name, data) => streamWriter!.AddFile(name, data),
        finishWrite: () => streamWriter!.BuildTo(target!, totalSectors));
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new TFatReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new TFatWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(totalSectors);
      });
  }
}
