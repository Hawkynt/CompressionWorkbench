#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes an OpenType Collection (.otc) as an archive of its member fonts.
/// The container format is identical to .ttc (same 'ttcf' magic); only the default
/// extension and the typical inner sfnt version ('OTTO') differ.
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
  public string Description => "OpenType Collection; each member font extractable as standalone .otf/.ttf.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var members = Read(stream);
    var entries = new List<ArchiveEntryInfo>(members.Count);
    foreach (var m in members)
      entries.Add(new ArchiveEntryInfo(
        Index: m.Index,
        Name: $"font_{m.Index:D3}{m.Extension}",
        OriginalSize: m.Data.Length,
        CompressedSize: m.Data.Length,
        Method: "stored",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var m in Read(stream)) {
      var name = $"font_{m.Index:D3}{m.Extension}";
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, name, m.Data);
    }
  }

  private static List<TtcReader.Member> Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return new TtcReader().Read(ms.ToArray());
  }
}
