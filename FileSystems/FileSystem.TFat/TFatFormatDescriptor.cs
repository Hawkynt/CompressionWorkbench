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
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs))
      TFatModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes named entries from a TFAT image using the alternating-FAT
  /// transactional commit protocol. Each removal is a separate transaction.
  /// Cluster data is wiped before the seq bump so no forensic trace of the
  /// removed bytes remains after commit.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      TFatModifier.RemoveFile(archive, name, wipeData: true);
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
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new TFatReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new TFatWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
