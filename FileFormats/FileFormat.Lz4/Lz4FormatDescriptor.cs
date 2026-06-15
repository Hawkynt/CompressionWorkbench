#pragma warning disable CS1591 // Missing XML comment

using Compression.Core.Dictionary.Lz4;
using Compression.Registry;

namespace FileFormat.Lz4;

public sealed class Lz4FormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Lz4";
  public string DisplayName => "LZ4";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".lz4";
  public IReadOnlyList<string> Extensions => [".lz4"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x04, 0x22, 0x4D, 0x18], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lz4", "LZ4", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Extremely fast LZ77 with byte-aligned tokens, optimized for speed";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // The LZ4 frame descriptor (FLG/BD bytes) is self-describing, so each axis
  // below is read back losslessly by any conformant reader. The optimizer
  // searches these to find the smallest output for the caller's data.
  //
  // Block independence is deliberately NOT exposed: the frame writer hardwires
  // the independence bit (FLG bit 5 = 1) and does not implement linked-block
  // back-references across block boundaries, so a "linked" toggle would not be
  // honored. Encoder strength is Fast vs. Hc (the two match finders we ship).

  /// <summary>Maps each <c>BlockSize</c> dropdown label to its max-block size in bytes.</summary>
  private static readonly IReadOnlyDictionary<string, int> BlockSizesByLabel =
    new Dictionary<string, int> {
      ["64 KB"] = 64 * 1024,
      ["256 KB"] = 256 * 1024,
      ["1 MB"] = 1024 * 1024,
      ["4 MB"] = 4 * 1024 * 1024,
    };

  /// <summary>The tunable LZ4 frame knobs: encoder strength, max block size, and the
  /// two optional xxHash32 checksums (content + per-block). Every combination yields
  /// a fully conformant, self-describing LZ4 frame.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: nameof(Lz4CompressionLevel.Fast),
      AllowedValues: [nameof(Lz4CompressionLevel.Fast), nameof(Lz4CompressionLevel.Hc)],
      Description: "Fast = quickest; Hc = high compression (smaller, slower)."),
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Max block size",
      Kind: FormatOptionKind.Enum,
      Default: "4 MB",
      AllowedValues: ["64 KB", "256 KB", "1 MB", "4 MB"],
      Description: "Maximum size of each compressed block; smaller blocks bound memory but limit match range."),
    new FormatOptionDescriptor(
      Key: "ContentChecksum",
      DisplayName: "Content checksum",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "Append an xxHash32 checksum of the whole content at the end of the frame."),
    new FormatOptionDescriptor(
      Key: "BlockChecksum",
      DisplayName: "Per-block checksum",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Append an xxHash32 checksum after each block (detects per-block corruption)."),
  ];

  /// <summary>Parses the LZ4 encoder level from the format-specific options;
  /// falls back to <see cref="Lz4CompressionLevel.Fast"/> when absent or unknown.</summary>
  internal static Lz4CompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("Level");
    return Enum.TryParse<Lz4CompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : Lz4CompressionLevel.Fast;
  }

  /// <summary>Parses the max-block-size label into bytes; falls back to the 4 MiB default.</summary>
  internal static int ParseBlockSize(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("BlockSize");
    return raw is not null && BlockSizesByLabel.TryGetValue(raw, out var bytes)
      ? bytes
      : Lz4Constants.MaxBlockSize;
  }

  /// <summary>Parses a boolean knob, falling back to <paramref name="fallback"/> when absent/unparseable.</summary>
  private static bool ParseBool(FormatCreateOptions options, string key, bool fallback) {
    var raw = options.FormatSpecific?.GetValueOrDefault(key);
    return raw is not null && bool.TryParse(raw, out var value) ? value : fallback;
  }

  /// <summary>Parses the content-checksum toggle; defaults to <c>true</c> (the historical default).</summary>
  internal static bool ParseContentChecksum(FormatCreateOptions options) => ParseBool(options, "ContentChecksum", true);

  /// <summary>Parses the per-block-checksum toggle; defaults to <c>false</c> (the historical default).</summary>
  internal static bool ParseBlockChecksum(FormatCreateOptions options) => ParseBool(options, "BlockChecksum", false);

  public void Decompress(Stream input, Stream output) {
    var r = new Lz4FrameReader(input);
    output.Write(r.Read());
  }

  public void Compress(Stream input, Stream output) =>
    CompressFrame(input, output, Lz4CompressionLevel.Fast, Lz4Constants.MaxBlockSize, contentChecksum: true, blockChecksum: false);

  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    CompressFrame(input, output, ParseLevel(options), ParseBlockSize(options),
      ParseContentChecksum(options), ParseBlockChecksum(options));

  public void CompressOptimal(Stream input, Stream output) =>
    CompressFrame(input, output, Lz4CompressionLevel.Hc, Lz4Constants.MaxBlockSize, contentChecksum: true, blockChecksum: false);

  private static void CompressFrame(Stream input, Stream output, Lz4CompressionLevel level,
      int blockMaxSize, bool contentChecksum, bool blockChecksum) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var w = new Lz4FrameWriter(output, blockMaxSize, contentChecksum, blockChecksum, level);
    w.Write(ms.ToArray());
  }
}
