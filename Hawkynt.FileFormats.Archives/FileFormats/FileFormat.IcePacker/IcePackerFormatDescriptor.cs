#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.IcePacker;

/// <summary>
/// Describes ice packer format.
/// </summary>
public sealed class IcePackerFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private static readonly IReadOnlyDictionary<string, int> SearchDepthByLevel =
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
      ["Fast"] = 16,
      ["Normal"] = IcePackerStream.DefaultSearchDepth,
      ["Best"] = 256,
    };

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "IcePacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "ICE Packer";
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
  public string DefaultExtension => ".ice";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".ice"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ice", "ICE", SupportsOptimize: true)];
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
  public string Description => "Atari ST/Amiga Ice Packer, demoscene LZ77";

  /// <summary>
  /// Encoder effort. This changes only how deeply the managed writer searches
  /// its LZ77 hash chains; the resulting ICE stream is self-contained and needs
  /// no matching decoder setting.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "Normal",
      AllowedValues: ["Fast", "Normal", "Best"],
      Description: "Fast searches 16 candidates per position, Normal 64, Best 256; deeper searches are slower but can find smaller parses."),
  ];

  private static int ParseSearchDepth(FormatCreateOptions options) {
    var level = options.GetString("Level");
    return level is not null && SearchDepthByLevel.TryGetValue(level, out var depth)
      ? depth
      : IcePackerStream.DefaultSearchDepth;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => IcePackerStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => IcePackerStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested optimizer-visible level.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => IcePackerStream.Compress(input, output, ParseSearchDepth(options));
  /// <summary>
  /// Encodes at every declared search depth and writes the smallest result.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var raw = new MemoryStream();
    input.CopyTo(raw);
    var data = raw.ToArray();

    byte[]? best = null;
    foreach (var depth in SearchDepthByLevel.Values.Distinct()) {
      var candidate = IcePackerStream.Compress(data, depth);
      if (best is null || candidate.Length < best.Length)
        best = candidate;
    }

    output.Write(best!);
  }
}
