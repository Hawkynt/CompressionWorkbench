#pragma warning disable CS1591
namespace FileFormat.Matlab;

/// <summary>
/// Represents a matlab constants.
/// </summary>
public static class MatlabConstants {

  /// <summary>Total bytes in the MAT v5 file header.</summary>
  public const int HeaderSize = 128;

  /// <summary>Length of the ASCII description portion of the header.</summary>
  public const int DescriptionLength = 116;

  /// <summary>Offset of the version field within the header.</summary>
  public const int VersionOffset = 124;

  /// <summary>Offset of the endian indicator (2 ASCII chars) within the header.</summary>
  public const int EndianIndicatorOffset = 126;

  /// <summary>Expected version value (0x0100, written little- or big-endian per file).</summary>
  public const ushort ExpectedVersion = 0x0100;

  /// <summary>ASCII bytes "IM" (little-endian indicator).</summary>
  public static readonly byte[] EndianIM = "IM"u8.ToArray();

  /// <summary>ASCII bytes "MI" (big-endian indicator).</summary>
  public static readonly byte[] EndianMI = "MI"u8.ToArray();

  /// <summary>"MATLAB" prefix used as the detection magic for MAT v5 files.</summary>
  public static readonly byte[] Magic = "MATLAB"u8.ToArray();

  // MAT v5 data element type codes
  /// <summary>
  /// Defines the mi int 8 constant value.
  /// </summary>
public const uint MiINT8 = 1;
  /// <summary>
  /// Defines the mi uint 8 constant value.
  /// </summary>
public const uint MiUINT8 = 2;
  /// <summary>
  /// Defines the mi int 16 constant value.
  /// </summary>
public const uint MiINT16 = 3;
  /// <summary>
  /// Defines the mi uint 16 constant value.
  /// </summary>
public const uint MiUINT16 = 4;
  /// <summary>
  /// Defines the mi int 32 constant value.
  /// </summary>
public const uint MiINT32 = 5;
  /// <summary>
  /// Defines the mi uint 32 constant value.
  /// </summary>
public const uint MiUINT32 = 6;
  /// <summary>
  /// Defines the mi single constant value.
  /// </summary>
public const uint MiSINGLE = 7;
  /// <summary>
  /// Defines the mi double constant value.
  /// </summary>
public const uint MiDOUBLE = 9;
  /// <summary>
  /// Defines the mi int 64 constant value.
  /// </summary>
public const uint MiINT64 = 12;
  /// <summary>
  /// Defines the mi uint 64 constant value.
  /// </summary>
public const uint MiUINT64 = 13;
  /// <summary>
  /// Defines the mi matrix constant value.
  /// </summary>
public const uint MiMATRIX = 14;
  /// <summary>
  /// Defines the mi compressed constant value.
  /// </summary>
public const uint MiCOMPRESSED = 15;
  /// <summary>
  /// Defines the mi utf 8 constant value.
  /// </summary>
public const uint MiUTF8 = 16;
  /// <summary>
  /// Defines the mi utf 16 constant value.
  /// </summary>
public const uint MiUTF16 = 17;
  /// <summary>
  /// Defines the mi utf 32 constant value.
  /// </summary>
public const uint MiUTF32 = 18;

  // MATLAB array class codes (low byte of ArrayFlags first uint32)
  /// <summary>
  /// Defines the mx cell class constant value.
  /// </summary>
public const byte MxCELL_CLASS = 1;
  /// <summary>
  /// Defines the mx struct class constant value.
  /// </summary>
public const byte MxSTRUCT_CLASS = 2;
  /// <summary>
  /// Defines the mx object class constant value.
  /// </summary>
public const byte MxOBJECT_CLASS = 3;
  /// <summary>
  /// Defines the mx char class constant value.
  /// </summary>
public const byte MxCHAR_CLASS = 4;
  /// <summary>
  /// Defines the mx sparse class constant value.
  /// </summary>
public const byte MxSPARSE_CLASS = 5;
  /// <summary>
  /// Defines the mx double class constant value.
  /// </summary>
public const byte MxDOUBLE_CLASS = 6;
  /// <summary>
  /// Defines the mx single class constant value.
  /// </summary>
public const byte MxSINGLE_CLASS = 7;
  /// <summary>
  /// Defines the mx int 8 class constant value.
  /// </summary>
public const byte MxINT8_CLASS = 8;
  /// <summary>
  /// Defines the mx uint 8 class constant value.
  /// </summary>
public const byte MxUINT8_CLASS = 9;
  /// <summary>
  /// Defines the mx int 16 class constant value.
  /// </summary>
public const byte MxINT16_CLASS = 10;
  /// <summary>
  /// Defines the mx uint 16 class constant value.
  /// </summary>
public const byte MxUINT16_CLASS = 11;
  /// <summary>
  /// Defines the mx int 32 class constant value.
  /// </summary>
public const byte MxINT32_CLASS = 12;
  /// <summary>
  /// Defines the mx uint 32 class constant value.
  /// </summary>
public const byte MxUINT32_CLASS = 13;
  /// <summary>
  /// Defines the mx int 64 class constant value.
  /// </summary>
public const byte MxINT64_CLASS = 14;
  /// <summary>
  /// Defines the mx uint 64 class constant value.
  /// </summary>
public const byte MxUINT64_CLASS = 15;
  /// <summary>
  /// Defines the mx function class constant value.
  /// </summary>
public const byte MxFUNCTION_CLASS = 16;
  /// <summary>
  /// Defines the mx opaque class constant value.
  /// </summary>
public const byte MxOPAQUE_CLASS = 17;
  /// <summary>
  /// Defines the mx logical class constant value.
  /// </summary>
public const byte MxLOGICAL_CLASS = 18;

  /// <summary>Maps a MATLAB class code to a human-readable name (used in metadata.ini).</summary>
  public static string ClassName(byte classCode) => classCode switch {
    MxCELL_CLASS => "cell",
    MxSTRUCT_CLASS => "struct",
    MxOBJECT_CLASS => "object",
    MxCHAR_CLASS => "char",
    MxSPARSE_CLASS => "sparse",
    MxDOUBLE_CLASS => "double",
    MxSINGLE_CLASS => "single",
    MxINT8_CLASS => "int8",
    MxUINT8_CLASS => "uint8",
    MxINT16_CLASS => "int16",
    MxUINT16_CLASS => "uint16",
    MxINT32_CLASS => "int32",
    MxUINT32_CLASS => "uint32",
    MxINT64_CLASS => "int64",
    MxUINT64_CLASS => "uint64",
    MxFUNCTION_CLASS => "function",
    MxOPAQUE_CLASS => "opaque",
    MxLOGICAL_CLASS => "logical",
    _ => "unknown_" + classCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };
}
