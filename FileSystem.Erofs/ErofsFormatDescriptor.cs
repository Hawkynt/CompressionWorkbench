#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Erofs;

/// <summary>
/// Read-only descriptor for EROFS images. Write support is intentionally absent —
/// generating EROFS is the job of <c>mkfs.erofs</c>; our role is the triage /
/// extraction side (typical user: pulled an Android system.img, needs to list it).
/// </summary>
public sealed class ErofsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Erofs";
  public string DisplayName => "EROFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".erofs";
  public IReadOnlyList<string> Extensions => [".erofs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Magic sits at offset 1024 (start of superblock). Value is 0xE0F5E2E0 stored
    // little-endian, so the on-disk byte sequence is E0 E2 F5 E0.
    new([0xE0, 0xE2, 0xF5, 0xE0], Offset: 1024, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Android read-only compressed filesystem; uncompressed + inline inode layouts.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = OpenReader(stream);
    var result = new List<ArchiveEntryInfo>(reader.Entries.Count);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var e = reader.Entries[i];
      result.Add(new ArchiveEntryInfo(
        Index: i,
        Name: e.Path,
        OriginalSize: e.Size,
        CompressedSize: e.Size,
        Method: "stored",
        IsDirectory: e.IsDirectory,
        IsEncrypted: false,
        LastModified: null));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = OpenReader(stream);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Path, files))
        continue;
      try {
        var data = reader.ExtractFile(e);
        FormatHelpers.WriteFile(outputDir, e.Path, data);
      } catch (NotSupportedException) {
        // Compressed-inode entry we can't decode yet; write an empty placeholder so
        // the user sees it exists but the content is unavailable.
        FormatHelpers.WriteFile(outputDir, e.Path + ".compressed-unsupported", []);
      }
    }
  }

  private static ErofsReader OpenReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new ErofsReader(ms.ToArray());
  }
}
