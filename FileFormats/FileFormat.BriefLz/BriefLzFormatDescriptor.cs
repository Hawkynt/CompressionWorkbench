#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BriefLz;

/// <summary>
/// Describes the BriefLZ <c>blzpack</c> stream format.
/// </summary>
public sealed class BriefLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BriefLz";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BriefLZ";
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
  public string DefaultExtension => ".blz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".blz"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x6C, 0x7A, 0x1A], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("brieflz", "BriefLZ", SupportsOptimize: true)];
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
  public string Description => "BriefLZ LZSS stream with gamma2-coded matches and tunable encoder effort";

  /// <summary>
  /// Encoder effort is not serialized: all ten choices produce the same
  /// decoder-compatible BriefLZ syntax, so the generic compression optimizer can
  /// exhaustively compare them on the caller's actual bytes.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "1",
      AllowedValues: ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"],
      Description: "Managed encoder effort: 1 follows the fast reference hash parser; higher levels search progressively more candidate matches.")
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => BriefLzStream.Decompress(input, output);

  /// <summary>
  /// Encodes using the reference-compatible fast parser.
  /// </summary>
  public void Compress(Stream input, Stream output) => BriefLzStream.Compress(input, output);

  /// <summary>
  /// Encodes using the requested managed effort level.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    BriefLzStream.Compress(input, output, ParseLevel(options));

  /// <summary>
  /// Tries every managed effort level and writes the smallest result.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) => BriefLzStream.CompressOptimal(input, output);

  internal static int ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    return int.TryParse(raw, out var level)
      && level is >= BriefLzStream.MinimumCompressionLevel and <= BriefLzStream.MaximumCompressionLevel
      ? level
      : BriefLzStream.MinimumCompressionLevel;
  }
}
