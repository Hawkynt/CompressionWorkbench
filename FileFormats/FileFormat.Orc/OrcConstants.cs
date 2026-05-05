#pragma warning disable CS1591
namespace FileFormat.Orc;

public static class OrcConstants {

  /// <summary>Apache ORC magic bytes ("ORC") at file start and inside the PostScript magic field.</summary>
  public static readonly byte[] Magic = [0x4F, 0x52, 0x43];

  /// <summary>Length of the ORC magic in bytes.</summary>
  public const int MagicLength = 3;

  /// <summary>Minimum file size: 3-byte leading magic + at least 1-byte PostScript length trailer.</summary>
  public const int MinFileLength = MagicLength + 1;

  // PostScript Protobuf field numbers.
  public const int PsFieldFooterLength = 1;
  public const int PsFieldCompression = 2;
  public const int PsFieldCompressionBlockSize = 3;
  public const int PsFieldVersion = 4;
  public const int PsFieldMetadataLength = 5;
  public const int PsFieldWriterVersion = 6;
  public const int PsFieldStripeStatisticsLength = 7;
  public const int PsFieldMagic = 8000;

  // Footer Protobuf field numbers (only meaningful when compression == NONE).
  public const int FooterFieldHeaderLength = 1;
  public const int FooterFieldStripes = 2;
  public const int FooterFieldTypes = 3;
  public const int FooterFieldUserMetadata = 4;
  public const int FooterFieldNumberOfRows = 5;

  // Protobuf wire types.
  public const int WireVarint = 0;
  public const int Wire64Bit = 1;
  public const int WireLengthDelimited = 2;
  public const int Wire32Bit = 5;

  // ORC compression enum (PostScript field 2).
  public const int CompressionNone = 0;
  public const int CompressionZlib = 1;
  public const int CompressionSnappy = 2;
  public const int CompressionLzo = 3;
  public const int CompressionLz4 = 4;
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
