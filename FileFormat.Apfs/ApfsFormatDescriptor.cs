#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Apfs;

public sealed class ApfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Apfs";
  public string DisplayName => "APFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".apfs";
  public IReadOnlyList<string> Extensions => [".apfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("NXSB"u8.ToArray(), Offset: 32, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple File System image (read-only, listing only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ApfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Listing-only — full APFS extent resolution not yet implemented.
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    new ApfsWriter().WriteTo(output);
  }
}
