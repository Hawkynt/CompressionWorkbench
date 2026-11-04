using Compression.Registry;

namespace FileFormat.Ctw;

/// <summary>
/// Files of the CTW 0.1 compression utility (Context Tree Weighting; Franken &amp; Peeters, TU
/// Eindhoven, 2002), byte-identical to the reference tool. See <c>docs/CTW-ON-DISK.md</c>.
/// </summary>
public sealed class CtwFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <inheritdoc/>
  public string Id => "Ctw";
  /// <inheritdoc/>
  public string DisplayName => "CTW 0.1";
  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc/>
  public FormatCapabilities Capabilities => FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc/>
  public string DefaultExtension => ".ctw";
  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".ctw"];
  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>None: the header starts 00 0C 00 00, too common to identify a file by.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ctw", "CTW")];
  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  /// <inheritdoc/>
  public string Description => "Context Tree Weighting coder of the CTW 0.1 utility (TU Eindhoven), byte-identical to the reference";

  /// <summary>The reference tool's tree depth (<c>-d</c>); every other parameter keeps its default.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "TreeDepth",
      DisplayName: "Tree depth",
      Kind: FormatOptionKind.Enum,
      Default: "6",
      AllowedValues: ["2", "3", "4", "5", "6", "7", "8", "9", "10"],
      Description: "Maximum context length in bytes, as ctw -d. Deeper trees model longer contexts and need more nodes."),
  ];

  /// <inheritdoc/>
  public void Compress(Stream input, Stream output) => CtwStream.Compress(input, output);

  /// <summary>Compresses with the selected tree depth.</summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    var depth = int.TryParse(options.GetString("TreeDepth"), out var value) && value is >= 1 and <= 12 ? value : 6;
    CtwStream.Compress(input, output, new CtwSettings(TreeDepth: depth));
  }

  /// <inheritdoc/>
  public void Decompress(Stream input, Stream output) => CtwStream.Decompress(input, output);
}
