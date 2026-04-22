#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Zfs;

public sealed class ZfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Zfs";
  public string DisplayName => "ZFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanTest;
  public string DefaultExtension => ".zfs";
  public IReadOnlyList<string> Extensions => [".zfs", ".zpool"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZFS pool/filesystem image (read-only — proper spec-compliant writer requires 4 vdev labels, NVList, uberblock ring, DSL and DMU; tracked in TODO.md)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Detection-only — full ZFS MOS/DSL/ZAP/dnode traversal not yet implemented.
  }
}
