#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pdf;

public sealed class PdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Pdf";
  public string DisplayName => "PDF (Image Extraction)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pdf";
  public IReadOnlyList<string> Extensions => [".pdf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("dct", "DCTDecode (JPEG)"), new("jpx", "JPXDecode (JPEG2000)"), new("flate", "FlateDecode")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PDF image extraction (JPEG, JPEG2000, raw image streams)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PdfReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, e.Filter, false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PdfReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
