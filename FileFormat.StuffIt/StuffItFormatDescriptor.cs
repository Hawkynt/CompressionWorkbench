#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StuffIt;

public sealed class StuffItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "StuffIt";
  public string DisplayName => "StuffIt";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sit";
  public IReadOnlyList<string> Extensions => [".sit"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'S', (byte)'I', (byte)'T', (byte)'!'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stuffit", "StuffIt")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Macintosh StuffIt archive, classic Mac compression";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new StuffItReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.DataForkSize,
      e.CompressedDataSize, $"Method {e.DataMethod}", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new StuffItReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new StuffItWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
