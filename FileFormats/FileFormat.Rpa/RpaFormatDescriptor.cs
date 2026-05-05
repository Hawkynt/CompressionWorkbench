#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rpa;

public sealed class RpaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Rpa";
  public string DisplayName => "Ren'Py Archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rpa";
  public IReadOnlyList<string> Extensions => [".rpa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RPA-2.0 "u8.ToArray(), Confidence: 0.95),
    new("RPA-3.0 "u8.ToArray(), Confidence: 0.95),
    new("RPA-3.2 "u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rpa", "Ren'Py RPA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Ren'Py visual-novel resource archive (pickle-indexed, zlib header)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RpaReader(stream);
    var list = new List<ArchiveEntryInfo>();
    int idx = 0;

    // Always surface passthrough + metadata
    list.Add(new ArchiveEntryInfo(idx++, "FULL.rpa", stream.Length, stream.Length, "Stored", false, false, null));
    list.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));

    foreach (var e in r.Entries)
      list.Add(new ArchiveEntryInfo(idx++, e.Path, e.Length, e.Length, "Stored", false, false, null));
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RpaReader(stream);

    // FULL passthrough
    if (files == null || MatchesFilter("FULL.rpa", files)) {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      WriteFile(outputDir, "FULL.rpa", ms.ToArray());
    }

    // metadata.ini
    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rpa]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"index_offset=0x{r.IndexOffset:X}");
      if (r.XorKey != 0)
        sb.AppendLine($"xor_key=0x{r.XorKey:X8}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      sb.AppendLine($"pickle_parsed={r.PickleParsed}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // TODO: if pickle parse is fragile on future RPA revisions, the reader sets PickleParsed=false
    //       and we only surface FULL + metadata. Known to work on RPA-2.0 / 3.0 / 3.2 protocol-2 pickles.
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }
}
