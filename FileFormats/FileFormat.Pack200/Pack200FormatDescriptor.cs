#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

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
/// The archive is presented as a read-only collection whose entries are the classes
/// it defines. Listing recovers each class's internal name by decoding the archive
/// header and constant-pool/class bands; extraction writes a manifest of those names
/// together with the decoded header summary. Full <c>.class</c> byte reconstruction
/// (method/code/bytecode bands) is out of scope and is reported honestly rather than
/// fabricated.
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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
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
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var seg = new Pack200Reader().Read(stream);

    // Header summary — honest report of what was and was not decoded.
    var info = new StringBuilder();
    info.Append("Pack200 (JSR-200) archive\n");
    info.Append($"archive-version: {seg.MajVersion}.{seg.MinVersion}\n");
    info.Append($"options: 0x{seg.Options:X}\n");
    info.Append($"default-class-version: {seg.DefaultClassMajVersion}.{seg.DefaultClassMinVersion}\n");
    info.Append($"utf8-count: {seg.Utf8Count}\n");
    info.Append($"class-pool-count: {seg.ClassPoolCount}\n");
    info.Append($"class-count: {seg.ClassCount}\n");
    info.Append($"resource-file-count: {seg.ResourceFileCount}\n");
    info.Append($"decode-status: {seg.Status}\n");
    if (seg.StatusNote != null)
      info.Append($"decode-note: {seg.StatusNote}\n");
    info.Append("note: class enumeration only; full .class byte reconstruction is not implemented.\n");

    if (files == null || MatchesFilter("pack200-info.txt", files))
      WriteFile(outputDir, "pack200-info.txt", Encoding.UTF8.GetBytes(info.ToString()));

    // Manifest of the class internal names this archive defines.
    var manifest = new StringBuilder();
    foreach (var name in seg.ClassNames)
      manifest.Append(name).Append('\n');
    if (files == null || MatchesFilter("classes.txt", files))
      WriteFile(outputDir, "classes.txt", Encoding.UTF8.GetBytes(manifest.ToString()));
  }
}
