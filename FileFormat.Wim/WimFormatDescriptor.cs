#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wim;

public sealed class WimFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Wim";
  public string DisplayName => "WIM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wim";
  public IReadOnlyList<string> Extensions => [".wim", ".swm", ".esd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'S', (byte)'W', (byte)'I', (byte)'M', 0x00, 0x00, 0x00], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wim", "WIM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows Imaging Format, file-based disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WimReader(stream);
    var namedFiles = r.GetNamedFiles();

    if (namedFiles.Count > 0) {
      return namedFiles.Select((f, i) => new ArchiveEntryInfo(i, f.FileName, f.FileSize,
        f.ResourceIndex >= 0 ? r.Resources[f.ResourceIndex].CompressedSize : 0,
        r.Header.CompressionType != WimConstants.CompressionNone ? "Compressed" : "Store",
        false, false, null)).ToList();
    }

    // Fallback: no metadata — list raw resources.
    return r.Resources
      .Where(e => !e.IsMetadata)
      .Select((e, i) => new ArchiveEntryInfo(i, $"resource_{i}", e.OriginalSize, e.CompressedSize,
        e.IsCompressed ? "Compressed" : "Store", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WimReader(stream);
    var namedFiles = r.GetNamedFiles();

    if (namedFiles.Count > 0) {
      foreach (var f in namedFiles) {
        if (files != null && !MatchesFilter(f.FileName, files)) continue;
        if (f.ResourceIndex < 0) continue;
        WriteFile(outputDir, f.FileName, r.ReadResource(f.ResourceIndex));
      }
      return;
    }

    // Fallback: no metadata — extract raw resources.
    var dataIndex = 0;
    for (var i = 0; i < r.Resources.Count; ++i) {
      if (r.Resources[i].IsMetadata) continue;
      var name = $"resource_{dataIndex}";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ReadResource(i));
      dataIndex++;
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var resources = FormatHelpers.FilesOnly(inputs).Select(f => f.Data).ToList();
    var w = new WimWriter(output);
    w.Write(resources);
  }
}
