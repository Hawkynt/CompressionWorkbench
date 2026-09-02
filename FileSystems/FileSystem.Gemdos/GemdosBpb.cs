#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Gemdos;

/// <summary>
/// Parsed Atari ST GEMDOS BPB. Layout matches MS-DOS FAT12 BPB but the jump
/// byte is <c>0x60</c> (m68k <c>BRA.S</c>) instead of x86 <c>0xEB</c>/<c>0xE9</c>.
///
/// All multi-byte fields are little-endian. Selected fields:
///   0x00  jmp       u8   — 0x60 (BRA.S)
///   0x01  branch    u16  — branch displacement
///   0x03  OEM       8 B  — vendor ID
///   0x0B  bps       u16  — bytes per sector (typically 512)
///   0x0D  spc       u8   — sectors per cluster
///   0x0E  resv      u16  — reserved sectors
///   0x10  nfats     u8   — number of FATs (typically 2)
///   0x11  nroot     u16  — root directory entries
///   0x13  totsec    u16  — total sectors
///   0x15  media     u8   — media descriptor
///   0x16  spf       u16  — sectors per FAT
///   0x18  spt       u16  — sectors per track
///   0x1A  sides     u16  — heads
/// </summary>
public sealed class GemdosBpb {
  /// <summary>
  /// Defines the gemdos jump constant value.
  /// </summary>
public const byte GemdosJump = 0x60;

  /// <summary>
  /// Gets a value indicating whether valid.
  /// </summary>
public bool Valid { get; init; }
  /// <summary>
  /// Gets or sets the jump byte.
  /// </summary>
public byte JumpByte { get; init; }
  /// <summary>
  /// Gets or sets the bytes per sector.
  /// </summary>
public ushort BytesPerSector { get; init; }
  /// <summary>
  /// Gets or sets the sectors per cluster.
  /// </summary>
public byte SectorsPerCluster { get; init; }
  /// <summary>
  /// Gets or sets the reserved sectors.
  /// </summary>
public ushort ReservedSectors { get; init; }
  /// <summary>
  /// Gets or sets the num fats.
  /// </summary>
public byte NumFats { get; init; }
  /// <summary>
  /// Gets or sets the root entries.
  /// </summary>
public ushort RootEntries { get; init; }
  /// <summary>
  /// Gets or sets the total sectors.
  /// </summary>
public ushort TotalSectors { get; init; }
  /// <summary>
  /// Gets or sets the media descriptor.
  /// </summary>
public byte MediaDescriptor { get; init; }
  /// <summary>
  /// Gets or sets the sectors per fat.
  /// </summary>
public ushort SectorsPerFat { get; init; }
  /// <summary>
  /// Gets or sets the sectors per track.
  /// </summary>
public ushort SectorsPerTrack { get; init; }
  /// <summary>
  /// Gets or sets the sides.
  /// </summary>
public ushort Sides { get; init; }
  /// <summary>
  /// Gets or sets the raw bytes.
  /// </summary>
public byte[] RawBytes { get; init; } = [];

  /// <summary>
  /// Attempts to parse the value from the supplied data.
  /// </summary>
public static GemdosBpb TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 0x20) return new GemdosBpb();
    if (image[0] != GemdosJump) return new GemdosBpb();

    var bps = ReadU16Le(image, 0x0B);
    // Sanity gate — GEMDOS sectors are always 512 (occasionally 256/1024).
    if (bps is not (256 or 512 or 1024)) return new GemdosBpb();

    var raw = image.Slice(0, Math.Min(512, image.Length)).ToArray();
    if (raw.Length < 32) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new GemdosBpb {
      Valid = true,
      JumpByte = image[0],
      BytesPerSector = bps,
      SectorsPerCluster = image[0x0D],
      ReservedSectors = ReadU16Le(image, 0x0E),
      NumFats = image[0x10],
      RootEntries = ReadU16Le(image, 0x11),
      TotalSectors = ReadU16Le(image, 0x13),
      MediaDescriptor = image[0x15],
      SectorsPerFat = ReadU16Le(image, 0x16),
      SectorsPerTrack = ReadU16Le(image, 0x18),
      Sides = ReadU16Le(image, 0x1A),
      RawBytes = raw,
    };
  }

  private static ushort ReadU16Le(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(off, 2)) : (ushort)0;
}
