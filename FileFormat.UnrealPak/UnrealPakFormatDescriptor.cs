#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UnrealPak;

/// <summary>
/// Unreal Engine 4/5 <c>.pak</c> archive. Entries are stored or zlib-compressed and are listed
/// through an index block at the end of the file. Encrypted PAKs and Oodle-compressed entries
/// are listed but not extracted.
/// </summary>
public sealed class UnrealPakFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "UnrealPak";
  public string DisplayName => "Unreal Pak";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pak";
  public IReadOnlyList<string> Extensions => [".pak", ".ucas", ".utoc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic lives at the end of the file, not the beginning — rely on extension-based detection
  // (the generic Quake .pak descriptor has no magic either, so this doesn't conflict).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("zlib", "Zlib"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unreal Engine 4/5 PAK archive (stored + zlib entries).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = new UnrealPakReader(AsSeekable(stream));
    var entries = new List<ArchiveEntryInfo>(reader.Entries.Count);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var e = reader.Entries[i];
      entries.Add(new ArchiveEntryInfo(
        Index: i,
        Name: CombinePath(reader.MountPoint, e.Path),
        OriginalSize: e.UncompressedSize,
        CompressedSize: e.Size,
        Method: e.CompressionMethod == 0 ? "Stored" : "Zlib",
        IsDirectory: false,
        IsEncrypted: e.IsEncrypted,
        LastModified: null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new UnrealPakReader(AsSeekable(stream));
    foreach (var e in reader.Entries) {
      var fullPath = CombinePath(reader.MountPoint, e.Path);
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(fullPath, files))
        continue;
      if (e.UnsupportedReason != null || e.IsEncrypted)
        continue; // skip — listing already surfaces the entry with IsEncrypted=true
      var data = reader.Extract(e);
      FormatHelpers.WriteFile(outputDir, fullPath, data);
    }
  }

  private static string CombinePath(string mount, string path) {
    // Mount points are typically "../../../Game/" — strip the leading "../" chain so the
    // extracted tree is rooted at the project name.
    var m = mount.Replace('\\', '/');
    while (m.StartsWith("../")) m = m[3..];
    if (m.StartsWith('/')) m = m.TrimStart('/');
    m = m.TrimEnd('/');
    return m.Length == 0 ? path : m + "/" + path.TrimStart('/');
  }

  private static Stream AsSeekable(Stream stream) {
    if (stream.CanSeek) return stream;
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    ms.Position = 0;
    return ms;
  }
}
