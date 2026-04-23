#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Gif;

/// <summary>
/// Exposes a multi-frame GIF89a/GIF87a as an archive of per-frame standalone GIFs.
/// Slicing is byte-level — LZW data is copied verbatim so each frame opens in any viewer
/// without re-encoding artifacts.
/// </summary>
public sealed class GifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Gif";
  public string DisplayName => "GIF (multi-frame)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gif";
  public IReadOnlyList<string> Extensions => [".gif"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GIF87a"u8.ToArray(), Confidence: 0.95),
    new("GIF89a"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW (variable-width, GIF flavour)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Animated GIF; each frame extractable as a standalone single-frame GIF.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var frames = ReadAll(stream);
    var entries = new List<ArchiveEntryInfo>(frames.Count);
    for (var i = 0; i < frames.Count; ++i) {
      var name = $"frame_{i:D3}.gif";
      entries.Add(new ArchiveEntryInfo(
        Index: i,
        Name: name,
        OriginalSize: frames[i].Data.Length,
        CompressedSize: frames[i].Data.Length,
        // Per-frame image data is LZW-compressed (variable-width, GIF flavour). The
        // descriptor's slicing only copies the LZW byte stream verbatim — no re-encoding —
        // so the listed entries report "lzw" to reflect the actual on-disk encoding the
        // user would see if they hex-dumped the byte range.
        Method: "lzw",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var frames = ReadAll(stream);
    for (var i = 0; i < frames.Count; ++i) {
      var name = $"frame_{i:D3}.gif";
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, name, frames[i].Data);
    }
  }

  private static List<GifReader.Frame> ReadAll(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return new GifReader().Read(ms.ToArray());
  }
}
