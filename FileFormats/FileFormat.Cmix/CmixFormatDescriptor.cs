#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Cmix;

/// <summary>
/// Describes cmix format.
/// </summary>
public sealed class CmixFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Cmix";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "cmix";
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
  public string DefaultExtension => ".cmix";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".cmix"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cmix", "cmix", SupportsOptimize: true)];
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
  public string Description => "Neural context-mixing compressor by Byron Knoll";

  /// <summary>
  /// Arithmetic-coder endings exposed to the shared optimizer. Legacy preserves
  /// the historical managed output; Compact removes three redundant termination bytes.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Finalization",
      DisplayName: "Arithmetic finalization",
      Kind: FormatOptionKind.Enum,
      Default: "Legacy",
      AllowedValues: ["Legacy", "Compact"],
      Description: "Legacy writes the historical 32-bit final code. Compact writes only the distinguishing byte and relies on zero EOF padding."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => CmixStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => CmixStream.Compress(input, output);

  /// <summary>
  /// Encodes the supplied input using the requested optimizer parameters.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    CmixStream.Compress(input, output, ParseFinalization(options));

  /// <summary>
  /// Encodes with the compact arithmetic finalization.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    CmixStream.Compress(input, output, CmixStream.FinalizationMode.Compact);

  private static CmixStream.FinalizationMode ParseFinalization(FormatCreateOptions options) =>
    options.GetOption("Finalization", "Legacy").Equals("Compact", StringComparison.OrdinalIgnoreCase)
      ? CmixStream.FinalizationMode.Compact
      : CmixStream.FinalizationMode.Legacy;
}
