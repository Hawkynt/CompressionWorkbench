#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Gettext;

/// <summary>
/// Exposes a gettext .po text catalog as an archive of per-message text files.
/// Matches <see cref="MoFormatDescriptor"/>'s entry layout; only the source-parsing
/// path differs.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.gnu.org/software/gettext/manual/html_node/PO-Files.html</c> — GNU gettext manual — PO file syntax</description></item>
///   <item><description><c>https://www.gnu.org/software/gettext/</c> — GNU gettext project</description></item>
/// </list>
/// </summary>
public sealed class PoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Po";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PO (gettext text catalog)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".po";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".po", ".pot"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Text gettext message catalog; each msgid extractable as a text file.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    GettextEntryHelper.ToArchiveEntries(Read(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    GettextEntryHelper.Extract(Read(stream), outputDir, files);

  private static List<CatalogEntry> Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return new PoReader().Read(ms.ToArray());
  }
}
