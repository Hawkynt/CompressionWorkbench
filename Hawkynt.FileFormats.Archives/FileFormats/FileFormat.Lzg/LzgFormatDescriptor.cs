#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.Lzg;

/// <summary>
/// Describes LZG format.
/// </summary>
public sealed class LzgFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Lzg";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZG";
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
  public string DefaultExtension => ".lzg";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lzg"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4C, 0x5A, 0x47], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzg1", "LZG1", SupportsOptimize: true)];
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
  public string Description => "Marcus Geelnard's lightweight LZ77 codec with marker-coded back-references";

  /// <summary>
  /// Gets the finite liblzg-compatible encoder option space searched by the
  /// generic compression optimizer.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: "5",
      AllowedValues: ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
      Description: "Compression effort from 1 (fastest/smallest window) to 9 (strongest/largest window)."),
    new FormatOptionDescriptor(
      Key: "Fast",
      DisplayName: "Fast match lookup",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "Use a three-byte match key instead of the lower-memory two-byte key."),
  ];

  /// <summary>
  /// Resolves the requested compression level, preserving liblzg's level-5 default.
  /// </summary>
  internal static int ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    return int.TryParse(raw, out var level) ? Math.Clamp(level, 1, 9) : 5;
  }

  /// <summary>
  /// Resolves the requested match-lookup mode, preserving liblzg's fast=true default.
  /// </summary>
  internal static bool ParseFast(FormatCreateOptions options) {
    var raw = options.GetString("Fast");
    return bool.TryParse(raw, out var fast) ? fast : true;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => LzgStream.Decompress(input, output);

  /// <summary>
  /// Encodes the supplied input with the historical/default LZG settings.
  /// </summary>
  public void Compress(Stream input, Stream output) => LzgStream.Compress(input, output);

  /// <summary>
  /// Encodes the supplied input with the selected LZG optimizer parameters.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => LzgStream.Compress(input, output, ParseLevel(options), ParseFast(options));

  /// <summary>
  /// Encodes with the strongest configured LZG search level.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output)
    => LzgStream.Compress(input, output, level: 9, fast: true);
}
