#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mhk;

public sealed class MhkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Mhk";
  public string DisplayName => "Cyan Mohawk";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mhk";
  public IReadOnlyList<string> Extensions => [".mhk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MHWK"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mhk", "Mohawk")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Cyan Mohawk archive (Myst / Riven / Cosmic Osmo / Living Books)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MhkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.DisplayName, e.Size, e.Size, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MhkReader(stream);
    foreach (var e in r.Entries) {
      // Surface entries as flat files named by display key + .bin so multi-tag archives don't collide.
      var fileName = e.DisplayName + ".bin";
      if (files != null && !MatchesFilter(fileName, files)) continue;
      WriteFile(outputDir, fileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MhkWriter(output, leaveOpen: true);
    var autoId = (ushort)1000;
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs)) {
      var (type, id, resName) = SplitInputName(name, ref autoId);
      w.AddEntry(type, id, resName, data);
    }
  }

  // Convention for round-tripping flat input files: map filenames of the form
  // "TYPE_id_name.ext" or "TYPE_id.ext" back to (type, id, name). When a file's
  // stem doesn't fit the convention we fall back to a "tDAT" type with an auto-incrementing id
  // so callers can still create archives from arbitrary inputs.
  private static (string Type, ushort Id, string? Name) SplitInputName(string filename, ref ushort autoId) {
    var stem = Path.GetFileNameWithoutExtension(filename);
    var firstUnderscore = stem.IndexOf('_');
    if (firstUnderscore == MhkConstants.TypeTagSize) {
      var type = stem[..MhkConstants.TypeTagSize];
      if (IsAscii(type)) {
        var rest = stem[(firstUnderscore + 1)..];
        var secondUnderscore = rest.IndexOf('_');
        var idText = secondUnderscore < 0 ? rest : rest[..secondUnderscore];
        if (ushort.TryParse(idText, out var id)) {
          var resName = secondUnderscore < 0 ? null : rest[(secondUnderscore + 1)..];
          return (type, id, string.IsNullOrEmpty(resName) ? null : resName);
        }
      }
    }
    return ("tDAT", autoId++, null);
  }

  private static bool IsAscii(string s) {
    foreach (var ch in s) {
      if (ch > 0x7F)
        return false;
    }
    return true;
  }
}
