#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MooseFs;

/// <summary>
/// Partial R/O descriptor for MooseFS master-metadata images
/// (<c>metadata.mfs</c>). Surfaces the metadata envelope (signature,
/// counters, section index) and the raw payload bytes of each walked
/// section. Path-tree (NODE/EDGE) and chunk-id (CHNK) bodies are
/// version-specific and require golden samples to decode honestly — the
/// reader makes no claim about their internal structure.
///
/// <para>
/// MooseFS file content lives on chunk servers and is unreachable from a
/// single metadata image. Listing therefore surfaces ONLY synthetic
/// metadata + per-section raw payloads, never POSIX paths.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/moosefs/moosefs</c> — canonical source (master metadata dump/load code)</description></item>
///   <item><description><c>https://moosefs.com/</c> — vendor site and documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Moose_File_System</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class MooseFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "MooseFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MooseFS";
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
public string DefaultExtension => ".mfsm";
  // Note: ".mfs" intentionally NOT claimed — collides with Macintosh File System (FileSystem.Mfs).
  // MooseFS detection is by magic 'MFSM' at offset 0; the ".mfsm" extension is MooseFS-specific.
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mfsm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "MFSM" at offset 0 — MooseFS master metadata tag.
    new("MFSM"u8.ToArray(), Offset: 0, Confidence: 0.90),
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
    "MooseFS — partial R/O of master metadata envelope (signature + section " +
    "index). File content lives on chunk servers and is unreachable from a " +
    "single metadata image; only metadata.ini + raw image + per-section " +
    "raw payloads are surfaced.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MooseFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MooseFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new MooseFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"MooseFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
