#pragma warning disable CS1591
namespace FileFormat.Wbn;

/// <summary>
/// Represents a wbn constants.
/// </summary>
public static class WbnConstants {

  /// <summary>
  /// Web Bundle magic header. Decodes as a CBOR array-of-4 (0x84) whose first element is a
  /// length-8 byte string (0x48) containing the UTF-8 bytes of the globe + package emojis
  /// (U+1F310 + U+1F4E6).
  /// </summary>
  public static readonly byte[] Magic = [
    0x84, 0x48,
    0xF0, 0x9F, 0x8C, 0xD0,
    0xF0, 0x9F, 0x93, 0xA6,
  ];

    /// <summary>
  /// Defines the magic length constant value.
  /// </summary>
public const int MagicLength = 10;

  /// <summary>Length of the version field that immediately follows the magic byte string. Encoded as a CBOR length-4 byte string (0x44 + 4 bytes).</summary>
  public const int VersionFieldLength = 4;

  // CBOR major type constants (high 3 bits of the leading byte, shifted down).
    /// <summary>
  /// Defines the major type unsigned int constant value.
  /// </summary>
public const byte MajorTypeUnsignedInt = 0;
    /// <summary>
  /// Defines the major type negative int constant value.
  /// </summary>
public const byte MajorTypeNegativeInt = 1;
    /// <summary>
  /// Defines the major type byte string constant value.
  /// </summary>
public const byte MajorTypeByteString = 2;
    /// <summary>
  /// Defines the major type text string constant value.
  /// </summary>
public const byte MajorTypeTextString = 3;
    /// <summary>
  /// Defines the major type array constant value.
  /// </summary>
public const byte MajorTypeArray = 4;
    /// <summary>
  /// Defines the major type map constant value.
  /// </summary>
public const byte MajorTypeMap = 5;
    /// <summary>
  /// Defines the major type tag constant value.
  /// </summary>
public const byte MajorTypeTag = 6;
    /// <summary>
  /// Defines the major type simple or float constant value.
  /// </summary>
public const byte MajorTypeSimpleOrFloat = 7;

  /// <summary>CBOR break stop-code, used inside indefinite-length items.</summary>
  public const byte BreakStopCode = 0xFF;
}
