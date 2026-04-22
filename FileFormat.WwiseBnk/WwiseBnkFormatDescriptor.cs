#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WwiseBnk;

public sealed class WwiseBnkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "WwiseBnk";
  public string DisplayName => "Wwise SoundBank";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bnk";
  public IReadOnlyList<string> Extensions => [".bnk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("BKHD"u8.ToArray(), Confidence: 0.9)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bnk", "Wwise SoundBank")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Audiokinetic Wwise SoundBank container (BKHD/DIDX/DATA/HIRC)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WwiseBnkReader(stream);
    var list = new List<ArchiveEntryInfo>();
    int idx = 0;
    list.Add(new ArchiveEntryInfo(idx++, "FULL.bnk", stream.Length, stream.Length, "Stored", false, false, null));
    list.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));
    if (r.HircObjects.Count > 0)
      list.Add(new ArchiveEntryInfo(idx++, "hirc_objects.txt", 0, 0, "Stored", false, false, null));
    foreach (var w in r.Wems)
      list.Add(new ArchiveEntryInfo(idx++, $"wems/{w.WemId}.wem", w.Size, w.Size, "Stored", false, false, null));
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WwiseBnkReader(stream);

    if (files == null || MatchesFilter("FULL.bnk", files)) {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      WriteFile(outputDir, "FULL.bnk", ms.ToArray());
    }

    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[wwise_bnk]");
      sb.AppendLine($"version={r.BankVersion}");
      sb.AppendLine($"bank_id=0x{r.BankId:X8}");
      sb.AppendLine($"hirc_object_count={r.HircObjects.Count}");
      sb.AppendLine($"wem_count={r.Wems.Count}");
      sb.AppendLine($"chunks={string.Join(",", r.Chunks.Keys)}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    if (r.HircObjects.Count > 0 && (files == null || MatchesFilter("hirc_objects.txt", files))) {
      var sb = new StringBuilder();
      foreach (var h in r.HircObjects)
        sb.AppendLine($"{h.Type} 0x{h.Id:X8} {h.Size}");
      WriteFile(outputDir, "hirc_objects.txt", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    foreach (var w in r.Wems) {
      var name = $"wems/{w.WemId}.wem";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ExtractWem(w));
    }
  }
}
