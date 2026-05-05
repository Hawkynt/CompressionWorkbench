#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mix;

public sealed class MixFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Mix";
  public string DisplayName => "Westwood MIX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mix";
  public IReadOnlyList<string> Extensions => [".mix"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("td", "MIX TD/RA1")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Westwood Studios C&C / Red Alert archive (TD/RA1 variant)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MixReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MixReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MixWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddEntry(name, data);
  }
}
