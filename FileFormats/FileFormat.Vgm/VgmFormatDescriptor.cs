#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vgm;

/// <summary>
/// Read-only pseudo-archive descriptor for VGM (Video Game Music) register dumps,
/// including the gzip-wrapped .vgz variant. Lists the file as FULL + header
/// metadata + GD3 tag + raw command stream, without emulating any sound chip.
/// </summary>
public sealed class VgmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Vgm";
  public string DisplayName => "VGM (Video Game Music)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vgm";
  public IReadOnlyList<string> Extensions => [".vgm", ".vgz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // "Vgm " marks an uncompressed dump; .vgz files start with the gzip magic and
  // carry the "Vgm " header inside the deflate stream, so the gzip signature is
  // intentionally low-confidence (Gzip owns it) and detection relies on extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Vgm "u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "VGM / VGZ sound-chip register dump surfaced as a read-only pseudo-archive " +
    "(FULL + header metadata + GD3 tag + raw command stream); .vgz is gunzipped " +
    "only to read the header, FULL keeps the original bytes. Never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = VgmDecomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var file = ReadAll(stream);
    foreach (var e in VgmDecomposer.Decompose(file)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
