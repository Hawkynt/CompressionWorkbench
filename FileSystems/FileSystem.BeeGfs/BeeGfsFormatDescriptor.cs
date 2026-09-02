#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.BeeGfs;

/// <summary>
/// Stage 0 detection-only descriptor for BeeGFS chunk-file / dump tags.
/// Surfaces only a synthetic <c>metadata.ini</c> and the raw image bytes;
/// no real file-walk is attempted because a BeeGFS volume has no
/// standalone on-disk image.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.beegfs.io</c> — official BeeGFS site and documentation portal</description></item>
///   <item><description><c>https://github.com/ThinkParQ/beegfs</c> — BeeGFS source (ThinkParQ)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/BeeGFS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class BeeGfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BeeGfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "BeeGFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".beegfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".beegfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "BeeGFS" (6 bytes) at offset 0.
    new("BeeGFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // ASCII "BeeG" (0x42656547 BE) at offset 0 — short tag form.
    new("BeeG"u8.ToArray(), Offset: 0, Confidence: 0.85),
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
public string Description =>
    "BeeGFS — Stage 0 detection only. Distributed parallel cluster FS (Fraunhofer, ex-FhGFS): " +
    "the namespace lives across metadata-target processes (per-inode files + xattrs on a regular " +
    "Linux FS like ext4/xfs), file payload lives across storage-target processes (chunk files in " +
    "a hashed dir layout on a regular Linux FS). No standalone on-disk image — a volume cannot be " +
    "represented as a single byte-stream. R/O promotion would require traversing a live metadata " +
    "target directory tree + resolving the stripe pattern + target group map via beegfs-meta. " +
    "Magic 'BeeGFS' / 0x42656547 at offset 0 of a chunk-file or dump-tool output is the only " +
    "single-stream surface available.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BeeGfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BeeGfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new BeeGfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"BeeGFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
