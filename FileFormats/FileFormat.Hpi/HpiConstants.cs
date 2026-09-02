#pragma warning disable CS1591
namespace FileFormat.Hpi;

/// <summary>
/// Represents a hpi constants.
/// </summary>
public static class HpiConstants {
  // "HAPI" little-endian — the canonical Total Annihilation HPI/UFO/CCX/GP3 magic.
  /// <summary>
  /// Defines the magic constant value.
  /// </summary>
public const uint Magic = 0x49504148u;

  // TA classic version. BANK header (0x4B494D42) is encrypted/compressed-with-key — out of scope here.
  /// <summary>
  /// Defines the version ta classic constant value.
  /// </summary>
public const uint VersionTaClassic = 0x00010000u;

  // 'SQSH' little-endian. Per-chunk sentinel inside a file's data block when the file is compressed.
  /// <summary>
  /// Defines the chunk magic constant value.
  /// </summary>
public const uint ChunkMagic = 0x48535153u;

  // Header layout: magic(4) + version(4) + dirSize(4) + headerKey(4) + dirStart(4).
  /// <summary>
  /// Defines the header size constant value.
  /// </summary>
public const int HeaderSize = 20;

  // Per-chunk header layout: magic(4) + marker(1) + compression(1) + encrypt(1) + cSize(4) + dSize(4) + checksum(4).
  /// <summary>
  /// Defines the chunk header size constant value.
  /// </summary>
public const int ChunkHeaderSize = 19;

  // 8-byte directory header: entryCount(4) + entryListOffset(4).
  /// <summary>
  /// Defines the directory header size constant value.
  /// </summary>
public const int DirectoryHeaderSize = 8;

  // 9-byte entry record: nameOffset(4) + dataOffset(4) + isDirectory(1).
  /// <summary>
  /// Defines the entry record size constant value.
  /// </summary>
public const int EntryRecordSize = 9;

  // Files larger than this are split into independently-compressed SQSH chunks.
  /// <summary>
  /// Defines the max chunk size constant value.
  /// </summary>
public const int MaxChunkSize = 65536;

  // Per-chunk compression flag values. We support stored and zlib only.
  /// <summary>
  /// Defines the compression stored constant value.
  /// </summary>
public const byte CompressionStored = 0;
  /// <summary>
  /// Defines the compression lz 77 constant value.
  /// </summary>
public const byte CompressionLz77 = 1;
  /// <summary>
  /// Defines the compression zlib constant value.
  /// </summary>
public const byte CompressionZlib = 2;

  // Encrypt flag inside SQSH chunk header. Non-zero rejected on read.
  /// <summary>
  /// Defines the encrypt plain constant value.
  /// </summary>
public const byte EncryptPlain = 0;

  // Default marker byte written into SQSH chunks.
  /// <summary>
  /// Defines the chunk marker default constant value.
  /// </summary>
public const byte ChunkMarkerDefault = 2;
}
