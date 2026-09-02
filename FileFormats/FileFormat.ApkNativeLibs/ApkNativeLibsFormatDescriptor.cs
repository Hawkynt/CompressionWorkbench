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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.android.com/ndk/guides/abis</c> — Android ABI management — defines the per-ABI native-library directory layout inside an APK</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apk_(file_format)</c> — APK container overview</description></item>
/// </list>
/// </summary>
public sealed class ApkNativeLibsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "ApkNativeLibs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "APK native libraries";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".apk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => []; // explicit-only routing
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Alternative view over an APK exposing only lib/<abi>/*.so native libraries.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  /// <summary>
  /// Emits a fresh APK-shaped ZIP containing only the native libraries supplied
  /// in <paramref name="inputs"/>. Incoming entry paths may use either the
  /// underlying <c>lib/&lt;abi&gt;/*.so</c> form or the rewritten
  /// <c>native_libs/&lt;abi&gt;/*.so</c> view — the latter is unrewrap-ed back
  /// to <c>lib/</c> before being added to the inner ZIP so the produced archive
  /// is a standard split-APK fragment loadable by any APK tool.
  /// Entries that don't end in <c>.so</c> are written verbatim.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      var name = i.ArchiveName.Replace('\\', '/');
      if (name.StartsWith("native_libs/", StringComparison.Ordinal))
        name = "lib/" + name.Substring("native_libs/".Length);
      if (i.IsDirectory) { w.AddDirectory(name); continue; }
      w.AddEntry(name, i.ReadContent());
    }
  }
}
