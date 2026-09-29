#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Pack200;

/// <summary>
/// Format descriptor for Pack200 (JSR-200) archives — the compressed representation
/// of a set of Java <c>.class</c> files used by <c>pack200</c>/<c>unpack200</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.oracle.com/javase/8/docs/technotes/guides/pack200/pack-spec.html</c> — "Pack200: A Packed Class Deployment Format" (the JSR-200 band/coding spec)</description></item>
///   <item><description><c>https://jcp.org/en/jsr/detail?id=200</c> — JSR 200, "Network Transfer Format for Java Archives"</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Pack200</c> — format overview (removed from the JDK in Java 14)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The archive is presented as a read-only class-name listing. Listing recovers each
/// class's internal name from a subset of Pack200 bands. Full <c>.class</c> reconstruction
/// and archive creation are not implemented, so extraction is deliberately not advertised
/// or simulated by writing summary text in place of class files.
/// </remarks>
public sealed class Pack200FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Pack200";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Pack200";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".pack";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pack"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [".pack.gz"];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Pack200Reader.Magic, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pack200", "Pack200")];
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
  public string Description => "Pack200 Java class archive (JSR-200)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    Pack200Segment seg;
    try {
      seg = new Pack200Reader().Read(stream);
    } catch (InvalidDataException) {
      return [];
    }

    var lastMod = seg.ModTime > 0
      ? DateTimeOffset.FromUnixTimeSeconds(seg.ModTime).UtcDateTime
      : (DateTime?)null;

    var result = new List<ArchiveEntryInfo>(seg.ClassNames.Count);
    for (var i = 0; i < seg.ClassNames.Count; ++i) {
      var name = seg.ClassNames[i];
      var fileName = name.EndsWith(".class", StringComparison.Ordinal) ? name : name + ".class";
      result.Add(new ArchiveEntryInfo(i, fileName, 0, 0, "pack200", false, false, lastMod,
        Kind: seg.Status == Pack200DecodeStatus.Full ? "class" : "class (name unresolved)"));
    }
    return result;
  }

  /// <summary>
  /// Pack200 class-file reconstruction is not implemented.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    throw new NotSupportedException("Pack200 extraction requires full class-file reconstruction, which this descriptor does not implement.");
  }
}
