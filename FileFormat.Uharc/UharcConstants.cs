namespace FileFormat.Uharc;

/// <summary>
/// Constants for the UHARC archive format.
/// </summary>
internal static class UharcConstants {
  /// <summary>Magic signature: "UHA" (3 bytes).</summary>
  internal static readonly byte[] Magic = [0x55, 0x48, 0x41];

  /// <summary>Current format version.</summary>
  internal const byte Version = 3;

  /// <summary>Compression method: LZP.</summary>
  internal const byte MethodLzp = 0;

  /// <summary>Compression method: stored (no compression).</summary>
  internal const byte MethodStore = 255;

  /// <summary>Archive header size: 3 (magic) + 1 (version) + 3 (flags) = 7 bytes.</summary>
  internal const int HeaderSize = 7;
}
