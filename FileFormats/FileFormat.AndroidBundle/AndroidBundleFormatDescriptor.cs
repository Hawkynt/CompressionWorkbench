#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AndroidBundle;

/// <summary>
/// Read-only archive view of an Android App Bundle (<c>.aab</c>) or split-APK set
/// (<c>.apks</c>). The underlying container is a ZIP; this descriptor re-exposes its
/// entries with the split-APK semantics surfaced in the path:
/// <list type="bullet">
///   <item><c>base/</c> sub-tree → <c>base/...</c> (verbatim).</item>
///   <item><c>splits/*.apk</c> top-level APKs → kept at <c>splits/*.apk</c>.</item>
///   <item><c>BundleConfig.pb</c> → kept at root.</item>
/// </list>
/// <para>
/// The actual content is a ZIP, so detection is extension-based; at the raw-magic level
/// this still looks like any other PK-signed ZIP and the Zip / Apk descriptors would
/// also match if routed by magic. This descriptor intentionally declares a lower
/// detection confidence for the ZIP local-file header so Zip/Apk win on ambiguous
/// inputs.
/// </para>
/// </summary>
public sealed class AndroidBundleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "AndroidBundle";
  public string DisplayName => "Android App Bundle / split-APK set";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".aab";
  public IReadOnlyList<string> Extensions => [".aab", ".apks"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // PK local-file header. Intentionally low confidence so Zip/Apk outrank us on
    // extensionless inputs — AAB/APKS detection really wants the file extension.
    new([0x50, 0x4B, 0x03, 0x04], Confidence: 0.15),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Android App Bundle (.aab) or split-APK set (.apks) re-surfaced so split boundaries " +
    "are visible (base/, splits/, BundleConfig.pb).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, RewriteName(e.FileName), e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      var rewritten = RewriteName(e.FileName);
      if (files != null && !MatchesFilter(rewritten, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, rewritten)); continue; }
      var data = r.ExtractEntry(e);
      WriteFile(outputDir, rewritten, data);
      if (e.FileName.Equals("BundleConfig.pb", StringComparison.OrdinalIgnoreCase))
        WriteFile(outputDir, "metadata.ini", SummarizeBundleConfig(data));
    }
  }

  /// <summary>
  /// AAB entry names already carry the split structure (<c>base/</c>, <c>splits/</c>,
  /// <c>BundleConfig.pb</c>); this method is a no-op for recognised shapes and a
  /// passthrough otherwise so malformed bundles still extract.
  /// </summary>
  private static string RewriteName(string zipName) => zipName;

  /// <summary>
  /// Emits a best-effort plain-text summary of <c>BundleConfig.pb</c> (a protobuf).
  /// We don't decode the schema; instead we surface printable ASCII runs ≥4 bytes to
  /// give a readable-ish view of the config without pulling in a protobuf dependency.
  /// </summary>
  private static byte[] SummarizeBundleConfig(byte[] pb) {
    var sb = new StringBuilder();
    sb.Append("# BundleConfig.pb — printable string summary\n");
    sb.Append("# (raw protobuf; use `protoc --decode_raw` for full structure)\n\n");
    var run = new StringBuilder();
    foreach (var b in pb) {
      if (b >= 0x20 && b < 0x7F) {
        run.Append((char)b);
        continue;
      }
      if (run.Length >= 4) {
        sb.Append(run).Append('\n');
      }
      run.Clear();
    }
    if (run.Length >= 4) sb.Append(run).Append('\n');
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
