#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Wafl;

/// <summary>
/// Stage-0 descriptor for NetApp WAFL (Write-Anywhere File Layout) volume images.
/// Surfaces only a synthetic <c>metadata.ini</c> and the raw image bytes; no real
/// inode walk is attempted.
///
/// <para>
/// <b>Stage-0 confirmed.</b> A read/write promotion was investigated against the
/// publicly available material (Hitz 1994 TR-3002, NetApp patents US5819292 and
/// US6289356, later NetApp WAFL papers, and the independent Aaru investigation).
/// Those sources publish the tree-of-blocks design and the 4 KiB allocation unit,
/// but not the byte-complete modern ONTAP mapping needed to translate FlexVol
/// virtual blocks through aggregate/RAID members or to prove snapshot reachability.
/// Consequently compact/defrag/wipe/shrink/re-layout/purge are deliberately not
/// advertised: each would risk treating still-referenced blocks as disposable.
/// </para>
/// </summary>
public sealed class WaflFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ILayoutOptimizable {

  /// <summary>Gets the id.</summary>
  public string Id => "Wafl";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "NetApp WAFL";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".wafl";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".wafl"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("wafd"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "NetApp WAFL — Stage-0 confirmed: detection plus opaque streaming only. " +
    "Public NetApp material fixes the allocation unit at 4 KiB and documents the tree/consistency-point model, " +
    "but not the byte-complete FlexVol aggregate/RAID mapping or snapshot reachability needed for safe offline R/W. " +
    "Layout analysis therefore reports the fixed 4 KiB geometry only; compact/defrag/wipe/shrink/layout rewrite/purge stay disabled.";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new WaflReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index,
      entry.Name,
      entry.Size,
      entry.Size,
      "Stored",
      entry.IsDirectory,
      false,
      null)).ToList();
  }

  /// <summary>Extracts the supplied pseudo-entries.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new WaflReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;

      using var source = reader.OpenEntry(entry);
      using var target = CreateEntryFile(outputDir, entry.Name);
      source.CopyTo(target);
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    using var reader = new WaflReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate => candidate.Name == entryName)
      ?? throw new FileNotFoundException($"WAFL entry not found: {entryName}", entryName);

    if (!archive.CanSeek)
      return new MemoryStream(reader.Extract(entry), writable: false);

    // Seekable inputs are not owned by WaflReader. Its disposal after this return
    // therefore leaves the bounded view alive while still avoiding a second copy
    // of a potentially multi-terabyte WAFL aggregate image.
    return reader.OpenEntry(entry);
  }

  /// <summary>
  /// Reports the only layout fact that is normative in the public WAFL material:
  /// the fixed 4 KiB allocation block. Free-space/slack accounting requires the
  /// private allocation maps and is intentionally not guessed.
  /// </summary>
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    using var reader = new WaflReader(image);
    return new LayoutAnalysis {
      ImageSize = reader.ImageSize,
      CurrentUnitSize = WaflReader.BlockSize,
      CurrentSlackBytes = 0,
      OptimalUnitSize = WaflReader.BlockSize,
      OptimalSlackBytes = 0,
      Notes = [
        "WAFL uses fixed 4096-byte allocation blocks in the published format design.",
        "Slack/free-space values are unavailable at Stage 0; zero means not computed, not that the volume has no slack.",
        "Offline re-layout is intentionally unavailable until FlexVol aggregate/RAID mappings and snapshot reachability can be parsed and validated.",
      ],
    };
  }
}
