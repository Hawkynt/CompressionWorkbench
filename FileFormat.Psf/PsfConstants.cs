#pragma warning disable CS1591
namespace FileFormat.Psf;

public static class PsfConstants {
  // 3-byte ASCII magic at file offset 0; followed by a single platform/version byte.
  public static readonly byte[] Magic = "PSF"u8.ToArray();

  // Header layout: magic(3) + versionByte(1) + reservedSize(4 LE) + programSize(4 LE) + programCrc32(4 LE).
  public const int HeaderSize = 16;

  // Optional tag block sentinel at the start of the trailing area; ASCII "[TAG]".
  public const string TagPrefix = "[TAG]";

  // Default platform byte for PS1, the original PSF format.
  public const byte VersionPs1 = 0x01;

  // IEEE 802.3 reflected polynomial used by zlib/zip/gzip CRC-32. Required: the PSF spec
  // computes the CRC over the COMPRESSED program bytes, not the uncompressed payload.
  public const uint Crc32Polynomial = 0xEDB88320u;

  // Synthetic entry names exposed by the reader as a flat archive view of the container.
  public const string EntryHeader = "header.bin";
  public const string EntryReserved = "reserved.bin";
  public const string EntryProgram = "program.bin";
  public const string EntryTags = "tags.txt";
}
