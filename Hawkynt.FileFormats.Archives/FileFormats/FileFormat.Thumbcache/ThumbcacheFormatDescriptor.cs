#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Thumbcache;

/// <summary>
/// Windows Explorer thumbnail cache database (<c>thumbcache_32.db</c>, <c>thumbcache_256.db</c>, …,
/// signature <c>CMMM</c>), Windows Vista through Windows 11. Every cached thumbnail is an entry,
/// named after its identifier, extracted as the stored BMP/JPEG/PNG bytes.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/libyal/documentation/blob/main/Windows%20Explorer%20Thumbnail%20Cache%20database%20format.asciidoc</c> — Joachim Metz's format description</description></item>
///   <item><description><c>https://github.com/fox-it/dissect.thumbcache</c> — Fox-IT's reader (AGPL-3.0); black-box oracle only</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Claims no extension — <c>.db</c> belongs to the Thumbs.db and SQLite descriptors — and is found by
/// its <c>CMMM</c> signature. The <c>thumbcache_idx.db</c> index (<c>IMMM</c>) holds no thumbnails and
/// is refused.
/// </remarks>
public sealed class ThumbcacheFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Thumbcache";
  public string DisplayName => "Windows thumbnail cache (thumbcache_*.db)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => "";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("CMMM"u8.ToArray(), Confidence: 0.6)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows Vista-11 Explorer thumbnail cache; lists and extracts the cached thumbnails (read-only).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    return Named(data).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Entry.DataSize, e.Entry.DataSize, "stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var (name, e) in Named(data)) {
      if (files is { Length: > 0 } && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, data.AsSpan((int)e.DataOffset, e.DataSize).ToArray());
    }
  }

  /// <summary>Entries with their file names; a repeated name gets a <c>~N</c> suffix before the extension.</summary>
  public static List<(string Name, ThumbcacheEntry Entry)> Named(byte[] data) {
    var r = new ThumbcacheReader(data);
    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var result = new List<(string, ThumbcacheEntry)>(r.Entries.Count);
    foreach (var e in r.Entries) {
      var name = ThumbcacheReader.EntryName(e, data.AsSpan((int)e.DataOffset, e.DataSize));
      if (seen.TryGetValue(name, out var n)) {
        seen[name] = n + 1;
        name = $"{Path.GetFileNameWithoutExtension(name)}~{n + 1}{Path.GetExtension(name)}";
      } else
        seen[name] = 1;
      result.Add((name, e));
    }
    return result;
  }

  private static byte[] ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
