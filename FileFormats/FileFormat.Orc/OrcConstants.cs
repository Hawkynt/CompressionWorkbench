#pragma warning disable CS1591
namespace FileFormat.Orc;

/// <summary>
/// Represents an orc constants.
/// </summary>
public static class OrcConstants {

  /// <summary>Apache ORC magic bytes ("ORC") at file start and inside the PostScript magic field.</summary>
  public static readonly byte[] Magic = [0x4F, 0x52, 0x43];

  /// <summary>Length of the ORC magic in bytes.</summary>
  public const int MagicLength = 3;

  /// <summary>Minimum file size: 3-byte leading magic + at least 1-byte PostScript length trailer.</summary>
  public const int MinFileLength = MagicLength + 1;

  // PostScript Protobuf field numbers.
  /// <summary>
  /// Defines the ps field footer length constant value.
  /// </summary>
  public const int PsFieldFooterLength = 1;
  /// <summary>
  /// Defines the ps field compression constant value.
  /// </summary>
  public const int PsFieldCompression = 2;
  /// <summary>
  /// Defines the ps field compression block size constant value.
  /// </summary>
  public const int PsFieldCompressionBlockSize = 3;
  /// <summary>
  /// Defines the ps field version constant value.
  /// </summary>
  public const int PsFieldVersion = 4;
  /// <summary>
  /// Defines the ps field metadata length constant value.
  /// </summary>
  public const int PsFieldMetadataLength = 5;
  /// <summary>
  /// Defines the ps field writer version constant value.
  /// </summary>
  public const int PsFieldWriterVersion = 6;
  /// <summary>
  /// Defines the ps field stripe statistics length constant value.
  /// </summary>
  public const int PsFieldStripeStatisticsLength = 7;
  /// <summary>
  /// Defines the ps field magic constant value.
  /// </summary>
  public const int PsFieldMagic = 8000;

  // Footer Protobuf field numbers (only meaningful when compression == NONE).
  /// <summary>
  /// Defines the footer field header length constant value.
  /// </summary>
  public const int FooterFieldHeaderLength = 1;
  /// <summary>
  /// Defines the footer field stripes constant value.
  /// </summary>
  public const int FooterFieldStripes = 2;
  /// <summary>
  /// Defines the footer field types constant value.
  /// </summary>
  public const int FooterFieldTypes = 3;
  /// <summary>
  /// Defines the footer field user metadata constant value.
  /// </summary>
  public const int FooterFieldUserMetadata = 4;
  /// <summary>
  /// Defines the footer field number of rows constant value.
  /// </summary>
  public const int FooterFieldNumberOfRows = 5;

  // Protobuf wire types.
  /// <summary>
  /// Defines the wire varint constant value.
  /// </summary>
  public const int WireVarint = 0;
  /// <summary>
  /// Defines the wire 64 bit constant value.
  /// </summary>
  public const int Wire64Bit = 1;
  /// <summary>
  /// Defines the wire length delimited constant value.
  /// </summary>
  public const int WireLengthDelimited = 2;
  /// <summary>
  /// Defines the wire 32 bit constant value.
  /// </summary>
  public const int Wire32Bit = 5;

  // ORC compression enum (PostScript field 2).
  /// <summary>
  /// Defines the compression none constant value.
  /// </summary>
  public const int CompressionNone = 0;
  /// <summary>
  /// Defines the compression zlib constant value.
  /// </summary>
  public const int CompressionZlib = 1;
  /// <summary>
  /// Defines the compression snappy constant value.
  /// </summary>
  public const int CompressionSnappy = 2;
  /// <summary>
  /// Defines the compression lzo constant value.
  /// </summary>
  public const int CompressionLzo = 3;
  /// <summary>
  /// Defines the compression lz 4 constant value.
  /// </summary>
  public const int CompressionLz4 = 4;
  /// <summary>
  /// Defines the compression zstd constant value.
  /// </summary>
  public const int CompressionZstd = 5;

  /// <summary>Maps the ORC compression enum value to a stable string label.</summary>
  public static string CompressionName(int value) => value switch {
    CompressionNone => "NONE",
    CompressionZlib => "ZLIB",
    CompressionSnappy => "SNAPPY",
    CompressionLzo => "LZO",
    CompressionLz4 => "LZ4",
    CompressionZstd => "ZSTD",
    _ => "unknown",
  };
}
