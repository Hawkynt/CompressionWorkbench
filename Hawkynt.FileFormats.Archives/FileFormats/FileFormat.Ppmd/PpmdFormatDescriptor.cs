#pragma warning disable CS1591
using System.Globalization;
using Compression.Core.Entropy.Ppmd;
using Compression.Registry;

namespace FileFormat.Ppmd;

/// <summary>
/// Describes ppmd format.
/// </summary>
public sealed class PpmdFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Ppmd";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PPMd";
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
  public string DefaultExtension => ".pmd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pmd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x8F, 0xAF, 0xAC, 0x84], Confidence: 0.90)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ppmd", "PPMd", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "PPMd standalone, Prediction by Partial Matching (Dmitry Shkarin)";

  /// <summary>
  /// PPMd-H model order. 7-Zip exposes orders 2 through 32; the optimizer searches
  /// the complete finite range because the best context depth depends on the input.
  /// The managed model's memory-size constructor parameter is not currently an
  /// effective capacity limit, so memory is deliberately not advertised as a knob.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Order",
      DisplayName: "Model order",
      Kind: FormatOptionKind.Integer,
      Default: PpmdBuildingBlock.DefaultOrder.ToString(CultureInfo.InvariantCulture),
      AllowedValues: Enumerable.Range(
          PpmdBuildingBlock.MinOrder,
          PpmdBuildingBlock.MaxOrder - PpmdBuildingBlock.MinOrder + 1)
        .Select(static order => order.ToString(CultureInfo.InvariantCulture))
        .ToArray(),
      Description: "Maximum PPMd-H context order (2-32). Higher orders can help structured text but are not universally smaller."),
  ];

  internal static int ParseOrder(FormatCreateOptions options) {
    if (!options.HasOption("Order"))
      return PpmdBuildingBlock.DefaultOrder;

    if (!options.TryGetInt("Order", out var order)
        || order is < PpmdBuildingBlock.MinOrder or > PpmdBuildingBlock.MaxOrder)
      throw new ArgumentException(
        $"PPMd Order must be an integer between {PpmdBuildingBlock.MinOrder} and {PpmdBuildingBlock.MaxOrder}.",
        nameof(options));

    return order;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => PpmdStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => PpmdStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input with format-specific PPMd tunables.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => PpmdStream.Compress(input, output, PpmdFormatDescriptor.ParseOrder(options));
}
