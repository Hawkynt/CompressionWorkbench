#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wad;

public sealed class WadFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Wad";
  public string DisplayName => "WAD";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wad";
  public IReadOnlyList<string> Extensions => [".wad"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'I', (byte)'W', (byte)'A', (byte)'D'], Confidence: 0.90),
    new([(byte)'P', (byte)'W', (byte)'A', (byte)'D'], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wad", "WAD")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Doom WAD game data archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WadReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", e.IsMarker, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WadReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsMarker) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new WadWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddLump(name, data);
  }
}
