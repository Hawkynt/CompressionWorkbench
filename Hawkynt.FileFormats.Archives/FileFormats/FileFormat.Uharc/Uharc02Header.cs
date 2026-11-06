using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// The 48-byte UHARC 0.2 archive header after descrambling (docs/UHARC-ON-DISK.md, "UHARC 0.2").
/// </summary>
internal sealed record Uharc02Header(
  ushort DosTime,
  ushort DosDate,
  int Method,
  int DirectoryCount,
  int FileCount,
  long UnpackedSize,
  long PackedSize,
  int NamesSize,
  int NamesPackedSize,
  int TimesPackedSize,
  int SizesPackedSize,
  uint TablesCrc) {

  public const int Size = 48;
  public const byte Version = 2;
  private const int ScrambledOffset = 4;
  private const int ScrambledLength = 40;

  /// <summary>Descrambles and validates a header.</summary>
  /// <exception cref="InvalidDataException">The magic or the header check does not match.</exception>
  public static Uharc02Header Parse(ReadOnlySpan<byte> raw) {
    if (raw.Length < Size)
      throw new InvalidDataException("UHARC: file is shorter than an archive header.");
    if (raw[0] != 'U' || raw[1] != 'H' || raw[2] != 'A' || raw[3] != Version)
      throw new InvalidDataException("UHARC: not a UHARC 0.2 archive.");

    Span<byte> h = stackalloc byte[Size];
    raw[..Size].CopyTo(h);
    var key = h[44];
    for (var i = 0; i < ScrambledLength; ++i)
      h[ScrambledOffset + i] ^= (byte)(key + i * i);

    if (Crc32.Compute(h.Slice(ScrambledOffset, ScrambledLength)) != BinaryPrimitives.ReadUInt32LittleEndian(h[44..]))
      throw new InvalidDataException("UHARC: archive header check failed.");

    return new(
      DosTime: BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
      DosDate: BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
      Method: h[9],
      DirectoryCount: BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),
      FileCount: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..])),
      UnpackedSize: BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),
      PackedSize: BinaryPrimitives.ReadUInt32LittleEndian(h[20..]),
      NamesSize: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[24..])),
      NamesPackedSize: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[28..])),
      TimesPackedSize: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[32..])),
      SizesPackedSize: checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h[36..])),
      TablesCrc: BinaryPrimitives.ReadUInt32LittleEndian(h[40..]));
  }

  /// <summary>Name of a 0.2 stream method.</summary>
  public static string MethodName(int method) => method switch {
    0 => "Store",
    1 => "RLE",
    >= 2 and <= 7 => "LZW",
    >= 8 and <= 19 => "m1",
    >= 20 and <= 31 => "m2",
    >= 32 and <= 43 => "m3",
    _ => $"method {method}",
  };

  /// <summary>Dictionary size of a 0.2 stream method, 0 where none applies.</summary>
  public static int DictionarySize(int method) => method switch {
    >= 2 and <= 7 => 1 << (method + 8),
    >= 8 and <= 43 => 1 << ((method - 8) % 12 + 10),
    _ => 0,
  };
}
