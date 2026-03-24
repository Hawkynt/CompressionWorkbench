#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mpq;

public sealed class MpqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mpq";
  public string DisplayName => "MPQ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpq";
  public IReadOnlyList<string> Extensions => [".mpq"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1A], Confidence: 0.95),
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1B], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mpq", "MPQ")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MpqReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "Compressed" : "Stored", false, e.IsEncrypted, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MpqReader(stream);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      try { WriteFile(outputDir, e.FileName, r.Extract(e)); } catch { }
    }
  }
}
