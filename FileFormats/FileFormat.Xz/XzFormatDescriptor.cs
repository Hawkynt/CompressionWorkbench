#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Streams;
using Compression.Registry;

namespace FileFormat.Xz;

/// <summary>
/// Describes xz format.
/// </summary>
public sealed class XzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Xz";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "XZ";
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
  public string DefaultExtension => ".xz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".xz"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], Confidence: 0.98)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
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
  public string Description => "LZMA2 container with CRC-64 integrity checks";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // The XZ block header stores the LZMA2 dictionary-size byte, so every
  // dictionary size below decodes self-describingly; the level only changes
  // the encoder's match-finder effort and has no decode-side footprint.
  //
  // lc/lp/pb are deliberately NOT exposed: the LZMA2 chunk control byte carries
  // a single properties byte, but the LZMA2 *encoder* (Lzma2Encoder) hardwires
  // lc=3/lp=0/pb=2 across all chunks and does not thread those bits, so exposing
  // them here would be a knob the writer ignores. Adding them needs an encoder
  // change, not a descriptor one.

  /// <summary>Maps each <c>DictionarySize</c> dropdown label to its size in bytes.</summary>
  private static readonly IReadOnlyDictionary<string, int> DictionarySizesByLabel =
    new Dictionary<string, int> {
      ["64 KB"] = 1 << 16,
      ["256 KB"] = 1 << 18,
      ["1 MB"] = 1 << 20,
      ["4 MB"] = 1 << 22,
      ["8 MB"] = 1 << 23,
      ["16 MB"] = 1 << 24,
      ["64 MB"] = 1 << 26,
    };

  private const int DefaultDictionarySize = 1 << 23;

  /// <summary>The tunable XZ knobs: LZMA2 effort level and dictionary size.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: nameof(LzmaCompressionLevel.Normal),
      AllowedValues: [
        nameof(LzmaCompressionLevel.Fast),
        nameof(LzmaCompressionLevel.Normal),
        nameof(LzmaCompressionLevel.Best),
      ],
      Description: "LZMA2 match-finder effort (Fast = quickest, Best = smallest)."),
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size",
      Kind: FormatOptionKind.Enum,
      Default: "8 MB",
      AllowedValues: ["64 KB", "256 KB", "1 MB", "4 MB", "8 MB", "16 MB", "64 MB"],
      Description: "Sliding-window size; larger finds longer-range matches at higher memory cost."),
  ];

  /// <summary>Parses the LZMA2 compression level from the options, falling back to Normal.</summary>
  internal static LzmaCompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("Level");
    return raw is not null && Enum.TryParse<LzmaCompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : LzmaCompressionLevel.Normal;
  }

  /// <summary>Parses the dictionary size label into bytes, falling back to the 8 MiB default.</summary>
  internal static int ParseDictionarySize(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("DictionarySize");
    return raw is not null && DictionarySizesByLabel.TryGetValue(raw, out var bytes)
      ? bytes
      : DefaultDictionarySize;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) {
    using var ds = new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) {
    using var cs = new XzStream(output, CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    using var cs = new XzStream(output, CompressionStreamMode.Compress,
      ParseDictionarySize(options), XzConstants.CheckCrc64, preFilters: null,
      ParseLevel(options), leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Performs the compress optimal operation.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) {
    using var cs = new XzStream(output, CompressionStreamMode.Compress,
      DefaultDictionarySize, XzConstants.CheckCrc64, preFilters: null,
      LzmaCompressionLevel.Best, leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Performs the wrap decompress operation.
  /// </summary>
  public Stream? WrapDecompress(Stream input) =>
    new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
  /// <summary>
  /// Performs the wrap compress operation.
  /// </summary>
  public Stream? WrapCompress(Stream output) =>
    new XzStream(output, CompressionStreamMode.Compress, leaveOpen: true);
}
