#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using Compression.Registry;

namespace FileFormat.Zip;

/// <summary>
/// Resolves <see cref="FormatCreateOptions"/> into ZIP-specific concrete settings
/// (codec, level, dictionary size, etc.). Used by
/// <see cref="ZipFormatDescriptor"/>'s <c>Create</c> implementation.
/// </summary>
internal static class ZipOptionsResolver {

  /// <summary>Resolves a method name string + optimize flag to a ZIP method + Deflate level.</summary>
  public static (ZipCompressionMethod Method, DeflateCompressionLevel Level)
      ResolveMethod(string? name, bool optimize) => (name ?? "").ToLowerInvariant() switch {
    "store" => (ZipCompressionMethod.Store, DeflateCompressionLevel.Default),
    "shrink" => (ZipCompressionMethod.Shrink, DeflateCompressionLevel.Default),
    "reduce" or "reduce4" => (ZipCompressionMethod.Reduce4, DeflateCompressionLevel.Default),
    "reduce1" => (ZipCompressionMethod.Reduce1, DeflateCompressionLevel.Default),
    "reduce2" => (ZipCompressionMethod.Reduce2, DeflateCompressionLevel.Default),
    "reduce3" => (ZipCompressionMethod.Reduce3, DeflateCompressionLevel.Default),
    "implode" => (ZipCompressionMethod.Implode, DeflateCompressionLevel.Default),
    "deflate64" => (ZipCompressionMethod.Deflate64, DeflateCompressionLevel.Default),
    "bzip2" => (ZipCompressionMethod.BZip2, DeflateCompressionLevel.Default),
    "lzma" => (ZipCompressionMethod.Lzma, DeflateCompressionLevel.Default),
    "ppmd" => (ZipCompressionMethod.Ppmd, DeflateCompressionLevel.Default),
    "zstd" => (ZipCompressionMethod.Zstd, DeflateCompressionLevel.Default),
    _ => (ZipCompressionMethod.Deflate,
          optimize ? DeflateCompressionLevel.Maximum : DeflateCompressionLevel.Default),
  };

  /// <summary>Resolves the ZIP encryption method from the option string.</summary>
  public static ZipEncryptionMethod ResolveEncryption(string? method) => method switch {
    "zipcrypto" => ZipEncryptionMethod.PkzipTraditional,
    _ => ZipEncryptionMethod.Aes256,
  };

  /// <summary>Resolves Deflate level from explicit Level + optimize flag.</summary>
  public static DeflateCompressionLevel ResolveDeflateLevel(int? level, bool optimize) {
    if (level.HasValue) return level.Value switch {
      0 => DeflateCompressionLevel.None,
      <= 1 => DeflateCompressionLevel.Fast,
      <= 5 => DeflateCompressionLevel.Default,
      <= 9 => DeflateCompressionLevel.Best,
      _ => DeflateCompressionLevel.Maximum,
    };
    return optimize ? DeflateCompressionLevel.Maximum : DeflateCompressionLevel.Default;
  }

  /// <summary>Resolves LZMA dictionary size, defaulting to 8 MB (or 64 MB for optimize).</summary>
  public static int ResolveLzmaDictSize(long dictSize, bool optimize) {
    if (dictSize > 0) return NormalizeDictSize((int)Math.Min(dictSize, 1L << 30));
    return optimize ? 1 << 26 : 1 << 23;
  }

  /// <summary>Snaps a dict size to the nearest valid LZMA value (2^n or 3×2^(n-1)).</summary>
  public static int NormalizeDictSize(int size) {
    if (size <= 4096) return 4096;
    var bits = 31 - int.LeadingZeroCount(size);
    var pow2 = 1 << bits;
    var pow2Next = 1 << (bits + 1);
    var threeHalf = 3 << (bits - 1);
    var best = pow2Next;
    if (pow2 >= size) best = pow2;
    if (threeHalf >= size && threeHalf < best) best = threeHalf;
    if (pow2Next < best) best = pow2Next;
    return Math.Min(best, 1 << 30);
  }

  public static LzmaCompressionLevel ResolveLzmaLevel(int? level, bool optimize) {
    if (level.HasValue) return level.Value switch {
      <= 1 => LzmaCompressionLevel.Fast,
      <= 5 => LzmaCompressionLevel.Normal,
      _ => LzmaCompressionLevel.Best,
    };
    return optimize ? LzmaCompressionLevel.Best : LzmaCompressionLevel.Normal;
  }

  public static int ResolveBzip2BlockSize(long dictSize) {
    if (dictSize > 0) return (int)Math.Clamp(dictSize / (100 * 1024), 1, 9);
    return 9;
  }

  public static int ResolvePpmdOrder(int? wordSize, int defaultOrder = 6) {
    if (wordSize.HasValue) return Math.Clamp(wordSize.Value, 2, 32);
    return defaultOrder;
  }
}
