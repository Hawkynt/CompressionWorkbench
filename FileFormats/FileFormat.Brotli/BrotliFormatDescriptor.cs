#pragma warning disable CS1591
using Compression.Core.Dictionary.Brotli;
using Compression.Registry;

namespace FileFormat.Brotli;

public sealed class BrotliFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Brotli";
  public string DisplayName => "Brotli";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".br";
  public IReadOnlyList<string> Extensions => [".br"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("brotli", "Brotli", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Google's modern LZ77+Huffman with static dictionary, great for web content";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  /// <summary>Compression quality, the single knob the Brotli encoder exposes:
  /// <see cref="BrotliCompressionLevel.Uncompressed"/> (store), <c>Fast</c>,
  /// <c>Default</c>, and <c>Best</c> (deepest LZ77 search). The optimizer searches
  /// these to find the smallest output for the given input. The encoder derives the
  /// LZ77 window (lgwin) automatically from the input length, so there is no
  /// separate window knob to expose.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Quality",
      DisplayName: "Compression quality",
      Kind: FormatOptionKind.Enum,
      Default: nameof(BrotliCompressionLevel.Default),
      AllowedValues: [
        nameof(BrotliCompressionLevel.Uncompressed),
        nameof(BrotliCompressionLevel.Fast),
        nameof(BrotliCompressionLevel.Default),
        nameof(BrotliCompressionLevel.Best),
      ],
      Description: "Brotli compression quality (Uncompressed = store, Best = smallest/slowest)."),
  ];

  /// <summary>Parses the Brotli compression quality from the format-specific
  /// options; falls back to the encoder default (<see cref="BrotliCompressionLevel.Default"/>)
  /// when the value is absent or unrecognised.</summary>
  internal static BrotliCompressionLevel ParseQuality(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("Quality");
    return Enum.TryParse<BrotliCompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : BrotliCompressionLevel.Default;
  }

  public void Decompress(Stream input, Stream output) {
    var d = BrotliStream.Decompress(input);
    output.Write(d);
  }
  public void Compress(Stream input, Stream output) {
    var c = BrotliStream.Compress(input);
    output.Write(c);
  }
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var c = BrotliStream.Compress(ms.ToArray(), ParseQuality(options));
    output.Write(c);
  }
  public void CompressOptimal(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var c = BrotliStream.Compress(ms.ToArray(), BrotliCompressionLevel.Best);
    output.Write(c);
  }
}
