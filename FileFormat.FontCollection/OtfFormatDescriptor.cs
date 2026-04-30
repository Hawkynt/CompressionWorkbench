#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a single-font .otf as <c>FULL.otf</c> + <c>metadata.ini</c> + per-glyph
/// SVG entries. OTFs with TrueType outlines ('glyf') split per glyph; OTFs with
/// CFF/CFF2 outlines emit FULL only and record the skip reason in metadata.ini.
/// </summary>
public sealed class OtfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Otf";
  public string DisplayName => "OTF (OpenType font — glyphs)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".otf";
  public IReadOnlyList<string> Extensions => [".otf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OTTO"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "OpenType font; FULL + metadata + per-glyph SVG (TrueType outlines).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    TtfFormatDescriptor.BuildEntries(Read(stream), defaultExt: ".otf")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in TtfFormatDescriptor.BuildEntries(Read(stream), defaultExt: ".otf")) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.EntryName, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.EntryName, entry.Bytes);
    }
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
