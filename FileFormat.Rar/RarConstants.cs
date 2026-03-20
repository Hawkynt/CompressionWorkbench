namespace FileFormat.Rar;

/// <summary>
/// Constants for the RAR5 archive format.
/// </summary>
internal static class RarConstants {
  /// <summary>RAR5 signature (8 bytes).</summary>
  public static ReadOnlySpan<byte> Rar5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

  /// <summary>RAR4 signature (7 bytes) — for detection only.</summary>
  public static ReadOnlySpan<byte> Rar4Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

  /// <summary>Main archive header type.</summary>
  public const int HeaderTypeMain = 1;

  /// <summary>File header type.</summary>
  public const int HeaderTypeFile = 2;

  /// <summary>Service header type.</summary>
  public const int HeaderTypeService = 3;

  /// <summary>Encryption header type.</summary>
  public const int HeaderTypeEncryption = 4;

  /// <summary>End of archive header type.</summary>
  public const int HeaderTypeEndArchive = 5;

  /// <summary>Store (no compression) method.</summary>
  public const int MethodStore = 0;

  /// <summary>Fastest compression method.</summary>
  public const int MethodFastest = 1;

  /// <summary>Fast compression method.</summary>
  public const int MethodFast = 2;

  /// <summary>Normal compression method.</summary>
  public const int MethodNormal = 3;

  /// <summary>Good compression method.</summary>
  public const int MethodGood = 4;

  /// <summary>Best compression method.</summary>
  public const int MethodBest = 5;

  /// <summary>Windows host OS.</summary>
  public const int OsWindows = 0;

  /// <summary>Unix host OS.</summary>
  public const int OsUnix = 1;

  /// <summary>Entry is a directory.</summary>
  public const int FileFlagDirectory = 0x0001;

  /// <summary>Modification time is present.</summary>
  public const int FileFlagTimeMtime = 0x0002;

  /// <summary>CRC-32 of unpacked data is present.</summary>
  public const int FileFlagCrc32 = 0x0004;

  /// <summary>Unpacked size is unknown.</summary>
  public const int FileFlagUnpackedSizeUnknown = 0x0008;

  /// <summary>Header has an extra area.</summary>
  public const int HeaderFlagExtraArea = 0x0001;

  /// <summary>Header has a data area following it.</summary>
  public const int HeaderFlagDataArea = 0x0002;

  /// <summary>Skip this header if its type is unknown.</summary>
  public const int HeaderFlagSkipIfUnknown = 0x0004;

  /// <summary>Data continues from a previous volume.</summary>
  public const int HeaderFlagSplitBefore = 0x0008;

  /// <summary>Data continues in the next volume.</summary>
  public const int HeaderFlagSplitAfter = 0x0010;

  // RAR4 header types
  /// <summary>RAR4 marker header.</summary>
  public const byte Rar4TypeMarker = 0x72;

  /// <summary>RAR4 main archive header.</summary>
  public const byte Rar4TypeMain = 0x73;

  /// <summary>RAR4 file header.</summary>
  public const byte Rar4TypeFile = 0x74;

  /// <summary>RAR4 comment header.</summary>
  public const byte Rar4TypeComment = 0x75;

  /// <summary>RAR4 sub-block header.</summary>
  public const byte Rar4TypeSubBlock = 0x77;

  /// <summary>RAR4 end of archive header.</summary>
  public const byte Rar4TypeEnd = 0x7B;

  // RAR4 flags
  /// <summary>RAR4 header flag: additional data size present (ADD_SIZE).</summary>
  public const ushort Rar4FlagAddSize = 0x8000;

  /// <summary>RAR4 file header flag: file continued from previous volume.</summary>
  public const ushort Rar4FlagSplitBefore = 0x0001;

  /// <summary>RAR4 file header flag: file continued in next volume.</summary>
  public const ushort Rar4FlagSplitAfter = 0x0002;

  /// <summary>RAR4 file header flag: encrypted.</summary>
  public const ushort Rar4FlagEncrypted = 0x0004;

  /// <summary>RAR4 file header flag: solid compression.</summary>
  public const ushort Rar4FlagSolid = 0x0010;

  /// <summary>RAR4 file header flag: directory entry.</summary>
  public const ushort Rar4FlagDirectory = 0x00E0;

  /// <summary>RAR4 file header flag: large file (64-bit sizes).</summary>
  public const ushort Rar4FlagLargeFile = 0x0100;

  /// <summary>RAR4 file header flag: filename is Unicode.</summary>
  public const ushort Rar4FlagUnicode = 0x0200;

  /// <summary>RAR4 compression method: Store.</summary>
  public const byte Rar4MethodStore = 0x30;

  /// <summary>RAR4 compression method: Fastest.</summary>
  public const byte Rar4MethodFastest = 0x31;

  /// <summary>RAR4 compression method: Fast.</summary>
  public const byte Rar4MethodFast = 0x32;

  /// <summary>RAR4 compression method: Normal.</summary>
  public const byte Rar4MethodNormal = 0x33;

  /// <summary>RAR4 compression method: Good.</summary>
  public const byte Rar4MethodGood = 0x34;

  /// <summary>RAR4 compression method: Best.</summary>
  public const byte Rar4MethodBest = 0x35;

  // RAR5 encryption constants

  /// <summary>RAR5 encryption version (0 = AES-256).</summary>
  public const int EncryptionVersionAes256 = 0;

  /// <summary>RAR5 encryption flag: password check value is present.</summary>
  public const int EncryptFlagPasswordCheck = 0x0001;

  /// <summary>RAR5 encryption flag: headers are also encrypted.</summary>
  public const int EncryptFlagHeaderEncrypt = 0x0002;

  /// <summary>RAR5 file extra record type: encryption.</summary>
  public const int FileExtraEncryption = 1;

  /// <summary>RAR5 file extra record type: hash (BLAKE2sp).</summary>
  public const int FileExtraHash = 2;

  /// <summary>RAR5 file extra record type: time.</summary>
  public const int FileExtraTime = 3;

  /// <summary>RAR5 recovery record service name.</summary>
  public const string RecoveryRecordName = "RR";

  /// <summary>Default recovery record sector size (64 KB).</summary>
  public const int RecoverySectorSize = 65536;
}
