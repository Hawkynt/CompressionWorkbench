namespace FileFormat.Ace;

/// <summary>
/// Constants for the ACE archive format.
/// </summary>
internal static class AceConstants {
  /// <summary>ACE magic signature at offset 7 in the archive header.</summary>
  public const string Magic = "**ACE**";

  /// <summary>Archive header type.</summary>
  public const byte HeaderTypeArchive = 0;

  /// <summary>File header type.</summary>
  public const byte HeaderTypeFile = 1;

  /// <summary>Recovery record header type.</summary>
  public const byte HeaderTypeRecovery = 2;

  /// <summary>Archive header flag: solid archive.</summary>
  public const ushort FlagSolid = 0x0004;

  /// <summary>Archive header flag: locked archive.</summary>
  public const ushort FlagLocked = 0x0002;

  /// <summary>Archive header flag: multi-volume.</summary>
  public const ushort FlagMultiVolume = 0x0800;

  /// <summary>Archive header flag: AV string present.</summary>
  public const ushort FlagAvString = 0x0100;

  /// <summary>Archive header flag: recovery record present.</summary>
  public const ushort FlagRecovery = 0x0200;

  /// <summary>File header flag: continued from previous volume.</summary>
  public const ushort FileFlagFromPrev = 0x0001;

  /// <summary>File header flag: continued to next volume.</summary>
  public const ushort FileFlagToNext = 0x0002;

  /// <summary>File header flag: encrypted.</summary>
  public const ushort FileFlagEncrypted = 0x0004;

  /// <summary>File header flag: solid.</summary>
  public const ushort FileFlagSolid = 0x4000;
}
