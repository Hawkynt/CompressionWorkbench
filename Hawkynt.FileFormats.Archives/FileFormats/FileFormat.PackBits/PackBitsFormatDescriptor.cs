#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.PackBits;

/// <summary>
/// Describes pack bits format.
/// </summary>
public sealed class PackBitsFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "PackBits";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PackBits";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".packbits";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".packbits"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x50, 0x4B, 0x42, 0x54], Confidence: 0.85)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("packbits", "PackBits", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Apple PackBits RLE, used in TIFF/Macintosh";

  /// <summary>
  /// Gets the packetization strategy searched by the generic compression optimizer.
  /// Greedy preserves the historical fast encoder; Optimal computes a minimum-size
  /// legal PackBits packet sequence for the complete input.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Packetizer",
      DisplayName: "Packetizer",
      Kind: FormatOptionKind.Enum,
      Default: nameof(PackBitsEncodingStrategy.Greedy),
      AllowedValues: [nameof(PackBitsEncodingStrategy.Greedy), nameof(PackBitsEncodingStrategy.Optimal)],
      Description: "Greedy is fastest; Optimal finds the minimum-size legal PackBits packet sequence."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => PackBitsStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input with the historical greedy packetizer.
  /// </summary>
  public void Compress(Stream input, Stream output) => PackBitsStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested packetizer.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    PackBitsStream.Compress(input, output, ParsePacketizer(options));
  /// <summary>
  /// Encodes the supplied input with the exact minimum-size packetizer.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    PackBitsStream.Compress(input, output, PackBitsEncodingStrategy.Optimal);

  private static PackBitsEncodingStrategy ParsePacketizer(FormatCreateOptions options) {
    var raw = options.GetString("Packetizer");
    return Enum.TryParse<PackBitsEncodingStrategy>(raw, ignoreCase: true, out var strategy)
      ? strategy
      : PackBitsEncodingStrategy.Greedy;
  }
}
