#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nsf;

/// <summary>
/// Read-only pseudo-archive descriptor for NES Sound Format files. Lists the file
/// as FULL + header metadata + raw 6502 program data, without emulating any chip.
/// </summary>
public sealed class NsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Nsf";
  public string DisplayName => "NES Sound Format";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nsf";
  public IReadOnlyList<string> Extensions => [".nsf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4E, 0x45, 0x53, 0x4D, 0x1A], Confidence: 0.98), // "NESM" + 0x1A
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NES Sound Format file surfaced as a read-only pseudo-archive (FULL + header " +
    "metadata + raw 6502 program data); the 6502 and expansion chips are never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var file = ReadAll(stream);
    var entries = NsfDecomposer.Decompose(file);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var file = ReadAll(stream);
    foreach (var e in NsfDecomposer.Decompose(file)) {
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
