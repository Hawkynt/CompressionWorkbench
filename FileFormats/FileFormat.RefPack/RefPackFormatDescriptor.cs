#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.RefPack;

/// <summary>
/// Describes ref pack format.
/// </summary>
public sealed class RefPackFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "RefPack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "RefPack/QFS";
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
  public string DefaultExtension => ".qfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".qfs", ".refpack"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("refpack", "RefPack", SupportsOptimize: true)];
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
  public string Description => "EA Games' LZ77 variant for game assets";

  /// <summary>
  /// RefPack has three useful encoder-search levers without changing the wire
  /// format: history reach, hash-chain search depth, and whether positions
  /// skipped by a match are indexed. The generic optimizer exhaustively searches
  /// the 24 finite combinations and keeps the smallest stream for the actual data.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      "WindowSize", "History window", FormatOptionKind.Integer,
      RefPackStream.DefaultWindowSize.ToString(), ["1024", "16384", "131072"],
      "Maximum back-reference reach in bytes. Smaller windows can prefer cheaper short references; 131072 is the full RefPack window."),
    new(
      "SearchDepth", "Match search depth", FormatOptionKind.Integer,
      RefPackStream.DefaultSearchDepth.ToString(), ["16", "64", "128", "512"],
      "Maximum number of hash-chain candidates examined at each input position."),
    new(
      "Quick", "Quick match indexing", FormatOptionKind.Boolean, "false", null,
      "When enabled, positions skipped by an emitted match are not inserted into the hash chain. Faster, but usually a weaker ratio."),
  ];

  private static (int WindowSize, int SearchDepth, bool Quick) ParseOptions(FormatCreateOptions options) {
    var windowSize = options.GetOptionInt("WindowSize", RefPackStream.DefaultWindowSize);
    if (Array.IndexOf(RefPackStream.OptimizationWindowSizes, windowSize) < 0)
      windowSize = RefPackStream.DefaultWindowSize;

    var searchDepth = options.GetOptionInt("SearchDepth", RefPackStream.DefaultSearchDepth);
    if (Array.IndexOf(RefPackStream.OptimizationSearchDepths, searchDepth) < 0)
      searchDepth = RefPackStream.DefaultSearchDepth;

    return (windowSize, searchDepth, options.GetOptionBool("Quick", fallback: false));
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => RefPackStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => RefPackStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using explicit RefPack match-search settings.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    using var raw = new MemoryStream();
    input.CopyTo(raw);
    var (windowSize, searchDepth, quick) = ParseOptions(options);
    output.Write(RefPackStream.Compress(raw.ToArray(), windowSize, searchDepth, quick));
  }
  /// <summary>
  /// Exhaustively searches the RefPack encoder settings and writes the smallest result.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) {
    using var raw = new MemoryStream();
    input.CopyTo(raw);
    output.Write(RefPackStream.CompressOptimal(raw.ToArray()));
  }
}
