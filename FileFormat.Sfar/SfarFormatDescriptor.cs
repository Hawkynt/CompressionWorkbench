#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sfar;

public sealed class SfarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Sfar";
  public string DisplayName => "BioWare SFAR";
  public FormatCategory Category => FormatCategory.Archive;

  // Read-only by design: writing requires LZX block packing + SHA-1 and MD5 hash-table
  // generation against canonical game paths, which is out of scope this wave.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".sfar";
  public IReadOnlyList<string> Extensions => [".sfar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x53, 0x46, 0x41, 0x52], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sfar", "SFAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BioWare Sirius File Archive (Mass Effect 3 DLC, read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new SfarReader(stream, leaveOpen: true);
    var method = r.IsLzxCompressed ? "LZX" : "Stored";
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, method, false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new SfarReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
