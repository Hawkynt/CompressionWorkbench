#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Xp3;

/// <summary>
/// KiriKiri XP3 — the resource archive of the KiriKiri 2 / KiriKiri Z (TVP) visual-novel engines.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://krkrz.github.io/</c> — KiriKiri Z, whose engine and the KiriKiri 2 Releaser are the oracles</description></item>
///   <item><description><c>https://github.com/morkt/GARbro</c> — GARbro (MIT), <c>ArcFormats/KiriKiri/ArcXP3.cs</c>, from which the reader is converted</description></item>
/// </list>
/// </summary>
public sealed class Xp3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Xp3";
  public string DisplayName => "KiriKiri XP3";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xp3";
  public IReadOnlyList<string> Extensions => [".xp3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(Xp3Reader.Magic.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "zlib"), new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "KiriKiri (TVP) engine archive: stored and zlib members, raw or compressed index, the 2.32 header. " +
    "Create options: method store|deflate, index=raw|zlib, version=1|2. Game-specific encryption is not decoded: " +
    "members failing their Adler-32 check are refused.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Xp3Reader(stream);
    var list = new List<ArchiveEntryInfo>(r.Entries.Count);
    foreach (var e in r.Entries) {
      if (e.IsBanner) continue;
      list.Add(new ArchiveEntryInfo(list.Count, e.Name, e.OriginalSize, e.ArchivedSize, MethodOf(e), false, false, null));
    }
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Xp3Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsBanner) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var compress = (options.MethodName ?? "deflate").ToLowerInvariant() switch {
      "deflate" or "zlib" => true,
      "store" or "stored" => false,
      var m => throw new NotSupportedException($"XP3 has no method '{m}'; use deflate or store."),
    };
    var version = (options.GetString("version") ?? "2") switch {
      "1" => 1,
      "2" => 2,
      var v => throw new ArgumentException($"XP3 header version '{v}' is not 1 or 2."),
    };
    var compressIndex = (options.GetString("index") ?? "zlib").ToLowerInvariant() switch {
      "zlib" => true,
      "raw" => false,
      var i => throw new ArgumentException($"XP3 index encoding '{i}' is not raw or zlib."),
    };
    var entries = FilesOnly(inputs).Select(f => Xp3WriteEntry.FromData(f.Name.Replace('\\', '/'), f.Data, compress)).ToList();
    Xp3Writer.Write(output, entries, version, compressIndex);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, entries => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var n = name.Replace('\\', '/');
        var replacement = Xp3WriteEntry.FromData(n, data, compress: true);
        var at = entries.FindIndex(e => string.Equals(e.Name, n, StringComparison.Ordinal));
        if (at >= 0) entries[at] = replacement with { Flags = entries[at].Flags };
        else entries.Add(replacement);
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames, StringComparer.Ordinal);
    Rewrite(archive, entries => entries.RemoveAll(e => drop.Contains(e.Name)));
  }

  private static void Rewrite(Stream archive, Action<List<Xp3WriteEntry>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var r = new Xp3Reader(archive);
    if (r.ForeignChunks.Count > 0)
      throw new NotSupportedException("XP3: the index carries engine-specific name tables; rewriting it would break them.");
    var entries = r.Entries.Select(e => Xp3WriteEntry.FromExisting(r, e)).ToList();
    edit(entries);
    using var rebuilt = new MemoryStream();
    Xp3Writer.Write(rebuilt, entries, r.Version, r.IndexCompressed);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private static string MethodOf(Xp3Entry e) {
    var compressed = e.Segments.Count(s => s.IsCompressed);
    return compressed == 0 ? "stored" : compressed == e.Segments.Count ? "zlib" : "zlib+stored";
  }
}
