#pragma warning disable CS1591
namespace FileFormat.Matlab;

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
  public const uint MiINT8 = 1;
  public const uint MiUINT8 = 2;
  public const uint MiINT16 = 3;
  public const uint MiUINT16 = 4;
  public const uint MiINT32 = 5;
  public const uint MiUINT32 = 6;
  public const uint MiSINGLE = 7;
  public const uint MiDOUBLE = 9;
  public const uint MiINT64 = 12;
  public const uint MiUINT64 = 13;
  public const uint MiMATRIX = 14;
  public const uint MiCOMPRESSED = 15;
  public const uint MiUTF8 = 16;
  public const uint MiUTF16 = 17;
  public const uint MiUTF32 = 18;

  // MATLAB array class codes (low byte of ArrayFlags first uint32)
  public const byte MxCELL_CLASS = 1;
  public const byte MxSTRUCT_CLASS = 2;
  public const byte MxOBJECT_CLASS = 3;
  public const byte MxCHAR_CLASS = 4;
  public const byte MxSPARSE_CLASS = 5;
  public const byte MxDOUBLE_CLASS = 6;
  public const byte MxSINGLE_CLASS = 7;
  public const byte MxINT8_CLASS = 8;
  public const byte MxUINT8_CLASS = 9;
  public const byte MxINT16_CLASS = 10;
  public const byte MxUINT16_CLASS = 11;
  public const byte MxINT32_CLASS = 12;
  public const byte MxUINT32_CLASS = 13;
  public const byte MxINT64_CLASS = 14;
  public const byte MxUINT64_CLASS = 15;
  public const byte MxFUNCTION_CLASS = 16;
  public const byte MxOPAQUE_CLASS = 17;
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
