#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rgss;

public sealed class RgssFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Rgss";
  public string DisplayName => "RPG Maker RGSSAD";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rgssad";
  public IReadOnlyList<string> Extensions => [".rgssad", ".rgss2a", ".rgss3a"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 1], Confidence: 0.95),
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 2], Confidence: 0.95),
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 3], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rgss", "RGSSAD")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RPG Maker XP/VX/VX Ace encrypted resource archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RgssReader(stream);
    var list = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", 0, 0, "Stored", false, false, null)
    };
    int idx = 1;
    foreach (var e in r.Entries)
      list.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "XOR", false, true, null));
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RgssReader(stream);

    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rgss]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      if (r.Version == 3)
        sb.AppendLine($"master_key=0x{r.MasterKeyV3:X8}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
