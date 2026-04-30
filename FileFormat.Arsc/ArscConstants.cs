#pragma warning disable CS1591
namespace FileFormat.Arsc;

public static class ArscConstants {

  public const ushort ResNullType = 0x0001;
  public const ushort ResStringPoolType = 0x0002;
  public const ushort ResTableType = 0x0003;
  public const ushort ResTablePackageType = 0x0200;
  public const ushort ResTableTypeType = 0x0201;
  public const ushort ResTableTypeSpecType = 0x0202;
  public const ushort ResTableLibraryType = 0x0203;

  public const int ChunkHeaderSize = 8;

  public const int ResTableHeaderSize = 12;

  public const int PackageNameLengthChars = 128;

  public const int PackageNameLengthBytes = PackageNameLengthChars * 2;

  public const uint StringPoolFlagSorted = 1u << 0;

  public const uint StringPoolFlagUtf8 = 1u << 8;

  public static readonly byte[] ResTableMagic = [0x03, 0x00, 0x0C, 0x00];
}
