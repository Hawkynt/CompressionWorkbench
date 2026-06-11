#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gbs;

/// <summary>
/// Read-only pseudo-archive descriptor for Game Boy Sound files. Lists the file as
/// FULL + header metadata + raw code+data blob, without emulating any hardware.
/// </summary>
public sealed class GbsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Gbs";
  public string DisplayName => "Game Boy Sound";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gbs";
  public IReadOnlyList<string> Extensions => [".gbs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GBS"u8.ToArray(), Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Game Boy Sound file surfaced as a read-only pseudo-archive (FULL + header " +
    "metadata + raw code+data blob); the Game Boy CPU and APU are never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = GbsDecomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var file = ReadAll(stream);
    foreach (var e in GbsDecomposer.Decompose(file)) {
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
