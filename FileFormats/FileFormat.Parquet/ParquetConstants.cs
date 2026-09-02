#pragma warning disable CS1591
namespace FileFormat.Parquet;

/// <summary>
/// Represents a parquet constants.
/// </summary>
public static class ParquetConstants {

  /// <summary>Apache Parquet magic bytes ("PAR1") at file start and just before the trailer.</summary>
  public static readonly byte[] Magic = [0x50, 0x41, 0x52, 0x31];

  /// <summary>Length of the Parquet magic in bytes.</summary>
  public const int MagicLength = 4;

  /// <summary>Trailer length: 4-byte LE footer length + 4-byte trailing magic.</summary>
  public const int TrailerLength = 8;

  // Thrift compact protocol type codes used in field headers.
    /// <summary>
  /// Defines the type stop constant value.
  /// </summary>
public const byte TypeStop = 0;
    /// <summary>
  /// Defines the type bool true constant value.
  /// </summary>
public const byte TypeBoolTrue = 1;
    /// <summary>
  /// Defines the type bool false constant value.
  /// </summary>
public const byte TypeBoolFalse = 2;
    /// <summary>
  /// Defines the type byte constant value.
  /// </summary>
public const byte TypeByte = 3;
    /// <summary>
  /// Defines the type i 16 constant value.
  /// </summary>
public const byte TypeI16 = 4;
    /// <summary>
  /// Defines the type i 32 constant value.
  /// </summary>
public const byte TypeI32 = 5;
    /// <summary>
  /// Defines the type i 64 constant value.
  /// </summary>
public const byte TypeI64 = 6;
    /// <summary>
  /// Defines the type double constant value.
  /// </summary>
public const byte TypeDouble = 7;
    /// <summary>
  /// Defines the type binary constant value.
  /// </summary>
public const byte TypeBinary = 8;
    /// <summary>
  /// Defines the type list constant value.
  /// </summary>
public const byte TypeList = 9;
    /// <summary>
  /// Defines the type set constant value.
  /// </summary>
public const byte TypeSet = 10;
    /// <summary>
  /// Defines the type map constant value.
  /// </summary>
public const byte TypeMap = 11;
    /// <summary>
  /// Defines the type struct constant value.
  /// </summary>
public const byte TypeStruct = 12;
}
