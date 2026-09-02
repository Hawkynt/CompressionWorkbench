#pragma warning disable CS1591
namespace FileFormat.MatlabV4;

/// <summary>
/// Represents a matlab v 4 constants.
/// </summary>
public static class MatlabV4Constants {

  /// <summary>Total bytes in the per-record fixed header (Type, Rows, Cols, ImagFlag, NameLength).</summary>
  public const int RecordHeaderSize = 20;

  /// <summary>Maximum plausible variable name length (including the null terminator) used for header validation.</summary>
  public const int MaxNameLength = 256;

  // M-digit (high digit of MOPT) — host machine endianness/format
  /// <summary>
  /// Defines the machine le constant value.
  /// </summary>
  public const uint MachineLE = 0;       // Intel little-endian
  /// <summary>
  /// Defines the machine be constant value.
  /// </summary>
  public const uint MachineBE = 1;       // Motorola/Sun big-endian
  /// <summary>
  /// Defines the machine vax d constant value.
  /// </summary>
  public const uint MachineVaxD = 2;     // VAX D-float
  /// <summary>
  /// Defines the machine vax g constant value.
  /// </summary>
  public const uint MachineVaxG = 3;     // VAX G-float
  /// <summary>
  /// Defines the machine cray constant value.
  /// </summary>
  public const uint MachineCray = 4;     // Cray
  /// <summary>
  /// Defines the max machine constant value.
  /// </summary>
  public const uint MaxMachine = 4;

  // P-digit — element precision
  /// <summary>
  /// Defines the precision double constant value.
  /// </summary>
  public const uint PrecisionDouble = 0; // 8 bytes
  /// <summary>
  /// Defines the precision single constant value.
  /// </summary>
  public const uint PrecisionSingle = 1; // 4 bytes
  /// <summary>
  /// Defines the precision int 32 constant value.
  /// </summary>
  public const uint PrecisionInt32 = 2;  // 4 bytes
  /// <summary>
  /// Defines the precision int 16 constant value.
  /// </summary>
  public const uint PrecisionInt16 = 3;  // 2 bytes
  /// <summary>
  /// Defines the precision u int 16 constant value.
  /// </summary>
  public const uint PrecisionUInt16 = 4; // 2 bytes
  /// <summary>
  /// Defines the precision u int 8 constant value.
  /// </summary>
  public const uint PrecisionUInt8 = 5;  // 1 byte
  /// <summary>
  /// Defines the max precision constant value.
  /// </summary>
  public const uint MaxPrecision = 5;

  // T-digit — matrix type
  /// <summary>
  /// Defines the type full numeric constant value.
  /// </summary>
  public const uint TypeFullNumeric = 0;
  /// <summary>
  /// Defines the type text constant value.
  /// </summary>
  public const uint TypeText = 1;
  /// <summary>
  /// Defines the type sparse constant value.
  /// </summary>
  public const uint TypeSparse = 2;
  /// <summary>
  /// Defines the max type constant value.
  /// </summary>
  public const uint MaxType = 2;

  /// <summary>Bytes-per-element for each precision code; index by P-digit.</summary>
  public static int ElementSize(uint precision) => precision switch {
    PrecisionDouble => 8,
    PrecisionSingle => 4,
    PrecisionInt32 => 4,
    PrecisionInt16 => 2,
    PrecisionUInt16 => 2,
    PrecisionUInt8 => 1,
    _ => 0,
  };

  /// <summary>Maps a (T,P) MOPT pair to a human-readable type name surfaced in metadata.ini.</summary>
  public static string TypeName(uint matrixType, uint precision) {
    if (matrixType == TypeText) return "text";
    if (matrixType == TypeSparse) return "sparse";
    return precision switch {
      PrecisionDouble => "double",
      PrecisionSingle => "single",
      PrecisionInt32 => "int32",
      PrecisionInt16 => "int16",
      PrecisionUInt16 => "uint16",
      PrecisionUInt8 => "uint8",
      _ => "unknown_p" + precision.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
  }

  /// <summary>Maps a machine code (M-digit) to a metadata-friendly endian token.</summary>
  public static string MachineName(uint machine) => machine switch {
    MachineLE => "LE",
    MachineBE => "BE",
    MachineVaxD => "VAX-D",
    MachineVaxG => "VAX-G",
    MachineCray => "Cray",
    _ => "unknown_m" + machine.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };
}
