#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Gettext;

/// <summary>
/// Exposes a gettext .po text catalog as an archive of per-message text files.
/// Matches <see cref="MoFormatDescriptor"/>'s entry layout; only the source-parsing
/// path differs.
/// </summary>
public sealed class PoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Po";
  public string DisplayName => "PO (gettext text catalog)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".po";
  public IReadOnlyList<string> Extensions => [".po", ".pot"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Text gettext message catalog; each msgid extractable as a text file.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    GettextEntryHelper.ToArchiveEntries(Read(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    GettextEntryHelper.Extract(Read(stream), outputDir, files);

  private static List<CatalogEntry> Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return new PoReader().Read(ms.ToArray());
  }
}
