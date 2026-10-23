using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.HighSierra;

/// <summary>
/// Constants and field codecs of the High Sierra Group format — the 1986 CD-ROM
/// "Working Paper for Information Processing: Volume and File Structure of CD-ROM"
/// that preceded ISO 9660. Byte positions (BP) below are the working paper's,
/// 1-based; the code uses 0-based offsets. See <c>docs/HIGH-SIERRA-ON-DISK.md</c>.
/// </summary>
public static class HighSierraLayout {
  /// <summary>Bytes per logical sector (6.1.2).</summary>
  public const int SectorSize = 2048;

  /// <summary>Logical sector of the first volume descriptor (6.3.1).</summary>
  public const int FirstDescriptorSector = 16;

  /// <summary>Volume Structure Standard Identifier, BP 10-14 (11.1.3).</summary>
  public static ReadOnlySpan<byte> StandardId => "CDROM"u8;

  /// <summary>Volume Descriptor Type of the Standard File Structure Volume Descriptor (11.1.2).</summary>
  public const byte TypeStandard = 1;

  /// <summary>Volume Descriptor Type of the Volume Descriptor Sequence Terminator (11.1.2).</summary>
  public const byte TypeTerminator = 255;

  // Standard File Structure Volume Descriptor offsets (11.4, BP − 1).
  public const int VdLbn = 0, VdType = 8, VdId = 9, VdVersion = 14;
  public const int PvdSystemId = 16, PvdVolumeId = 48, PvdVolumeSpaceSize = 88;
  public const int PvdVolumeSetSize = 128, PvdVolumeSetSequence = 132, PvdLogicalBlockSize = 136;
  public const int PvdPathTableSize = 140, PvdPathTableL = 148, PvdPathTableM = 164;
  public const int PvdRootRecord = 180, PvdVolumeSetId = 214, PvdPublisherId = 342;
  public const int PvdPreparerId = 470, PvdApplicationId = 598, PvdCopyrightFile = 726, PvdAbstractFile = 758;
  public const int PvdCreation = 790, PvdModification = 806, PvdExpiration = 822, PvdEffective = 838;
  public const int PvdFileStructureVersion = 854;

  // Directory Record offsets (13.1, BP − 1).
  public const int DrLength = 0, DrXarLength = 1, DrExtent = 2, DrDataLength = 10, DrDate = 18;
  public const int DrFlags = 24, DrInterleaveSize = 26, DrInterleaveSkip = 27, DrVolumeSequence = 28;
  public const int DrNameLength = 32, DrName = 33;

  /// <summary>Length of a Directory Record with no system-use field: 33 bytes, the identifier, and a pad byte after an even-length identifier (13.1.13).</summary>
  public static int DirectoryRecordLength(int identifierLength) => 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);

  /// <summary>Length of a Path Table Record: 8 bytes, the identifier, and a pad byte after an odd-length identifier (12.1.1.6).</summary>
  public static int PathTableRecordLength(int identifierLength) => 8 + identifierLength + (identifierLength & 1);

  // File Flags (13.1.6).
  public const byte FlagHidden = 0x01, FlagDirectory = 0x02, FlagAssociated = 0x04, FlagRecord = 0x08, FlagProtection = 0x10, FlagMultiExtent = 0x80;

  /// <summary>Writes a 32-bit value in both byte orders (10.3.3).</summary>
  public static void WriteBoth32(Span<byte> dest, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(dest, value);
    BinaryPrimitives.WriteUInt32BigEndian(dest[4..], value);
  }

  /// <summary>Writes a 16-bit value in both byte orders (10.2.3).</summary>
  public static void WriteBoth16(Span<byte> dest, ushort value) {
    BinaryPrimitives.WriteUInt16LittleEndian(dest, value);
    BinaryPrimitives.WriteUInt16BigEndian(dest[2..], value);
  }

  /// <summary>Reads the little-endian half of a both-byte-order 32-bit field.</summary>
  public static uint ReadBoth32(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt32LittleEndian(src);

  /// <summary>Reads the little-endian half of a both-byte-order 16-bit field.</summary>
  public static ushort ReadBoth16(ReadOnlySpan<byte> src) => BinaryPrimitives.ReadUInt16LittleEndian(src);

  /// <summary>Decodes the 6-byte Directory Record date (13.1.5): years since 1900, month, day, hour, minute, second; no time zone.</summary>
  public static DateTime? DecodeRecordDate(ReadOnlySpan<byte> d) {
    if (d[..6].IndexOfAnyExcept((byte)0) < 0) return null;
    try {
      return new DateTime(1900 + d[0], d[1], d[2], d[3], d[4], d[5], DateTimeKind.Utc);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  /// <summary>Encodes a 6-byte Directory Record date.</summary>
  public static void EncodeRecordDate(Span<byte> d, DateTime t) {
    t = t.ToUniversalTime();
    var year = Math.Clamp(t.Year - 1900, 0, 255);
    d[0] = (byte)year; d[1] = (byte)t.Month; d[2] = (byte)t.Day;
    d[3] = (byte)t.Hour; d[4] = (byte)t.Minute; d[5] = (byte)t.Second;
  }

  /// <summary>Encodes the 16-digit volume date (11.4.30.1): YYYYMMDDHHMMSS and hundredths; all zero digits = not specified.</summary>
  public static void EncodeVolumeDate(Span<byte> d, DateTime? t) {
    var text = t is { } v
      ? v.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + (v.Millisecond / 10).ToString("00", CultureInfo.InvariantCulture)
      : "0000000000000000";
    Encoding.ASCII.GetBytes(text, d);
  }

  /// <summary>Space-pads <paramref name="value"/> into a fixed a/d-character field (10.4.3).</summary>
  public static void WritePadded(Span<byte> field, string value) {
    field.Fill((byte)' ');
    var bytes = Encoding.ASCII.GetBytes(value);
    bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
  }

  /// <summary>Reads a space-padded identifier field.</summary>
  public static string ReadPadded(ReadOnlySpan<byte> field) => Encoding.ASCII.GetString(field).TrimEnd(' ', '\0');

  /// <summary>True for a d-character (10.4.1): A-Z, 0-9 and LOW LINE.</summary>
  public static bool IsDCharacter(char c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}
