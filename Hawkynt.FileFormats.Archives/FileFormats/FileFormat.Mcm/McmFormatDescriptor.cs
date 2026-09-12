#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Mcm;

/// <summary>
/// Describes mcm format.
/// </summary>
public sealed class McmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mcm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MCM";
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
  public string DefaultExtension => ".mcm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mcm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4D, 0x43, 0x4D, 0x41, 0x52, 0x43, 0x48, 0x49, 0x56, 0x45], Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mcm", "MCM", SupportsOptimize: true)];
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
  public string Description => "MCM-style context-mixing stream with clean-room reduced profile optimization";

  /// <summary>
  /// Searchable MCM modes. Legacy preserves the writer's historical payload;
  /// the remaining modes progressively enable more of the reduced clean-room
  /// context-mixing graph and therefore trade CPU/memory for coding density.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Mode",
      DisplayName: "Compression mode",
      Kind: FormatOptionKind.Enum,
      Default: "Legacy",
      AllowedValues: ["Legacy", "Turbo", "Fast", "Mid", "High", "Max"],
      Description: "Legacy preserves existing streams; Turbo through Max progressively enable more managed MCM model groups and SSE refinement."),
  ];

  internal static McmCompressionMode ParseMode(FormatCreateOptions options) {
    var raw = options.GetString("Mode");
    return raw is not null && Enum.TryParse<McmCompressionMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
      ? mode
      : McmCompressionMode.Legacy;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => McmStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => McmStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input with the selected profile.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    McmStream.Compress(input, output, ParseMode(options));
  /// <summary>
  /// Encodes with the highest-effort managed MCM profile.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    McmStream.Compress(input, output, McmCompressionMode.Max);
}
