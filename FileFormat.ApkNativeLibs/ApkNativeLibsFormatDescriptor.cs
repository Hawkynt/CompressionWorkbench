#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ApkNativeLibs;

/// <summary>
/// Alternative view over an Android APK that surfaces only its packaged native
/// libraries (<c>lib/&lt;abi&gt;/*.so</c>) as archive entries under
/// <c>native_libs/&lt;abi&gt;/*.so</c>. Intentionally not registered for magic
/// detection (all magic signatures are zero-confidence); the caller must route
/// here explicitly, e.g. <c>cwb list --format ApkNativeLibs foo.apk</c>.
/// </summary>
public sealed class ApkNativeLibsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "ApkNativeLibs";
  public string DisplayName => "APK native libraries";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".apk";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => []; // explicit-only routing
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Alternative view over an APK exposing only lib/<abi>/*.so native libraries.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    var result = new List<ArchiveEntryInfo>();
    var idx = 0;
    foreach (var e in r.Entries) {
      if (!IsNativeLib(e.FileName)) continue;
      result.Add(new ArchiveEntryInfo(
        idx++, Rewrite(e.FileName), e.UncompressedSize, e.CompressedSize,
        e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (!IsNativeLib(e.FileName)) continue;
      var rewritten = Rewrite(e.FileName);
      if (files != null && !MatchesFilter(rewritten, files)) continue;
      if (e.IsDirectory) continue;
      WriteFile(outputDir, rewritten, r.ExtractEntry(e));
    }
  }

  private static bool IsNativeLib(string path) =>
    path.StartsWith("lib/", StringComparison.Ordinal) &&
    path.EndsWith(".so", StringComparison.OrdinalIgnoreCase);

  // "lib/arm64-v8a/libfoo.so" → "native_libs/arm64-v8a/libfoo.so"
  private static string Rewrite(string path) => "native_libs/" + path.Substring(4);
}
