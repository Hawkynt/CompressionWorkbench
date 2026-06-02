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
public sealed class ApkNativeLibsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

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

  /// <summary>
  /// Opens a single rewritten native-lib entry as a bounded stream. The
  /// caller's <paramref name="entryName"/> uses the synthetic
  /// <c>native_libs/&lt;abi&gt;/*.so</c> view; we reverse the rewrite back
  /// to the underlying <c>lib/&lt;abi&gt;/*.so</c> ZIP entry, decode it, and
  /// wrap the bytes in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's uncompressed length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    // Reverse the rewrite: "native_libs/arm64-v8a/libfoo.so" → "lib/arm64-v8a/libfoo.so".
    var innerName = entryName.StartsWith("native_libs/", StringComparison.Ordinal)
      ? "lib/" + entryName.Substring("native_libs/".Length)
      : entryName;
    using var r = new ZipReader(archive, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!IsNativeLib(e.FileName)) continue;
      if (!string.Equals(e.FileName, innerName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  private static bool IsNativeLib(string path) =>
    path.StartsWith("lib/", StringComparison.Ordinal) &&
    path.EndsWith(".so", StringComparison.OrdinalIgnoreCase);

  // "lib/arm64-v8a/libfoo.so" → "native_libs/arm64-v8a/libfoo.so"
  private static string Rewrite(string path) => "native_libs/" + path.Substring(4);
}
