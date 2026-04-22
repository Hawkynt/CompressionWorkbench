#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.GodotPck;

public sealed class GodotPckFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "GodotPck";
  public string DisplayName => "Godot PCK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pck";
  public IReadOnlyList<string> Extensions => [".pck"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GDPC"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pck", "Godot PCK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Godot Engine resource pack";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PckReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PckReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new PckWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
