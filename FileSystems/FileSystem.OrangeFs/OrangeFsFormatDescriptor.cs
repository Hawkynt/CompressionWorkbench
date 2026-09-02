#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OrangeFs;

/// <summary>
/// Read-only descriptor for OrangeFS / PVFS2 DBPF (Direct Block Pool
/// Format) storage-object files. PVFS2 is a parallel distributed FS, but
/// its server-side <c>bstream-XX</c> objects are single files starting
/// with a 4-byte ASCII tag (<c>"PVFS"</c> classic, <c>"OGFP"</c>
/// OrangeFS-native) followed by version, datastream-type, and object-size
/// fields. The contained object payload is surfaced as a single opaque
/// entry — semantic resolution requires cluster <c>fs.conf</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/waltligon/orangefs</c> — official PVFS/OrangeFS repository (DBPF storage layer)</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/orangefs.html</c> — Linux kernel client documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OrangeFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class OrangeFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "OrangeFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "OrangeFS / PVFS2 DBPF";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".orangefs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".orangefs", ".pvfs", ".bstream"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "PVFS" at offset 0 (classic PVFS2 DBPF).
    new("PVFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // "OGFP" at offset 0 (OrangeFS-native DBPF).
    new("OGFP"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
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
public string Description => "OrangeFS / PVFS2 DBPF — stub: header-only, opaque storage-object payload.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new OrangeFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new OrangeFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException("OrangeFs read-only — defragmentation requires a writer.");

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("OrangeFs read-only — defragmentation requires a writer.");
}
