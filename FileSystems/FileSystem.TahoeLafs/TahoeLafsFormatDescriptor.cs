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
/// </summary>
public sealed class TahoeLafsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "TahoeLafs";
  public string DisplayName => "Tahoe-LAFS share";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract;
  public string DefaultExtension => ".tahoe-share";
  public IReadOnlyList<string> Extensions => [".tahoe-share", ".share"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 4-byte big-endian share version at offset 0.
    new([0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.55),
    new([0x00, 0x00, 0x00, 0x02], Offset: 0, Confidence: 0.55),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Tahoe-LAFS share bucket — capability-encrypted Reed-Solomon share, surfaced opaque.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TahoeLafsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TahoeLafsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("TahoeLafs read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("TahoeLafs read-only — defragmentation requires a writer.");
}
