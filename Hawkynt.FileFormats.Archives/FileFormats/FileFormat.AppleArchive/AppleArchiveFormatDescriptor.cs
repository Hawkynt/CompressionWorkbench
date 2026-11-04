#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleArchive;

/// <summary>
/// Apple Archive (.aar; the older YAA1 variant too): AA01 item headers with typed fields, plain or
/// in a pbz container (LZFSE, zlib, xz, LZ4, LZBITMAP).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/0xilis/libNeoAppleArchive</c> — libNeoAppleArchive (MIT), converted for the item header layout</description></item>
///   <item><description><c>https://developer.apple.com/documentation/applearchive</c> — Apple's framework documentation; macOS <c>aa</c> is the oracle</description></item>
/// </list>
/// </summary>
public sealed class AppleArchiveFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "AppleArchive";
  public string DisplayName => "Apple Archive";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: the writer's raw/LZFSE/zlib archives are checked by macOS `aa`; edits copy every
  // surviving item byte for byte inside the same container compression.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".aar";
  public IReadOnlyList<string> Extensions => [".aar", ".yaa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AA01"u8.ToArray(), Confidence: 0.80),
    new("YAA1"u8.ToArray(), Confidence: 0.80),
    new("pbze"u8.ToArray(), Confidence: 0.75),
    new("pbzz"u8.ToArray(), Confidence: 0.75),
    new("pbz4"u8.ToArray(), Confidence: 0.75),
    new("pbzb"u8.ToArray(), Confidence: 0.75),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzfse", "LZFSE"), new("zlib", "zlib"), new("raw", "raw")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple Archive (aa): plain or LZFSE/zlib/xz/LZ4/LZBITMAP-compressed; writes raw, LZFSE and zlib";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AppleArchiveReader(stream);
    return r.Entries.Where(e => e.Path.Length > 0).Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size,
      e.Type switch { 'F' => r.Compression, 'D' => "dir", 'L' => "symlink", var t => $"type {t}" },
      e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AppleArchiveReader(stream);
    foreach (var e in r.Entries) {
      if (e.Path.Length == 0) continue;
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path));
      else if (e.Type == 'F') WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new AppleArchiveWriter();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : null);
      if (input.IsDirectory) w.AddDirectory(input.ArchiveName, modified);
      else w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }

    output.Write(w.ToArray(options.MethodName is "raw" or "zlib" ? options.MethodName : "lzfse"));
  }

  /// <summary>Adds (or replaces by path) files; every other item is copied byte for byte.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var files = FilesOnly(inputs).ToList();
    var names = files.Select(f => Normalise(f.Name)).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, e => !names.Contains(e.Path), files.SelectMany(f => {
      var header = AppleArchiveWriter.BuildItem('F', f.Name, 0x1A4, null, f.Data.Length);
      return header.Concat(f.Data);
    }).ToArray());
  }

  /// <summary>Removes items by path; every other item is copied byte for byte.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var names = entryNames.Select(Normalise).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, e => !names.Contains(e.Path), []);
  }

  private static string Normalise(string path) =>
    string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

  private static void Rewrite(Stream archive, Func<AppleArchiveEntry, bool> keep, byte[] appended) {
    var r = new AppleArchiveReader(archive);
    if (r.IsTruncated)
      throw new NotSupportedException("The Apple Archive has an item that does not parse; refusing to edit it.");
    if (r.Compression is not ("raw" or "lzfse" or "zlib"))
      throw new NotSupportedException($"Editing an Apple Archive compressed with {r.Compression} is not supported.");

    using var plain = new MemoryStream();
    for (var i = 0; i < r.Entries.Count; ++i)
      if (keep(r.Entries[i])) {
        var (offset, length) = r.ItemRanges[i];
        plain.Write(r.Plain.Slice(offset, length));
      }

    plain.Write(appended);
    var bytes = AppleArchiveWriter.Wrap(plain.ToArray(), r.Compression);
    archive.Position = 0;
    archive.Write(bytes);
    archive.SetLength(bytes.Length);
    archive.Flush();
  }
}
