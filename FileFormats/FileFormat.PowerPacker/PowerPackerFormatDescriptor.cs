#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PowerPacker;

/// <summary>
/// Describes power packer format.
/// </summary>
public sealed class PowerPackerFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "PowerPacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PowerPacker";
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
  public string DefaultExtension => ".pp";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pp", ".pp20"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("powerpacker", "PowerPacker", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Amiga PowerPacker PP20 backward LZ compression";

  /// <summary>The historical offset-width preset used by the PP20 match coder.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Efficiency",
      DisplayName: "Efficiency",
      Kind: FormatOptionKind.Enum,
      Default: nameof(PowerPackerEfficiency.Good),
      AllowedValues: Enum.GetNames<PowerPackerEfficiency>(),
      Description: "PowerPacker offset-width preset. Optimize searches all five historical tables and keeps the smallest stream."),
  ];

  internal static PowerPackerEfficiency ParseEfficiency(FormatCreateOptions options) {
    var value = options.GetOption("Efficiency", nameof(PowerPackerEfficiency.Good));
    return Enum.TryParse<PowerPackerEfficiency>(value, ignoreCase: true, out var efficiency)
      && Enum.IsDefined(efficiency)
        ? efficiency
        : PowerPackerEfficiency.Good;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => PowerPackerStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => PowerPackerStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input with the requested efficiency preset.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => PowerPackerStream.Compress(input, output, ParseEfficiency(options));
  /// <summary>
  /// Tries all historical efficiency presets and writes the smallest PP20 stream.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) => PowerPackerStream.CompressOptimal(input, output);
}
