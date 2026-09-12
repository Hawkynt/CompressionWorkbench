namespace FileFormat.Lbr;

/// <summary>
/// Constants for the CP/M LBR (Library) archive format.
/// </summary>
internal static class LbrConstants {

  /// <summary>CP/M sector size in bytes.</summary>
  public const int SectorSize = 128;

  /// <summary>Size of a single directory entry in bytes.</summary>
  public const int DirectoryEntrySize = 32;

  /// <summary>Maximum length of the filename portion (CP/M 8.3).</summary>
  public const int MaxFileNameLength = 8;

  /// <summary>Maximum length of the extension portion (CP/M 8.3).</summary>
  public const int MaxExtensionLength = 3;

  /// <summary>Status byte indicating an active directory entry.</summary>
  public const byte StatusActive = 0x00;

  /// <summary>Status byte indicating a deleted or unused directory entry.</summary>
  public const byte StatusDeleted = 0xFE;

  /// <summary>Space character used to pad filenames and extensions.</summary>
  public const byte PadChar = 0x20;

  /// <summary>CP/M EOF marker used to pad file data to sector boundaries.</summary>
  public const byte FillByte = 0x1A;

  /// <summary>CP/M epoch: January 1, 1978.</summary>
  public static readonly DateTime CpmEpoch = new(1978, 1, 1);

}
