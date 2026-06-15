#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzma;
using Compression.Registry;

namespace FileFormat.Lzip;

public sealed class LzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Lzip";
  public string DisplayName => "Lzip";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".lz";
  public IReadOnlyList<string> Extensions => [".lz", ".lzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4C, 0x5A, 0x49, 0x50], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "LZMA with CRC32, designed for long-term archival";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // The Lzip header stores the dictionary-size byte, so each size below decodes
  // self-describingly; the level only changes the LZMA match-finder effort.
  //
  // lc/lp/pb are deliberately NOT exposed: the Lzip stream format fixes them at
  // lc=3/lp=0/pb=2 (no properties byte is stored in the member), so the writer
  // cannot vary them without producing a non-conformant stream. They are fixed
  // by the format, not a missing feature.

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

  /// <summary>The tunable Lzip knobs: LZMA effort level and dictionary size.</summary>
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
      Description: "LZMA match-finder effort (Fast = quickest, Best = smallest)."),
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size",
      Kind: FormatOptionKind.Enum,
      Default: "8 MB",
      AllowedValues: ["64 KB", "256 KB", "1 MB", "4 MB", "8 MB", "16 MB", "64 MB"],
      Description: "Sliding-window size; larger finds longer-range matches at higher memory cost."),
  ];

  /// <summary>Parses the LZMA compression level from the options, falling back to Normal.</summary>
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

  public void Decompress(Stream input, Stream output) => LzipStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzipStream.Compress(input, output);
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    LzipStream.Compress(input, output, ParseDictionarySize(options), ParseLevel(options));
  public void CompressOptimal(Stream input, Stream output) =>
    LzipStream.Compress(input, output, DefaultDictionarySize, LzmaCompressionLevel.Best);
}
