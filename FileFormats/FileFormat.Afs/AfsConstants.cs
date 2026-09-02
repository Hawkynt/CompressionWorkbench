#pragma warning disable CS1591
namespace FileFormat.Afs;

/// <summary>
/// Represents an afs constants.
/// </summary>
public static class AfsConstants {
  // "AFS\0" — Sega Athena File System magic
  /// <summary>
  /// Provides the magic value.
  /// </summary>
  public static readonly byte[] Magic = [0x41, 0x46, 0x53, 0x00];

  // Header: magic(4) + fileCount(4)
  /// <summary>
  /// Defines the header size constant value.
  /// </summary>
  public const int HeaderSize = 8;

  // File index entry: offset(4) + size(4)
  /// <summary>
  /// Defines the index entry size constant value.
  /// </summary>
  public const int IndexEntrySize = 8;

  // Metadata pointer: offset(4) + size(4); sits directly after the file index
  /// <summary>
  /// Defines the metadata pointer size constant value.
  /// </summary>
  public const int MetadataPointerSize = 8;

  // Per-file metadata record: name(32) + 6×UInt16 timestamp + size(4)
  /// <summary>
  /// Defines the metadata record size constant value.
  /// </summary>
  public const int MetadataRecordSize = 48;

  // Filename byte length inside metadata record (32 bytes including null terminator)
  /// <summary>
  /// Defines the metadata name size constant value.
  /// </summary>
  public const int MetadataNameSize = 32;

  // 31 ASCII bytes max so a null terminator fits in the 32-byte field
  /// <summary>
  /// Defines the max name length constant value.
  /// </summary>
  public const int MaxNameLength = 31;

  // Real Sega AFS files conventionally align each file's payload to 0x800 (2048 bytes).
  // The format does not require it, but writers should match the convention so the
  // resulting archive is byte-identical to common dump tools.
  /// <summary>
  /// Defines the alignment constant value.
  /// </summary>
  public const int Alignment = 0x800;
}
