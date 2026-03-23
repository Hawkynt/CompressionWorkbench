namespace Compression.Lib;

/// <summary>
/// Bundles all compression settings from CLI options into a single object
/// that flows through Create/Convert operations.
/// </summary>
internal sealed class CompressionOptions {
  /// <summary>Compression method (e.g. deflate, lzma, store) with optional "+" optimization.</summary>
  internal MethodSpec Method { get; init; } = MethodSpec.Default;

  /// <summary>Number of parallel compression threads. 1 = single-threaded.</summary>
  internal int Threads { get; init; } = 1;

  /// <summary>Maximum solid block size in bytes. 0 = single block (no splitting).</summary>
  internal long SolidSize { get; init; }

  /// <summary>Dictionary size in bytes, or 0 for format default.</summary>
  internal long DictSize { get; init; }

  /// <summary>Word size / fast bytes, or null for format default.</summary>
  internal int? WordSize { get; init; }

  /// <summary>Compression level (0-9), or null for format default.</summary>
  internal int? Level { get; init; }

  /// <summary>Whether to compress all files regardless of entropy detection.</summary>
  internal bool ForceCompress { get; init; }

  /// <summary>Encryption password.</summary>
  internal string? Password { get; init; }

  /// <summary>When true, encrypt file names/headers in addition to file data (7z, RAR5).</summary>
  internal bool EncryptFilenames { get; init; }

  /// <summary>ZIP encryption method: "aes256" (default) or "zipcrypto".</summary>
  internal string? ZipEncryption { get; init; }

  /// <summary>Resolves the ZIP encryption method enum from the string option.</summary>
  internal FileFormat.Zip.ZipEncryptionMethod ResolveZipEncryption() => ZipEncryption switch {
    "zipcrypto" => FileFormat.Zip.ZipEncryptionMethod.PkzipTraditional,
    _ => FileFormat.Zip.ZipEncryptionMethod.Aes256,
  };

  /// <summary>
  /// Resolves the effective LZMA dictionary size, using the user's --dict-size if set,
  /// or a default based on the method spec. Result is always a valid 2^n or 3×2^(n-1) value.
  /// </summary>
  internal int ResolveLzmaDictSize() {
    if (DictSize > 0) return NormalizeDictSize((int)Math.Min(DictSize, 1L << 30));
    return Method.Optimize ? 1 << 26 : 1 << 23; // 64MB for +, 8MB default
  }

  /// <summary>
  /// Snaps a dictionary size to the nearest valid LZMA/LZMA2 value (2^n or 3×2^(n-1)).
  /// These are the only sizes 7-Zip recognizes as valid for LZMA format detection.
  /// </summary>
  internal static int NormalizeDictSize(int size) {
    if (size <= 4096) return 4096;
    // Find the two candidate sizes: 2^n and 3×2^(n-1)
    var bits = 31 - int.LeadingZeroCount(size);
    var pow2 = 1 << bits;             // e.g. for 5MB → 4MB
    var pow2Next = 1 << (bits + 1);   // e.g. for 5MB → 8MB
    var threeHalf = 3 << (bits - 1);  // e.g. for 5MB → 6MB

    // Pick the closest that's >= size (round up to next valid)
    var best = pow2Next; // worst case
    if (pow2 >= size) best = pow2;
    if (threeHalf >= size && threeHalf < best) best = threeHalf;
    if (pow2Next < best) best = pow2Next;

    return Math.Min(best, 1 << 30); // cap at 1GB
  }

  /// <summary>
  /// Resolves the effective LZMA compression level.
  /// </summary>
  internal Compression.Core.Dictionary.Lzma.LzmaCompressionLevel ResolveLzmaLevel() {
    if (Level.HasValue) return Level.Value switch {
      <= 1 => Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Fast,
      <= 5 => Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Normal,
      _ => Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Best,
    };
    return Method.Optimize
      ? Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Best
      : Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Normal;
  }

  /// <summary>
  /// Resolves the effective Deflate compression level.
  /// </summary>
  internal Compression.Core.Deflate.DeflateCompressionLevel ResolveDeflateLevel() {
    if (Level.HasValue) return Level.Value switch {
      0 => Compression.Core.Deflate.DeflateCompressionLevel.None,
      <= 1 => Compression.Core.Deflate.DeflateCompressionLevel.Fast,
      <= 5 => Compression.Core.Deflate.DeflateCompressionLevel.Default,
      <= 9 => Compression.Core.Deflate.DeflateCompressionLevel.Best,
      _ => Compression.Core.Deflate.DeflateCompressionLevel.Maximum,
    };
    return Method.ResolveDeflateLevel();
  }

  /// <summary>
  /// Resolves the BZip2 block size (1-9, where N = N*100KB).
  /// </summary>
  internal int ResolveBzip2BlockSize() {
    if (DictSize > 0) return (int)Math.Clamp(DictSize / (100 * 1024), 1, 9);
    return 9; // default: 900KB
  }

  /// <summary>
  /// Resolves the PPMd model order from --word-size (2-32, default 6 for 7z, 8 for ZIP).
  /// </summary>
  internal int ResolvePpmdOrder(int defaultOrder = 6) {
    if (WordSize.HasValue) return Math.Clamp(WordSize.Value, 2, 32);
    return defaultOrder;
  }

  /// <summary>
  /// Resolves the PPMd memory/dictionary size from --dict-size.
  /// </summary>
  internal int ResolvePpmdMemorySize() {
    if (DictSize > 0) return (int)Math.Min(DictSize, 1L << 30);
    return 1 << 24; // 16MB default
  }
}
