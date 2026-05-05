#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gob;

public sealed class GobFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Gob";
  public string DisplayName => "Lucasarts GOB";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gob";
  public IReadOnlyList<string> Extensions => [".gob", ".goo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Trailing space is part of the GOB v2 magic — without it we would collide with
  // GOB v1 (Dark Forces) which is structurally different and out of scope here.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GOB "u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gob2", "GOB v2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Lucasarts archive (Jedi Knight, Outlaws)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GobReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GobReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new GobWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddEntry(name, data);
  }
}
