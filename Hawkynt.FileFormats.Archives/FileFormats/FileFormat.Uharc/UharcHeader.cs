using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// The 39-byte UHARC 0.6 archive header after descrambling (docs/UHARC-ON-DISK.md, "Header").
/// </summary>
internal sealed record UharcHeader(
  ushort DosTime,
  ushort DosDate,
  byte Flags,
  byte Filters,
  int Method,
  int DirectoryCount,
  long UnpackedSize,
  long PackedSize,
  int FileCount,
  int NamesSize,
  int NamesPackedSize,
  int MetadataPackedSize,
  ushort CrcTable,
  ushort MetadataCrc) {

  public const int Size = 39;
  public const int ScrambledOffset = 4;
  public const int ScrambledLength = 31;
  public const byte Version = 6;

  /// <summary>Flags bit: the directory tables are enciphered with the user password.</summary>
  public const byte TablesUsePassword = 0x04;

  /// <summary>Flags bit: the packed stream is enciphered with the user password.</summary>
  public const byte StreamUsesPassword = 0x02;

  /// <summary>Filters bit: executables were run through the E8/E9 transform before packing.</summary>
  public const byte ExecutableFilter = 0x01;

  /// <summary>Filters bit: BMP/WAV/VOC files were delta-coded before packing (LZP mode only).</summary>
  public const byte MultimediaFilter = 0x02;

  /// <summary>Recognises the four magic bytes <c>UHA\x06</c>.</summary>
  public static bool HasMagic(ReadOnlySpan<byte> data)
    => data.Length >= 4 && data[0] == 'U' && data[1] == 'H' && data[2] == 'A' && data[3] == Version;

  /// <summary>Descrambles and validates a header.</summary>
  /// <exception cref="InvalidDataException">The magic, version or header check does not match.</exception>
  public static UharcHeader Parse(ReadOnlySpan<byte> raw) {
    if (raw.Length < Size)
      throw new InvalidDataException("UHARC: file is shorter than an archive header.");
    if (raw[0] != 'U' || raw[1] != 'H' || raw[2] != 'A')
      throw new InvalidDataException("UHARC: missing UHA signature.");
    if (raw[3] != Version)
      throw new InvalidDataException($"UHARC: archive version {raw[3]} is not supported (only 0.6 archives, version {Version}).");

    Span<byte> h = stackalloc byte[Size];
    raw[..Size].CopyTo(h);
    UharcCipher.DescrambleHeader(h, h[37]);
    var stored = BinaryPrimitives.ReadUInt16LittleEndian(h[37..]);
    var computed = (ushort)Crc32.Compute(h.Slice(ScrambledOffset, ScrambledLength));
    if (stored != computed)
      throw new InvalidDataException("UHARC: archive header check failed.");

    return new(
      DosTime: BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
      DosDate: BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
      Flags: h[8],
      Filters: h[9],
      Method: h[10],
      DirectoryCount: BinaryPrimitives.ReadUInt16LittleEndian(h[11..]),
      UnpackedSize: BinaryPrimitives.ReadUInt32LittleEndian(h[13..]),
      PackedSize: BinaryPrimitives.ReadUInt32LittleEndian(h[17..]),
      FileCount: ReadUInt24(h[21..]),
      NamesSize: ReadUInt24(h[24..]),
      NamesPackedSize: ReadUInt24(h[27..]),
      MetadataPackedSize: ReadUInt24(h[30..]),
      CrcTable: BinaryPrimitives.ReadUInt16LittleEndian(h[33..]),
      MetadataCrc: BinaryPrimitives.ReadUInt16LittleEndian(h[35..]));
  }

  private static int ReadUInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
}
