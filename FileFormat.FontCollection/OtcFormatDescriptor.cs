#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes an OpenType Collection (.otc) — same container format as .ttc (same
/// 'ttcf' magic) — as <c>FULL.otc</c> + <c>metadata.ini</c> +
/// <c>fonts/&lt;i&gt;_&lt;name&gt;.{otf,ttf}</c> + <c>glyphs/&lt;i&gt;_&lt;name&gt;/U+XXXX.svg</c>.
/// CFF-outline members are recognised but produce no glyph SVGs (recorded in metadata).
/// </summary>
public sealed class OtcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Otc";
  public string DisplayName => "OTC (OpenType collection)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".otc";
  public IReadOnlyList<string> Extensions => [".otc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic overlaps with TTC ('ttcf'). Extension drives disambiguation.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "OpenType Collection; FULL + per-member fonts + per-glyph SVG.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    TtcFormatDescriptor.BuildEntries(Read(stream), fullName: "FULL.otc")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in TtcFormatDescriptor.BuildEntries(Read(stream), fullName: "FULL.otc")) {
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
