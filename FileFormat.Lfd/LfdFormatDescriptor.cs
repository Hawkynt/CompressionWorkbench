#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lfd;

public sealed class LfdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Lfd";
  public string DisplayName => "LucasArts LFD";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lfd";
  public IReadOnlyList<string> Extensions => [".lfd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No magic bytes — LFD has no global header. Detection is by extension only; the reader
  // validates plausibility (header sizes, payload bounds) and throws on garbage.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lfd", "LFD")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LucasArts X-Wing / TIE Fighter resource bundle";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LfdReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.DisplayName, e.Size, e.Size, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LfdReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.DisplayName, files)) continue;
      WriteFile(outputDir, e.DisplayName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LfdWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs)) {
      // Map flat input names to LFD's TYPE.NAME convention. If the name lacks a dot,
      // synthesize a generic "DATA" type so callers can still feed regular files in.
      var (type, resName) = SplitName(name);
      w.AddEntry(type, resName, data);
    }
  }

  private static (string Type, string Name) SplitName(string filename) {
    var stem = Path.GetFileNameWithoutExtension(filename);
    var dot = stem.IndexOf('.');
    if (dot <= 0 || dot >= stem.Length - 1)
      return ("DATA", Truncate(stem, 8));

    var type = Truncate(stem[..dot], 4);
    var name = Truncate(stem[(dot + 1)..], 8);
    return (type, name);
  }

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
