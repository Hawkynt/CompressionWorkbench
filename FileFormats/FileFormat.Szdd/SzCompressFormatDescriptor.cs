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
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "SzCompress";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SZ (old MS COMPRESS)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".sz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzss", "LZSS")];
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
public string Description => "Old Microsoft 'SZ ' COMPRESS LZSS (pre-SZDD / QBasic era), read + write";

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => SzddStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => SzddStream.CompressQBasic(input, output);
}
