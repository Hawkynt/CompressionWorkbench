#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>
/// Resolves <see cref="FormatCreateOptions"/> into 7z-specific concrete settings.
/// Used by <see cref="SevenZipFormatDescriptor"/>'s <c>Create</c> implementation.
/// </summary>
internal static class SevenZipOptionsResolver {

  /// <summary>Resolves the 7z codec from an option string. Defaults to LZMA2.</summary>
  public static SevenZipCodec ResolveCodec(string? name) => (name ?? "").ToLowerInvariant() switch {
    "lzma" => SevenZipCodec.Lzma,
    "deflate" => SevenZipCodec.Deflate,
    "bzip2" => SevenZipCodec.BZip2,
    "ppmd" => SevenZipCodec.PPMd,
    "copy" or "store" => SevenZipCodec.Copy,
    _ => SevenZipCodec.Lzma2,
  };

  public static int ResolveLzmaDictSize(long dictSize, bool optimize) {
    if (dictSize > 0) return NormalizeDictSize((int)Math.Min(dictSize, 1L << 30));
    return optimize ? 1 << 26 : 1 << 23;
  }

  public static int NormalizeDictSize(int size) => Compression.Core.Dictionary.Lzma.LzmaDictionarySize.Normalize(size);

  public static int ResolvePpmdOrder(int? wordSize, int defaultOrder = 6) {
    if (wordSize.HasValue) return Math.Clamp(wordSize.Value, 2, 32);
    return defaultOrder;
  }

  public static int ResolvePpmdMemorySize(long dictSize) {
    if (dictSize > 0) return (int)Math.Min(dictSize, 1L << 30);
    return 1 << 24;
  }

  public static int ResolveBzip2BlockSize(long dictSize) {
    if (dictSize > 0) return (int)Math.Clamp(dictSize / (100 * 1024), 1, 9);
    return 9;
  }
}
