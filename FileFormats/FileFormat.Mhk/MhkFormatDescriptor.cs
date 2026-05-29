#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mhk;

public sealed class MhkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new MhkReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.DisplayName);
    }
  }

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

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    // Capture (type, id, name) tuples up-front so the rebuild preserves the full
    // resource identity. Pass through DefragRebuilder using DisplayName as the
    // sortable key and stash the side-channel info in a parallel dictionary.
    var meta = new Dictionary<string, (string Type, ushort Id, string? Name)>(StringComparer.Ordinal);

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MhkReader(stream);
        var list = new List<(string, byte[])>(r.Entries.Count);
        foreach (var e in r.Entries) {
          meta[e.DisplayName] = (e.Type, e.Id, e.Name);
          list.Add((e.DisplayName, r.Extract(e)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new MhkWriter(ms, leaveOpen: true)) {
          foreach (var (display, data) in files) {
            if (meta.TryGetValue(display, out var info))
              w.AddEntry(info.Type, info.Id, info.Name, data);
          }
        }
        return ms.ToArray();
      });
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
