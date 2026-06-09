#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;
using FileSystem.ProDos;

namespace FileSystem.GsOs;

/// <summary>
/// Descriptor for Apple IIgs GS/OS 2IMG disk images. GS/OS volumes are
/// ProDOS-derived on disk; the 2IMG container wraps the ProDOS payload
/// with a 64-byte metadata header recognised by every IIgs emulator
/// (Catakig, ASIMOV2, Bernie ][ The Rescue, KEGS, GSplus).
///
/// <para><b>WORM tier.</b> Emit-only: <see cref="Create"/> builds a fresh
/// 2IMG-wrapped ProDOS volume via <see cref="GsOsWriter"/>; List/Extract
/// surface the embedded ProDOS volume as an opaque <c>.po</c> entry —
/// downstream callers can route the <c>.po</c> payload through
/// <see cref="ProDosFormatDescriptor"/> for full hierarchical walk.
/// Modify is intentionally not implemented because rewriting the
/// embedded volume requires recomputing the data-length field in the
/// 2IMG header and any comment-block offsets, which a true WORM rewrite
/// (via Create) handles correctly without surprising the caller.</para>
///
/// <para><b>Detection.</b> The .gsdos extension routes here (FileSystem.ProDos
/// owns .2mg/.po so the detector doesn't first-match between them); the
/// reader still parses the 2IMG header to validate the volume.</para>
///
/// <para><b>Spec.</b> Universal 2IMG specification (Catakig/IIgs emulator
/// community, 1997), ProDOS 8 Technical Reference Manual, Apple IIgs Hardware
/// Reference (1987). Both ProDOS 8 and GS/OS (ProDOS 16 / FST) read the
/// same on-disk volume layout.</para>
/// </summary>
public sealed class GsOsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IFormatOptionsSchema {
  public string Id => "GsOs";
  public string DisplayName => "Apple IIgs GS/OS (2IMG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  // .2mg is owned by FileSystem.ProDos; we register the GS/OS-specific
  // .gsdos extension only to avoid extension routing conflicts.
  public string DefaultExtension => ".gsdos";
  public IReadOnlyList<string> Extensions => [".gsdos"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic intentionally omitted: ProDos already advertises "2IMG"@0, and
  // we don't want detector first-match to fight over the same bytes.
  // Routing to GS/OS is by extension; the reader still parses the 2IMG header.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple IIgs GS/OS 2IMG — WORM-tier: emits a fresh 2IMG-wrapped ProDOS volume; read path surfaces the inner ProDOS volume as an opaque entry for downstream walk.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "ImageSize",
      DisplayName: "Image size",
      Kind: FormatOptionKind.Enum,
      Default: "140 KB (5.25\")",
      AllowedValues: ["140 KB (5.25\")", "800 KB (3.5\")"],
      Description: "Underlying ProDOS volume size — 140 KB 5.25\" floppy (280 blocks) or 800 KB 3.5\" floppy (1600 blocks)."),
    new FormatOptionDescriptor(
      Key: "VolumeName",
      DisplayName: "Volume name",
      Kind: FormatOptionKind.String,
      Default: "GSOS",
      Description: "ProDOS volume name (1..15 chars; letters, digits, periods; must start with a letter)."),
    new FormatOptionDescriptor(
      Key: "Creator",
      DisplayName: "2IMG creator code",
      Kind: FormatOptionKind.String,
      Default: GsOsWriter.DefaultCreator,
      Description: "Four-character ASCII creator code stamped at offset 4 of the 2IMG header."),
    new FormatOptionDescriptor(
      Key: "Comment",
      DisplayName: "Volume comment",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional ASCII comment block appended after the ProDOS payload (surfaced by the 2IMG comment offset/length fields)."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new GsOsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new GsOsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var sizeLabel = options.GetOption("ImageSize", "140 KB (5.25\")");
    var totalBlocks = sizeLabel.Contains("800", StringComparison.Ordinal)
      ? ProDosWriter.Disk800KTotalBlocks
      : ProDosWriter.FloppyTotalBlocks;
    var volumeName = options.GetOption("VolumeName", "GSOS");
    var creator = options.GetOption("Creator", GsOsWriter.DefaultCreator);
    var comment = options.GetOption("Comment", "");

    var w = new GsOsWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent());
    if (!string.IsNullOrEmpty(comment)) w.SetComment(comment);
    output.Write(w.Build(volumeName, totalBlocks, creator));
  }
}
