using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Parses a method string like "deflate", "deflate+", "lzma+", "store" into a
/// normalized name and optimize flag. The "+" suffix selects the best available
/// encoder for that codec while remaining fully decoder-compatible.
/// </summary>
public readonly record struct MethodSpec(string Name, bool Optimize) {

  public static MethodSpec Default => new("default", false);

  public static MethodSpec Parse(string? input) {
    if (string.IsNullOrWhiteSpace(input)) return Default;
    var trimmed = input.Trim();
    if (trimmed.EndsWith('+'))
      return new(trimmed[..^1].ToLowerInvariant(), true);
    return new(trimmed.ToLowerInvariant(), false);
  }

  /// <summary>Whether this is the default "no preference" spec.</summary>
  public bool IsDefault => Name == "default" && !Optimize;

  public override string ToString() => Optimize ? $"{Name}+" : Name;

  // ── ZIP method resolution ───────────────────────────────────────────

  public (FileFormat.Zip.ZipCompressionMethod Method, Compression.Core.Deflate.DeflateCompressionLevel Level)
      ResolveZip() => Name switch {
    "store" => (FileFormat.Zip.ZipCompressionMethod.Store, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "shrink" => (FileFormat.Zip.ZipCompressionMethod.Shrink, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "reduce" or "reduce4" => (FileFormat.Zip.ZipCompressionMethod.Reduce4, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "reduce1" => (FileFormat.Zip.ZipCompressionMethod.Reduce1, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "reduce2" => (FileFormat.Zip.ZipCompressionMethod.Reduce2, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "reduce3" => (FileFormat.Zip.ZipCompressionMethod.Reduce3, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "implode" => (FileFormat.Zip.ZipCompressionMethod.Implode, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "deflate64" => (FileFormat.Zip.ZipCompressionMethod.Deflate64, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "bzip2" => (FileFormat.Zip.ZipCompressionMethod.BZip2, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "lzma" => (FileFormat.Zip.ZipCompressionMethod.Lzma, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "ppmd" => (FileFormat.Zip.ZipCompressionMethod.Ppmd, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    "zstd" => (FileFormat.Zip.ZipCompressionMethod.Zstd, Compression.Core.Deflate.DeflateCompressionLevel.Default),
    // deflate or default
    _ => (FileFormat.Zip.ZipCompressionMethod.Deflate,
          Optimize ? Compression.Core.Deflate.DeflateCompressionLevel.Maximum
                   : Compression.Core.Deflate.DeflateCompressionLevel.Default),
  };

  // ── 7z codec resolution ─────────────────────────────────────────────

  public FileFormat.SevenZip.SevenZipCodec Resolve7z() => Name switch {
    "lzma" => FileFormat.SevenZip.SevenZipCodec.Lzma,
    "deflate" => FileFormat.SevenZip.SevenZipCodec.Deflate,
    "bzip2" => FileFormat.SevenZip.SevenZipCodec.BZip2,
    "ppmd" => FileFormat.SevenZip.SevenZipCodec.PPMd,
    "copy" or "store" => FileFormat.SevenZip.SevenZipCodec.Copy,
    _ => FileFormat.SevenZip.SevenZipCodec.Lzma2,
  };

  // ── Deflate level (for Gzip, Zlib, standalone Deflate) ──────────────

  public Compression.Core.Deflate.DeflateCompressionLevel ResolveDeflateLevel()
    => Optimize
      ? Compression.Core.Deflate.DeflateCompressionLevel.Maximum
      : Compression.Core.Deflate.DeflateCompressionLevel.Default;

  // ── Display: list of available "+" methods per format ───────────────

  public static string[] GetOptimizableMethods(F format) => format switch {
    F.Zip => ["deflate+  (Zopfli optimal Deflate)", "deflate64+", "lzma+", "zstd+"],
    F.SevenZip => ["lzma2+  (Best LZMA2)", "lzma+", "deflate+"],
    F.Gzip or F.Zlib => ["deflate+  (Zopfli optimal Deflate)"],
    F.Xz or F.Lzma or F.Lzip => ["lzma+  (Best LZMA)"],
    F.Zstd => ["zstd+  (Best Zstd)"],
    F.Lz4 => ["lz4+  (HC max)"],
    F.Brotli => ["brotli+  (Best Brotli)"],
    F.Compress => ["lzw+  (Optimal LZW)"],
    F.Lzop => ["lzo+  (LZO1X-999)"],
    _ => [],
  };
}
