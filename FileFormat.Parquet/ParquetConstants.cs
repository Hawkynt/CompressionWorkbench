#pragma warning disable CS1591
namespace FileFormat.Parquet;

public static class ParquetConstants {

  /// <summary>Apache Parquet magic bytes ("PAR1") at file start and just before the trailer.</summary>
  public static readonly byte[] Magic = [0x50, 0x41, 0x52, 0x31];

  /// <summary>Length of the Parquet magic in bytes.</summary>
  public const int MagicLength = 4;

  /// <summary>Trailer length: 4-byte LE footer length + 4-byte trailing magic.</summary>
  public const int TrailerLength = 8;

  // Thrift compact protocol type codes used in field headers.
  public const byte TypeStop = 0;
  public const byte TypeBoolTrue = 1;
  public const byte TypeBoolFalse = 2;
  public const byte TypeByte = 3;
  public const byte TypeI16 = 4;
  public const byte TypeI32 = 5;
  public const byte TypeI64 = 6;
  public const byte TypeDouble = 7;
  public const byte TypeBinary = 8;
  public const byte TypeList = 9;
  public const byte TypeSet = 10;
  public const byte TypeMap = 11;
  public const byte TypeStruct = 12;
}
