namespace FileFormat.Zip;

/// <summary>
/// Constants defined by the PKWARE ZIP APPNOTE specification.
/// </summary>
internal static class ZipConstants {
  /// <summary>Local file header signature.</summary>
  public const uint LocalFileHeaderSignature = 0x04034B50;

  /// <summary>Central directory file header signature.</summary>
  public const uint CentralDirectorySignature = 0x02014B50;

  /// <summary>End of central directory record signature.</summary>
  public const uint EndOfCentralDirectorySignature = 0x06054B50;

  /// <summary>ZIP64 end of central directory record signature.</summary>
  public const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;

  /// <summary>ZIP64 end of central directory locator signature.</summary>
  public const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064B50;

  /// <summary>Data descriptor signature (optional).</summary>
  public const uint DataDescriptorSignature = 0x08074B50;

  /// <summary>Version needed to extract (1.0, baseline/legacy methods).</summary>
  public const ushort VersionNeeded10 = 10;

  /// <summary>Version needed to extract (2.0, Deflate/directories/traditional encryption).</summary>
  public const ushort VersionNeeded20 = 20;

  /// <summary>Version needed to extract (2.1, Deflate64).</summary>
  public const ushort VersionNeeded21 = 21;

  /// <summary>Version needed to extract (4.5, ZIP64).</summary>
  public const ushort VersionNeeded45 = 45;

  /// <summary>Version needed to extract (4.6, BZip2).</summary>
  public const ushort VersionNeeded46 = 46;

  /// <summary>Version needed to extract (5.1, AES encryption).</summary>
  public const ushort VersionNeeded51 = 51;

  /// <summary>Version needed to extract (6.3, LZMA/PPMd/Zstandard family used by this writer).</summary>
  public const ushort VersionNeeded63 = 63;

  /// <summary>Version made by (2.0 on FAT filesystem).</summary>
  public const ushort VersionMadeBy20 = 20;

  /// <summary>General purpose bit flag: data descriptor follows compressed data.</summary>
  public const ushort FlagDataDescriptor = 0x0008;

  /// <summary>General purpose bit flag: UTF-8 encoding for filename and comment.</summary>
  public const ushort FlagUtf8 = 0x0800;

  /// <summary>ZIP64 extra field tag.</summary>
  public const ushort Zip64ExtraFieldTag = 0x0001;

  /// <summary>Sentinel value indicating the field requires ZIP64.</summary>
  public const uint Zip64Sentinel32 = 0xFFFFFFFF;

  /// <summary>Sentinel value indicating the field requires ZIP64.</summary>
  public const ushort Zip64Sentinel16 = 0xFFFF;

  /// <summary>General purpose bit flag: entry is encrypted.</summary>
  public const ushort FlagEncrypted = 0x0001;
}
