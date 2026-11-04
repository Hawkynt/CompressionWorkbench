using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PeSections;

/// <summary>Shared list/extract plumbing for the PE and TE section views.</summary>
internal static class PeSectionArchive {

  /// <summary>Reads the whole stream into memory (images are bounded by the 32-bit offsets they use).</summary>
  public static byte[] ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Entry names: the region names, with a <c>~N</c> suffix on repeats so two sections that share a
  /// name do not overwrite each other on extraction.
  /// </summary>
  public static List<(string Name, PeSectionItem Item)> Name(IEnumerable<PeSectionItem> items) {
    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var result = new List<(string, PeSectionItem)>();
    foreach (var item in items) {
      var name = item.Name.Length == 0 ? "[unnamed]" : item.Name;
      if (seen.TryGetValue(name, out var n)) {
        seen[name] = n + 1;
        name = $"{name}~{n + 1}";
      } else
        seen[name] = 1;
      result.Add((name, item));
    }
    return result;
  }

  public static List<ArchiveEntryInfo> List(IEnumerable<PeSectionItem> items)
    => Name(items).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Item.Size, e.Item.PhysicalSize, "stored", false, false, null)).ToList();

  public static void Extract(byte[] image, IEnumerable<PeSectionItem> items, string outputDir, string[]? files) {
    foreach (var (name, item) in Name(items)) {
      if (files is { Length: > 0 } && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, image.AsSpan((int)item.Offset, (int)item.Size).ToArray());
    }
  }
}
