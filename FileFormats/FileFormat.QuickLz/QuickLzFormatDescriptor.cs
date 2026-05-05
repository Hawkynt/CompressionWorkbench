#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.QuickLz;

public sealed class QuickLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "QuickLz";
  public string DisplayName => "QuickLZ";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".quicklz";
  public IReadOnlyList<string> Extensions => [".quicklz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // QuickLZ has no reliable magic bytes (just a flags byte with bit 6 set) — detect by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("level1", "Level 1")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Fast LZ77 compressor by Lasse Mikkel Reinhold";

  public void Decompress(Stream input, Stream output) => QuickLzStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => QuickLzStream.Compress(input, output);
}
