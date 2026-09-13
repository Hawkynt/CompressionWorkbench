#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zstd;

/// <summary>
/// Describes zstd format.
/// </summary>
public sealed class ZstdFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Zstd";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Zstandard";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".zst";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".zst", ".zstd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x28, 0xB5, 0x2F, 0xFD], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zstd", "Zstd", SupportsOptimize: true)];
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
  public string Description => "Facebook's modern codec, excellent speed/ratio tradeoff with dictionary support";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // Level is the only honored axis: our Zstd encoder derives the window size
  // per-frame from the content length and uses a single fixed hash-chain match
  // strategy with no long-distance-matching pass, so window-log / strategy / LDM
  // knobs would be ignored by the writer and are deliberately NOT exposed.

  /// <summary>Compression level 1..9 (higher = smaller/slower). The optimizer
  /// searches these to find the smallest output for the given input.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: "3",
      AllowedValues: ["1", "2", "3", "4", "5", "6", "7", "8", "9"],
      Description: "Zstd compression level (1 = fastest, 9 = smallest)."),
  ];

  /// <summary>Parses the Zstd compression level from the format-specific options,
  /// clamped to the supported 1..9 range; falls back to the default (3).</summary>
  internal static int ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    if (raw is not null && int.TryParse(raw, out var lvl)) return Math.Clamp(lvl, 1, 9);
    return options.Level is { } l ? Math.Clamp(l, 1, 9) : 3;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) {
    using var ds = new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      compressionLevel: ParseLevel(options), leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Performs the compress optimal operation.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      compressionLevel: 9, leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Performs the wrap decompress operation.
  /// </summary>
  public Stream? WrapDecompress(Stream input) =>
    new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  /// <summary>
  /// Performs the wrap compress operation.
  /// </summary>
  public Stream? WrapCompress(Stream output) =>
    new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
