#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sid;

/// <summary>
/// Read-only pseudo-archive descriptor for C64 PSID/RSID tunes. Lists the file as
/// FULL + header metadata + raw C64 program data, without emulating the 6502 or SID.
/// </summary>
public sealed class SidFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Sid";
  public string DisplayName => "C64 SID Tune";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sid";
  public IReadOnlyList<string> Extensions => [".sid"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSID"u8.ToArray(), Confidence: 0.97),
    new("RSID"u8.ToArray(), Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "C64 PSID/RSID tune surfaced as a read-only pseudo-archive (FULL + header " +
    "metadata + raw C64 program data); the 6502 and SID chip are never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = SidDecomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var file = ReadAll(stream);
    foreach (var e in SidDecomposer.Decompose(file)) {
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
