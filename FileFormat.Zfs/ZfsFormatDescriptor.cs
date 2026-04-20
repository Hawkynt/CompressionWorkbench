#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zfs;

public sealed class ZfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Zfs";
  public string DisplayName => "ZFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".zfs";
  public IReadOnlyList<string> Extensions => [".zfs", ".zpool"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZFS pool/filesystem image (read-only, detection only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Detection-only — full ZFS MOS/DSL/ZAP/dnode traversal not yet implemented.
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: produces a valid ZFS label with uberblock. File data embedded but
    // not yet recoverable through our reader (detection-only). Full ZAP/dnode
    // support is tracked for future work.
    byte[]? embedded = null;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      embedded = File.ReadAllBytes(i.FullPath);
      break;
    }
    new ZfsWriter().WriteTo(output, embedded);
  }
}
