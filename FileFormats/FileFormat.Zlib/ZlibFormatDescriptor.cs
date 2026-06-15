#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Registry;

namespace FileFormat.Zlib;

public sealed class ZlibFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Zlib";
  public string DisplayName => "Zlib";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".zlib";
  public IReadOnlyList<string> Extensions => [".zlib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Deflate with Adler32 checksum, foundational compression library";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // Level is the only honored axis. A Deflate *strategy* (filtered / huffman-only
  // / RLE / fixed) is NOT exposed because our Deflate core has no strategy
  // concept — the encoder only varies match-finding depth and static-vs-dynamic
  // Huffman by level, so a strategy knob would be ignored by the writer.

  /// <summary>The Deflate compression level applied to the Zlib payload. The
  /// optimizer searches these tiers to find the smallest output for the input.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "Default",
      AllowedValues: ["None", "Fast", "Default", "Best", "Maximum"],
      Description: "Deflate effort: None (stored) → Fast → Default → Best → Maximum (Zopfli-style optimal parsing; smallest, slowest)."),
  ];

  /// <summary>Resolves the requested <see cref="DeflateCompressionLevel"/> from the
  /// format-specific options: the named <c>Level</c> string wins; otherwise a numeric
  /// <see cref="FormatCreateOptions.Level"/> is mapped onto the nearest tier; failing
  /// both, <see cref="DeflateCompressionLevel.Default"/>.</summary>
  internal static DeflateCompressionLevel ParseLevel(FormatCreateOptions options) => DeflateLevelOption.Parse(options);

  public void Decompress(Stream input, Stream output) => ZlibStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => ZlibStream.Compress(input, output);
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    ZlibStream.Compress(input, output, ParseLevel(options));
  public void CompressOptimal(Stream input, Stream output) =>
    ZlibStream.Compress(input, output, DeflateCompressionLevel.Maximum);
}
