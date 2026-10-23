using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.NowCompress;

/// <summary>Now Compress archive (Now Software, Macintosh). Read-only.</summary>
public sealed class NowCompressFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>Suffix under which a file's resource fork is listed and extracted.</summary>
  public const string ResourceForkSuffix = ".rsrc";

  /// <inheritdoc />
  public string Id => "NowCompress";
  /// <inheritdoc />
  public string DisplayName => "Now Compress";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc />
  public string DefaultExtension => ".now";
  // .sea is shared with every Mac self-extractor; the archive is recognised by content.
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".now"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x02, 0x00, 0x60, 0x00, 0x01], Confidence: 0.6, Mask: [0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF]),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("now", "Now Compress")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description => "Now Compress Macintosh archive (LZSS / Huffman blocks); read-only";

  private static IEnumerable<(NowCompressEntry Entry, bool Resource, string Name)> Items(List<NowCompressEntry> entries) {
    foreach (var e in entries) {
      if (e.IsDirectory) { yield return (e, false, e.Path); continue; }
      if (e.DataSize > 0 || e.ResourceSize == 0) yield return (e, false, e.Path);
      if (e.ResourceSize > 0) yield return (e, true, e.Path + ResourceForkSuffix);
    }
  }

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => Items(NowCompressArchive.Read(stream)).Select((x, i) => new ArchiveEntryInfo(i, x.Name,
      x.Entry.IsDirectory ? 0 : x.Resource ? x.Entry.ResourceSize : x.Entry.DataSize, -1,
      x.Entry.IsDirectory ? "" : "Now Compress", x.Entry.IsDirectory, false, x.Entry.LastModified)).ToList();

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (e, resource, name) in Items(NowCompressArchive.Read(stream))) {
      if (e.IsDirectory || files is { Length: > 0 } && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, resource ? e.Resource : e.Data);
    }
  }

  /// <inheritdoc />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    foreach (var (e, resource, name) in Items(NowCompressArchive.Read(archive)))
      if (!e.IsDirectory && string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
        return resource ? e.Resource : e.Data;
    return [];
  }
}
