#pragma warning disable CS1591
using Compression.Core.Dictionary.Snappy;
using Compression.Registry;

namespace FileFormat.Snappy;

/// <summary>
/// Describes snappy format.
/// </summary>
public sealed class SnappyFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Snappy";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Snappy";
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
  public string DefaultExtension => ".sz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sz", ".snappy"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xFF, 0x06, 0x00, 0x00, 0x73, 0x4E, 0x61, 0x50], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("snappy", "Snappy", SupportsOptimize: true)];
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
  public string Description => "Google's fast compressor, designed for speed over ratio";

  private static readonly IReadOnlyDictionary<string, int> BlockSizesByLabel =
    new Dictionary<string, int> {
      ["4 KB"] = 4 * 1024,
      ["8 KB"] = 8 * 1024,
      ["16 KB"] = 16 * 1024,
      ["32 KB"] = 32 * 1024,
      ["64 KB"] = SnappyFrameWriter.MaxBlockSize,
    };

  /// <summary>
  /// Encoder parameters searched by the generic compression optimizer. Both axes affect only how
  /// a conforming Snappy stream is encoded; neither requires private decoder state.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Framing chunk size",
      Kind: FormatOptionKind.Enum,
      Default: "64 KB",
      AllowedValues: ["4 KB", "8 KB", "16 KB", "32 KB", "64 KB"],
      Description: "Maximum uncompressed bytes per data chunk. Smaller chunks can isolate incompressible regions but add framing overhead."),
    new FormatOptionDescriptor(
      Key: "HashTableBits",
      DisplayName: "Hash table bits",
      Kind: FormatOptionKind.Integer,
      Default: SnappyConstants.HashTableBits.ToString(),
      AllowedValues: ["8", "9", "10", "11", "12", "13", "14", "15"],
      Description: "Log2 of the encoder hash-table size. This changes match selection only and is not stored in the Snappy stream."),
  ];

  internal static int ParseBlockSize(FormatCreateOptions options) {
    var raw = options.GetString("BlockSize");
    return raw is not null && BlockSizesByLabel.TryGetValue(raw, out var value)
      ? value
      : SnappyFrameWriter.MaxBlockSize;
  }

  internal static int ParseHashTableBits(FormatCreateOptions options) {
    var raw = options.GetString("HashTableBits");
    return int.TryParse(raw, out var value)
           && value is >= SnappyConstants.MinHashTableBits and <= SnappyConstants.MaxHashTableBits
      ? value
      : SnappyConstants.HashTableBits;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) {
    var r = new SnappyFrameReader(input);
    output.Write(r.Read());
  }

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) =>
    Compress(input, output, SnappyFrameWriter.MaxBlockSize, SnappyConstants.HashTableBits);

  /// <summary>
  /// Encodes the supplied input with the requested optimizer parameters.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    Compress(input, output, ParseBlockSize(options), ParseHashTableBits(options));

  /// <summary>
  /// Encodes using the largest framing chunk and hash table. Schema-driven callers search all
  /// declared combinations and are not limited to this fixed high-resource setting.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    Compress(input, output, SnappyFrameWriter.MaxBlockSize, SnappyConstants.MaxHashTableBits);

  private static void Compress(Stream input, Stream output, int blockSize, int hashTableBits) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    new SnappyFrameWriter(output, blockSize, hashTableBits).Write(ms.ToArray());
  }
}
