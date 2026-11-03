namespace FileFormat.Ace;

/// <summary>
/// Header types and flags of the ACE container, as acefile documents them from the ACE 1.2 technical
/// notes and the test-archive corpus.
/// </summary>
internal static class AceConstants {
  /// <summary>ACE magic signature at offset 7 in the main header.</summary>
  public const string Magic = "**ACE**";

  public const byte HeaderTypeMain = 0;
  public const byte HeaderTypeFile32 = 1;
  public const byte HeaderTypeRecovery32 = 2;
  public const byte HeaderTypeFile64 = 3;
  public const byte HeaderTypeRecovery64A = 4;
  public const byte HeaderTypeRecovery64B = 5;

  // flags shared by all headers
  public const ushort FlagAddSize = 1 << 0;
  public const ushort FlagComment = 1 << 1;
  public const ushort Flag64Bit = 1 << 2;

  // main header flags
  public const ushort FlagV20Format = 1 << 8;
  public const ushort FlagSfx = 1 << 9;
  public const ushort FlagMultiVolume = 1 << 11;
  public const ushort FlagAdvert = 1 << 12;
  public const ushort FlagRecovery = 1 << 13;
  public const ushort FlagLocked = 1 << 14;
  public const ushort FlagSolid = 1 << 15;

  // file header flags
  public const ushort FileFlagNtSecurity = 1 << 10;
  public const ushort FileFlagContinuedFromPrevious = 1 << 12;
  public const ushort FileFlagContinuedInNext = 1 << 13;
  public const ushort FileFlagPassword = 1 << 14;
  public const ushort FileFlagSolid = 1 << 15;

  public const uint AttributeDirectory = 0x10;
  public const uint AttributeArchive = 0x20;

  /// <summary>Host OS byte for Win32, as WinAce writes it.</summary>
  public const byte HostWin32 = 2;

  /// <summary>
  /// ACE's CRC: the IEEE CRC-32 register started at 0xFFFFFFFF and stored without the final
  /// inversion. File headers hold all 32 bits, block headers the low 16.
  /// </summary>
  public static uint AceCrc32(ReadOnlySpan<byte> data) => ~Compression.Core.Checksums.Crc32.Compute(data);

  /// <summary>WinAce stores names and comments in the OEM code page (CP437 on the systems that wrote them).</summary>
  public static System.Text.Encoding Oem {
    get {
      System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
      return System.Text.Encoding.GetEncoding(437);
    }
  }

  public static ushort AceCrc16(ReadOnlySpan<byte> data) => (ushort)AceCrc32(data);
}
