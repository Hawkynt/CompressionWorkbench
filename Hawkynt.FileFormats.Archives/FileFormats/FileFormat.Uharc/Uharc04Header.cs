using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// The 44-byte header shared by UHARC 0.3 (<c>UHA\x03</c>) and UHARC 0.4 (<c>UHA\x04</c>) archives,
/// after descrambling (docs/UHARC-ON-DISK.md, "UHARC 0.4").
/// </summary>
internal sealed record Uharc04Header(
  int Version,
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
  int DatesPackedSize,
  int SizesPackedSize,
  ushort NamesCrc,
  ushort DatesCrc,
  ushort SizesCrc) {

  public const int Size = 44;
  private const int ScrambledOffset = 4;
  private const int ScrambledLength = 36;

  /// <summary>Flags bit: the directory tables and the packed stream are enciphered with the user password.</summary>
  public const byte UsesPassword = 0x02;

  /// <summary>Recognises the magic of a 0.3 or 0.4 archive.</summary>
  public static bool HasMagic(ReadOnlySpan<byte> data)
    => data.Length >= 4 && data[0] == 'U' && data[1] == 'H' && data[2] == 'A' && data[3] is 3 or 4;

  /// <summary>Descrambles and validates a header.</summary>
  /// <exception cref="InvalidDataException">The magic or the header check does not match.</exception>
  public static Uharc04Header Parse(ReadOnlySpan<byte> raw) {
    if (raw.Length < Size)
      throw new InvalidDataException("UHARC: file is shorter than an archive header.");
    if (!HasMagic(raw))
      throw new InvalidDataException("UHARC: not a UHARC 0.3 or 0.4 archive.");

    Span<byte> h = stackalloc byte[Size];
    raw[..Size].CopyTo(h);
    var key = h[42];
    for (var i = 0; i < ScrambledLength; ++i)
      h[ScrambledOffset + i] ^= (byte)(key + i * i);

    if ((ushort)Crc32.Compute(h.Slice(ScrambledOffset, ScrambledLength)) != BinaryPrimitives.ReadUInt16LittleEndian(h[42..]))
      throw new InvalidDataException("UHARC: archive header check failed.");

    return new(
      Version: h[3],
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
      DatesPackedSize: ReadUInt24(h[30..]),
      SizesPackedSize: ReadUInt24(h[33..]),
      NamesCrc: BinaryPrimitives.ReadUInt16LittleEndian(h[36..]),
      DatesCrc: BinaryPrimitives.ReadUInt16LittleEndian(h[38..]),
      SizesCrc: BinaryPrimitives.ReadUInt16LittleEndian(h[40..]));
  }

  private static int ReadUInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
}
