#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rzip;

/// <summary>
/// Describes rzip format.
/// </summary>
public sealed class RzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Rzip";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Rzip";
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
  public string DefaultExtension => ".rz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".rz", ".rzip"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rzip", "Rzip", SupportsOptimize: true)];
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
  public string Description => "Long-distance redundancy elimination, for large files";

  /// <summary>
  /// Encoder-only knobs searched by the generic compression optimizer. Neither value is
  /// serialized: all resulting streams use the same token grammar and decoder.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "MinMatch",
      DisplayName: "Minimum match length",
      Kind: FormatOptionKind.Integer,
      Default: "16",
      AllowedValues: ["8", "12", "16", "32", "64"],
      Description: "Rolling-signature width and shortest accepted long-range match. Smaller values find finer repeats; larger values avoid token overhead."),
    new FormatOptionDescriptor(
      Key: "CandidateSearchLimit",
      DisplayName: "Match candidates",
      Kind: FormatOptionKind.Integer,
      Default: "32",
      AllowedValues: ["1", "4", "16", "32", "64"],
      Description: "Maximum recent positions with the same rolling signature checked for the longest match. Larger values spend more CPU to recover older long matches."),
  ];

  private static int ParseMinMatch(FormatCreateOptions options)
    => options.GetOptionInt("MinMatch", RzipConstants.MinMatch) is 8 or 12 or 16 or 32 or 64
      ? options.GetOptionInt("MinMatch", RzipConstants.MinMatch)
      : RzipConstants.MinMatch;

  private static int ParseCandidateSearchLimit(FormatCreateOptions options)
    => options.GetOptionInt("CandidateSearchLimit", RzipConstants.CandidateSearchLimit) is 1 or 4 or 16 or 32 or 64
      ? options.GetOptionInt("CandidateSearchLimit", RzipConstants.CandidateSearchLimit)
      : RzipConstants.CandidateSearchLimit;

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => RzipStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => RzipStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the selected match-finder settings.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => RzipStream.Compress(input, output, ParseMinMatch(options), ParseCandidateSearchLimit(options));
}
