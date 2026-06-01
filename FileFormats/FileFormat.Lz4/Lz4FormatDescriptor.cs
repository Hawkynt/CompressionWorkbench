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
  /// <summary>The encoder strength: <c>Fast</c> (single-slot hash table) or
  /// <c>Hc</c> (high compression via hash chains). Both emit a fully conformant
  /// LZ4 frame; only the match search differs. The optimizer searches these to
  /// find the smallest output for the given input.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: nameof(Lz4CompressionLevel.Fast),
      AllowedValues: [nameof(Lz4CompressionLevel.Fast), nameof(Lz4CompressionLevel.Hc)],
      Description: "Fast = quickest; Hc = high compression (smaller, slower)."),
  ];

  /// <summary>Parses the LZ4 encoder level from the format-specific options;
  /// falls back to <see cref="Lz4CompressionLevel.Fast"/> when absent or unknown.</summary>
  internal static Lz4CompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("Level");
    return Enum.TryParse<Lz4CompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : Lz4CompressionLevel.Fast;
  }

  public void Decompress(Stream input, Stream output) {
    var r = new Lz4FrameReader(input);
    output.Write(r.Read());
  }

  public void Compress(Stream input, Stream output) => CompressFrame(input, output, Lz4CompressionLevel.Fast);

  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    CompressFrame(input, output, ParseLevel(options));

  public void CompressOptimal(Stream input, Stream output) => CompressFrame(input, output, Lz4CompressionLevel.Hc);

  private static void CompressFrame(Stream input, Stream output, Lz4CompressionLevel level) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var w = new Lz4FrameWriter(output, level: level);
    w.Write(ms.ToArray());
  }
}
