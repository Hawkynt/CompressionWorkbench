#pragma warning disable CS1591
namespace FileFormat.Arsc;

/// <summary>
/// Represents an arsc constants.
/// </summary>
public static class ArscConstants {

  /// <summary>
  /// Defines the res null type constant value.
  /// </summary>
public const ushort ResNullType = 0x0001;
  /// <summary>
  /// Defines the res string pool type constant value.
  /// </summary>
public const ushort ResStringPoolType = 0x0002;
  /// <summary>
  /// Defines the res table type constant value.
  /// </summary>
public const ushort ResTableType = 0x0003;
  /// <summary>
  /// Defines the res table package type constant value.
  /// </summary>
public const ushort ResTablePackageType = 0x0200;
  /// <summary>
  /// Defines the res table type type constant value.
  /// </summary>
public const ushort ResTableTypeType = 0x0201;
  /// <summary>
  /// Defines the res table type spec type constant value.
  /// </summary>
public const ushort ResTableTypeSpecType = 0x0202;
  /// <summary>
  /// Defines the res table library type constant value.
  /// </summary>
public const ushort ResTableLibraryType = 0x0203;

  /// <summary>
  /// Defines the chunk header size constant value.
  /// </summary>
public const int ChunkHeaderSize = 8;

  /// <summary>
  /// Defines the res table header size constant value.
  /// </summary>
public const int ResTableHeaderSize = 12;

  /// <summary>
  /// Defines the package name length chars constant value.
  /// </summary>
public const int PackageNameLengthChars = 128;

  /// <summary>
  /// Defines the package name length bytes constant value.
  /// </summary>
public const int PackageNameLengthBytes = PackageNameLengthChars * 2;

  /// <summary>
  /// Defines the string pool flag sorted constant value.
  /// </summary>
public const uint StringPoolFlagSorted = 1u << 0;

  /// <summary>
  /// Defines the string pool flag utf 8 constant value.
  /// </summary>
public const uint StringPoolFlagUtf8 = 1u << 8;

  /// <summary>
  /// Provides the res table magic value.
  /// </summary>
public static readonly byte[] ResTableMagic = [0x03, 0x00, 0x0C, 0x00];
}
