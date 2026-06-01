#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zstd;

public sealed class ZstdFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Zstd";
  public string DisplayName => "Zstandard";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".zst";
  public IReadOnlyList<string> Extensions => [".zst", ".zstd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x28, 0xB5, 0x2F, 0xFD], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zstd", "Zstd", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Facebook's modern codec, excellent speed/ratio tradeoff with dictionary support";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
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
    var raw = options.FormatSpecific?.GetValueOrDefault("Level");
    if (raw is not null && int.TryParse(raw, out var lvl)) return Math.Clamp(lvl, 1, 9);
    return options.Level is { } l ? Math.Clamp(l, 1, 9) : 3;
  }

  public void Decompress(Stream input, Stream output) {
    using var ds = new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      compressionLevel: ParseLevel(options), leaveOpen: true);
    input.CopyTo(cs);
  }
  public void CompressOptimal(Stream input, Stream output) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      compressionLevel: 9, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
