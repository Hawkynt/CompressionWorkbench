#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Read-only descriptor for Tahoe-LAFS share buckets — single on-disk
/// share files emitted by a Tahoe-LAFS storage server. Each share holds
/// capability-encrypted ciphertext (one of N Reed-Solomon shares; K
/// needed to reconstruct). Detection by the 4-byte big-endian version
/// prefix at offset 0 (0x00000001 immutable, 0x00000002 mutable). The
/// share payload is surfaced as a single opaque ciphertext entry —
/// decryption requires the read-cap and is out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/tahoe-lafs/tahoe-lafs</c> — canonical implementation — share-file layout lives in the source docs</description></item>
///   <item><description><c>https://tahoe-lafs.org/</c> — project home</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Tahoe-LAFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class TahoeLafsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "TahoeLafs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Tahoe-LAFS share";
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
public string DefaultExtension => ".tahoe-share";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".tahoe-share", ".share"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 4-byte big-endian share version at offset 0.
    new([0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.55),
    new([0x00, 0x00, 0x00, 0x02], Offset: 0, Confidence: 0.55),
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
public string Description => "Tahoe-LAFS share bucket — capability-encrypted Reed-Solomon share, surfaced opaque.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TahoeLafsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TahoeLafsReader(stream);
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
    => throw new NotSupportedException("TahoeLafs read-only — defragmentation requires a writer.");

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("TahoeLafs read-only — defragmentation requires a writer.");
}
