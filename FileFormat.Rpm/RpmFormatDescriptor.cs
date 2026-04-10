#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rpm;

public sealed class RpmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Rpm";
  public string DisplayName => "RPM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".rpm";
  public IReadOnlyList<string> Extensions => [".rpm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xED, 0xAB, 0xEE, 0xDB], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rpm", "RPM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Red Hat Package Manager archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    [new(0, "payload.cpio", 0, 0, "cpio", false, false, null)];

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RpmReader(stream);
    using var payload = r.GetPayloadStream();
    var cpioReader = new FileFormat.Cpio.CpioReader(payload);
    foreach (var (entry, data) in cpioReader.ReadAll()) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, entry.Name)); continue; }
      WriteFile(outputDir, entry.Name, data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new RpmWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
