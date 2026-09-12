namespace FileFormat.Mix;

internal static class MixConstants {
  /// <summary>6-byte file header: fileCount (UInt16 LE) + bodySize (UInt32 LE).</summary>
  internal const int HeaderSize = 6;

  /// <summary>12 bytes per directory entry: id (UInt32 LE) + offset (UInt32 LE) + size (UInt32 LE).</summary>
  internal const int DirectoryEntrySize = 12;

  /// <summary>Maximum file count (UInt16 max). Used as a sanity bound during detection.</summary>
  internal const int MaxFileCount = 65535;
}
