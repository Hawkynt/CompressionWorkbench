namespace FileFormat.Wad;

/// <summary>
/// Constants for the id Software WAD archive format (Doom/Heretic/Hexen).
/// </summary>
internal static class WadConstants {
  /// <summary>Magic bytes for an Internal WAD file: "IWAD" (0x49574144).</summary>
  public const uint MagicIwad = 0x44415749; // "IWAD" as little-endian uint32

  /// <summary>Magic bytes for a Patch WAD file: "PWAD" (0x50574144).</summary>
  public const uint MagicPwad = 0x44415750; // "PWAD" as little-endian uint32

  /// <summary>Magic string for an Internal WAD file.</summary>
  public const string MagicIwadString = "IWAD";

  /// <summary>Magic string for a Patch WAD file.</summary>
  public const string MagicPwadString = "PWAD";

  /// <summary>Size of the WAD file header in bytes (magic 4 + lump count 4 + directory offset 4).</summary>
  public const int HeaderSize = 12;

  /// <summary>Size of a single directory entry in bytes (offset 4 + size 4 + name 8).</summary>
  public const int DirectoryEntrySize = 16;

  /// <summary>Maximum length of a lump name in characters.</summary>
  public const int MaxLumpNameLength = 8;
}
