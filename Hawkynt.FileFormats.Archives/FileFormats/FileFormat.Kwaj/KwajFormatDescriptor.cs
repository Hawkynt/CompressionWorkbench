#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Registry;

namespace FileFormat.Kwaj;

/// <summary>
/// Describes kwaj format.
/// </summary>
public sealed class KwajFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Kwaj";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "KWAJ";
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
  public string DefaultExtension => ".kwaj";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4B, 0x57, 0x41, 0x4A], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("kwaj", "KWAJ", SupportsOptimize: true)];
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
  public string Description => "MS-DOS COMPRESS.EXE variant with extended header";

  /// <summary>
  /// Gets the KWAJ writer options searched by the generic compression optimizer.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "Method",
      DisplayName: "Compression method",
      Kind: FormatOptionKind.Enum,
      Default: "MSZIP",
      AllowedValues: ["Store", "XOR", "MSZIP"],
      Description: "KWAJ method: Store writes the payload verbatim; XOR applies the format's 0xFF transform; MSZIP uses Deflate."),
    new(
      Key: "Level",
      DisplayName: "MSZIP compression level",
      Kind: FormatOptionKind.Enum,
      Default: "Default",
      AllowedValues: ["None", "Fast", "Default", "Best", "Maximum"],
      Description: "Deflate effort used by MSZIP; ignored by Store and XOR.",
      DependsOn: "Method=MSZIP"),
  ];

  private static int ParseMethod(FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    var method = options.GetOption("Method", "MSZIP");
    return method.ToLowerInvariant() switch {
      "store" => KwajConstants.MethodStore,
      "xor" => KwajConstants.MethodXor,
      "mszip" => KwajConstants.MethodMsZip,
      _ => throw new ArgumentException($"Unsupported KWAJ compression method '{method}'.", nameof(options)),
    };
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => KwajStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => KwajStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested KWAJ method and MSZIP effort.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    KwajStream.Compress(input, output, ParseMethod(options), filename: null, level: DeflateLevelOption.Parse(options));
  /// <summary>
  /// Encodes the supplied input using maximum MSZIP effort.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    KwajStream.Compress(input, output, KwajConstants.MethodMsZip, filename: null, level: DeflateCompressionLevel.Maximum);
}
