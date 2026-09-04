using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Parses a method string like "deflate", "deflate+", "lzma+", "store" into a
/// normalized name and optimize flag. The "+" suffix selects the best available
/// encoder for that codec while remaining fully decoder-compatible, and a repeated
/// one asks for more of it again.
/// </summary>
/// <remarks>
/// Parsing agrees with <see cref="Compression.Registry.MethodNameParser"/>, which
/// is what the writers on the far side of the registry boundary read the same
/// string with. It used to strip exactly one trailing '+' where that parser strips
/// all of them and counts, so the two read one input two ways: "ds-lz77++" reached
/// the CVF writers as "ds-lz77+", and a caller who asked for the third effort tier
/// silently got the second.
/// </remarks>
public readonly record struct MethodSpec(string Name, bool Optimize) {

  /// <summary>
  /// How many trailing '+' the method carried. <see cref="Optimize"/> is whether
  /// there was one at all; this is how many, which is what a writer offering more
  /// than one effort tier reads.
  /// </summary>
  public int PlusLevel { get; init; } = Optimize ? 1 : 0;

  public static MethodSpec Default => new("default", false);

  /// <remarks>
  /// Every trailing <c>+</c> is stripped, not just the last one, so the base name matches what
  /// <see cref="Compression.Registry.MethodNameParser.Parse" /> derives on the far side of the
  /// create boundary. That parser documents <c>deflate++</c> as "~100× effort"; stripping a single
  /// <c>+</c> here left the base name as <c>deflate+</c>, which is not a method any creator knows.
  /// <see cref="Optimize" /> is a flag rather than a count, so how many were stripped is reported
  /// by <see cref="PlusLevel" /> rather than smuggled through in the name.
  /// </remarks>
  public static MethodSpec Parse(string? input) {
    if (string.IsNullOrWhiteSpace(input)) return Default;
    // Parsed by MethodNameParser itself rather than alongside it, so there is one
    // reading of a method name and not two that have to be kept in step.
    var (name, plus) = Compression.Registry.MethodNameParser.Parse(input);
    return new(name.ToLowerInvariant(), plus > 0) { PlusLevel = plus };
  }

  /// <summary>Whether this is the default "no preference" spec.</summary>
  public bool IsDefault => Name == "default" && !Optimize;

  /// <summary>
  /// The method name as the registry boundary spells it: <see langword="null" /> when the spec
  /// carries no method preference at all.
  /// </summary>
  /// <remarks>
  /// <see cref="Compression.Registry.FormatCreateOptions.MethodName" /> uses <see langword="null" />
  /// for "no preference"; this side spells the same thing as the literal <c>"default"</c>. Three
  /// further spellings mean it too and must not reach a creator either: a bare <c>"+"</c> parses to
  /// an empty base name, <c>default(MethodSpec)</c> leaves the name null, and <c>"default+"</c>
  /// keeps the sentinel while setting <see cref="Optimize" />. <see cref="IsDefault" /> answers
  /// false for that last one — it also requires <c>!Optimize</c>, which is right for the tier
  /// decision in <c>ArchiveOperations.ConvertCore</c> but wrong for the boundary, where a
  /// <c>-m default+</c> then handed every creator a method literally named "default": the lenient
  /// ones ignored it and the strict ones correctly refused it as unknown.
  /// </remarks>
  public string? EffectiveName => Name is null or "" or "default" ? null : Name;

  /// <remarks>
  /// The whole run of <c>+</c> is printed, not one of them, because this is what a
  /// writer that re-parses the name it was handed reads its effort tier out of.
  /// </remarks>
  public override string ToString() => PlusLevel > 0 ? Name + new string('+', PlusLevel) : Name;

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
