#pragma warning disable CS1591
namespace FileFormat.Psf;

/// <summary>
/// Represents a psf constants.
/// </summary>
public static class PsfConstants {
  // 3-byte ASCII magic at file offset 0; followed by a single platform/version byte.
    /// <summary>
  /// Provides the magic value.
  /// </summary>
public static readonly byte[] Magic = "PSF"u8.ToArray();

  // Header layout: magic(3) + versionByte(1) + reservedSize(4 LE) + programSize(4 LE) + programCrc32(4 LE).
    /// <summary>
  /// Defines the header size constant value.
  /// </summary>
public const int HeaderSize = 16;

  // Optional tag block sentinel at the start of the trailing area; ASCII "[TAG]".
    /// <summary>
  /// Defines the tag prefix constant value.
  /// </summary>
public const string TagPrefix = "[TAG]";

  // Default platform byte for PS1, the original PSF format.
    /// <summary>
  /// Defines the version ps 1 constant value.
  /// </summary>
public const byte VersionPs1 = 0x01;

  // IEEE 802.3 reflected polynomial used by zlib/zip/gzip CRC-32. Required: the PSF spec
  // computes the CRC over the COMPRESSED program bytes, not the uncompressed payload.
    /// <summary>
  /// Defines the crc 32 polynomial constant value.
  /// </summary>
public const uint Crc32Polynomial = 0xEDB88320u;

  // Synthetic entry names exposed by the reader as a flat archive view of the container.
    /// <summary>
  /// Defines the entry header constant value.
  /// </summary>
public const string EntryHeader = "header.bin";
    /// <summary>
  /// Defines the entry reserved constant value.
  /// </summary>
public const string EntryReserved = "reserved.bin";
    /// <summary>
  /// Defines the entry program constant value.
  /// </summary>
public const string EntryProgram = "program.bin";
    /// <summary>
  /// Defines the entry tags constant value.
  /// </summary>
public const string EntryTags = "tags.txt";
}
