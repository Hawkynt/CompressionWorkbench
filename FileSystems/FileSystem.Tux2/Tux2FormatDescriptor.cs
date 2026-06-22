#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Read+WORM descriptor for TUX2 — Daniel Phillips's 2002 phase-tree
/// filesystem proposal (OLS 2002 paper, never-stabilised research format).
/// Recognises a deterministic header pattern (magic "TUX2FS\0\0" at offset 0)
/// so research images we generate round-trip through the reader. Writer emits
/// a single-phase image only (no alpha/beta phases, no version chain) — real
/// legacy prototype images would need a custom parser matching the specific
/// snapshot of the in-progress code that produced them.
/// </summary>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The single tunable the single-phase WORM writer honours: the on-disk
  /// format version stamped into the header at offset 0x08. <see cref="Tux2Writer.Version"/>
  /// is written verbatim and <see cref="Tux2Reader.Version"/> reads it back, so
  /// the knob round-trips. Defaults to 1 (the version the reader documents).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Version", DisplayName: "Image version", Kind: FormatOptionKind.Integer, Default: "1",
      Description: "Format version stamped into the TUX2 header at offset 0x08."),
  ];

  public string Id => "Tux2";
  public string DisplayName => "TUX2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tux2";
  public IReadOnlyList<string> Extensions => [".tux2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX2FS\0\0"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TUX2 phase-tree research filesystem (Daniel Phillips, OLS 2002) — single-phase synthetic image.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Tux2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Emits a fresh single-phase TUX2 image: 16-byte header (magic + version +
  /// file count) followed by per-file records (u16 name length, UTF-8 name,
  /// u32 data length, raw bytes). Round-trips through <see cref="Tux2Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var version = (uint)Math.Max(0, options.GetOptionInt("Version", 1));
    var w = new Tux2Writer { Version = version };
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Tux2 single-phase WORM — defragmentation requires a rewriting writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Tux2 single-phase WORM — defragmentation requires a rewriting writer.");
}
