#pragma warning disable CS1591
using Compression.Core.Dictionary.Brotli;
using Compression.Registry;

namespace FileFormat.Brotli;

/// <summary>
/// Describes brotli format.
/// </summary>
public sealed class BrotliFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Brotli";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Brotli";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".br";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".br"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("brotli", "Brotli", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <summary>
  /// Gets the description.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) {
    var d = BrotliStream.Decompress(input);
    output.Write(d);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) {
    var c = BrotliStream.Compress(input);
    output.Write(c);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var c = BrotliStream.Compress(ms.ToArray(), ParseQuality(options));
    output.Write(c);
  }
  /// <summary>
  /// Performs the compress optimal operation.
  /// </summary>
public void CompressOptimal(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var c = BrotliStream.Compress(ms.ToArray(), BrotliCompressionLevel.Best);
    output.Write(c);
  }
}
