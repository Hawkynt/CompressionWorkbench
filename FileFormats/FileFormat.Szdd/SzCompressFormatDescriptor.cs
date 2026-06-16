#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Szdd;

/// <summary>
/// The older "SZ " Microsoft COMPRESS variant (pre-SZDD; QBasic-era
/// <c>COMPRESS.EXE</c>). Magic <c>53 5A 20 88 F0 27 33 D1</c>, a 12-byte header
/// (8-byte magic + little-endian u32 uncompressed length) and the same 4096-byte
/// ring LZSS body as <see cref="SzddFormatDescriptor"/>. Neither the legacy SZDD
/// reader nor 7-Zip handles this variant; here it is fully read + write
/// (<see cref="Compress"/> emits the "SZ " header, <see cref="Decompress"/>
/// auto-detects either variant).
/// </summary>
public sealed class SzCompressFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "SzCompress";
  public string DisplayName => "SZ (old MS COMPRESS)";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".sz";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzss", "LZSS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Old Microsoft 'SZ ' COMPRESS LZSS (pre-SZDD / QBasic era), read + write";

  public void Decompress(Stream input, Stream output) => SzddStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => SzddStream.CompressQBasic(input, output);
}
