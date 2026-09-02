#pragma warning disable CS1591
namespace FileFormat.U8;

/// <summary>
/// Represents an u 8 constants.
/// </summary>
public static class U8Constants {

  // Magic 0x55 0xAA 0x38 0x2D ("U·8-") — Nintendo U8 archive identifier.
    /// <summary>
  /// Gets the magic.
  /// </summary>
public static ReadOnlySpan<byte> Magic => [0x55, 0xAA, 0x38, 0x2D];

  // Header layout: magic(4) firstNodeOffset(4) nodeTableSize(4) dataOffset(4) reserved(16).
    /// <summary>
  /// Defines the header size constant value.
  /// </summary>
public const int HeaderSize = 32;

  // Each node is type(1) + nameOffset(3) + dataOffset(4) + size(4).
    /// <summary>
  /// Defines the node size constant value.
  /// </summary>
public const int NodeSize = 12;

    /// <summary>
  /// Defines the type file constant value.
  /// </summary>
public const byte TypeFile = 0x00;
    /// <summary>
  /// Defines the type directory constant value.
  /// </summary>
public const byte TypeDirectory = 0x01;

  // Default first-node offset matches the canonical layout (nodes start immediately after header).
    /// <summary>
  /// Defines the default first node offset constant value.
  /// </summary>
public const uint DefaultFirstNodeOffset = HeaderSize;

  // File data is conventionally aligned to 0x20 bytes after the string table.
    /// <summary>
  /// Defines the data alignment constant value.
  /// </summary>
public const int DataAlignment = 0x20;

  // Sanity cap on per-component name length when writing — guards against malformed callers
  // building absurd entries that would explode the string table.
    /// <summary>
  /// Defines the max name length constant value.
  /// </summary>
public const int MaxNameLength = 4096;
}
