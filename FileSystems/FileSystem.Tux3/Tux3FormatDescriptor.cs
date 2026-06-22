#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Read+WORM descriptor for TUX3 — Daniel Phillips's version-tree
/// successor to TUX2 (linux-tux3 prototype). Magic "TUX3SUPR" sits
/// at file offset 4096 (the start of the superblock block). The WORM
/// writer emits a single-version image (no version chain, no atomic-commit
/// log) — the documented superblock prefix plus a sentinel "TUX3WORM" file
/// table at block 2 that <see cref="Tux3Reader"/> walks. Full
/// itable/otable/atable B-tree traversal of real linux-tux3 prototype dumps
/// is out of scope.
/// </summary>
public sealed class Tux3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The single tunable the single-version WORM writer honours: the 64-bit
  /// <c>birthday</c> field stamped into the superblock at offset 0x08.
  /// <see cref="Tux3Writer.Birthday"/> is written verbatim and
  /// <see cref="Tux3Reader.Birthday"/> reads it back, so the knob round-trips.
  /// Supplied as a hexadecimal string (with or without a leading <c>0x</c>);
  /// the default matches the writer's deterministic placeholder.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Birthday", DisplayName: "Birthday (hex)", Kind: FormatOptionKind.String,
      Default: "5455583342534831",
      Description: "64-bit creation stamp written to the superblock at offset 0x08 (hexadecimal)."),
  ];

  public string Id => "Tux3";
  public string DisplayName => "TUX3";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tux3";
  public IReadOnlyList<string> Extensions => [".tux3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX3SUPR"u8.ToArray(), Offset: 4096, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TUX3 version-tree research filesystem (linux-tux3) — single-version WORM image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux3Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Tux3Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Emits a fresh single-version TUX3 image: zeroed boot region (block 0),
  /// documented superblock prefix (block 1, "TUX3SUPR" magic at offset 4096),
  /// and a sentinel WORM file table at block 2 carrying the per-file
  /// records. Round-trips through <see cref="Tux3Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Tux3Writer { Birthday = ParseBirthday(options.GetOption("Birthday", "5455583342534831")) };
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  /// <summary>Parses the hex <c>Birthday</c> knob; unparsable input falls back to the writer default.</summary>
  private static ulong ParseBirthday(string value) {
    var s = value.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      s = s[2..];
    return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
      System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0x_5455_5833_4253_4831UL;
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Tux3 single-version WORM — defragmentation requires a rewriting writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Tux3 single-version WORM — defragmentation requires a rewriting writer.");
}
