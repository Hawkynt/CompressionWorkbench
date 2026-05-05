#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hpi;

public sealed class HpiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Hpi";
  public string DisplayName => "Total Annihilation HPI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".hpi";
  // .ufo (TA: Kingdoms), .ccx (patch), .gp3 (mod) all share HAPI magic + structure.
  public IReadOnlyList<string> Extensions => [".hpi", ".ufo", ".ccx", ".gp3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("HAPI"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hpi-zlib", "HPI zlib")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Total Annihilation HAPI archive (zlib subset)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HpiReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i,
      e.Name,
      e.Size,
      e.Size, // Per-chunk compressed sizes aren't aggregated cheaply; report original for both.
      e.IsDirectory ? "Directory" : "Zlib",
      e.IsDirectory,
      false,
      null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HpiReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new HpiWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var bytes = File.ReadAllBytes(input.FullPath);
      // HPI is path-aware (TA reads "units/armcom.fbi" etc.), so we preserve archive paths verbatim.
      w.AddFile(input.ArchiveName, bytes);
    }
  }
}
