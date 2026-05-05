#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Uharc;

public sealed class UharcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Uharc";
  public string DisplayName => "UHARC";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".uha";
  public IReadOnlyList<string> Extensions => [".uha"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'U', (byte)'H', (byte)'A'], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("uharc", "UHARC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "UHARC high-compression archive (PPM/LZP/delta)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new UharcReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize,
      e.CompressedSize, e.Method == 255 ? "Store" : "LZP", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new UharcReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new UharcWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }
}
