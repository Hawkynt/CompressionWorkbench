namespace FileFormat.Cab;

/// <summary>
/// Constants for the Microsoft Cabinet (CAB) file format (MS-CAB-COMPRESS).
/// </summary>
public static class CabConstants {
  /// <summary>CAB file signature: "MSCF" (0x4D, 0x53, 0x43, 0x46).</summary>
  public static ReadOnlySpan<byte> Signature => "MSCF"u8;

  /// <summary>Size of the CFHEADER structure in bytes (fixed fields only).</summary>
  public const int HeaderSize = 36;

  /// <summary>Size of the CFFOLDER structure in bytes (fixed fields only).</summary>
  public const int FolderSize = 8;

  /// <summary>Size of the fixed part of a CFFILE structure (before the name).</summary>
  public const int FileFixedSize = 16;

  /// <summary>Size of the fixed part of a CFDATA structure (before compressed data).</summary>
  public const int DataFixedSize = 8;

  /// <summary>Cabinet version minor number.</summary>
  public const byte VersionMinor = 3;

  /// <summary>Cabinet version major number.</summary>
  public const byte VersionMajor = 1;

  /// <summary>Cabinet flags: bit 0 — previous cabinet present.</summary>
  public const ushort FlagPrevCabinet = 0x0001;

  /// <summary>Cabinet flags: bit 1 — next cabinet present.</summary>
  public const ushort FlagNextCabinet = 0x0002;

  /// <summary>Cabinet flags: bit 2 — reserve fields present.</summary>
  public const ushort FlagReserveFields = 0x0004;

  /// <summary>File attribute: read-only.</summary>
  public const ushort AttribReadOnly = 0x0001;

  /// <summary>File attribute: hidden.</summary>
  public const ushort AttribHidden = 0x0002;

  /// <summary>File attribute: system file.</summary>
  public const ushort AttribSystem = 0x0004;

  /// <summary>File attribute: modified since last backup (archive bit).</summary>
  public const ushort AttribArchive = 0x0020;

  /// <summary>File attribute: UTF-8 name encoding.</summary>
  public const ushort AttribUtf8 = 0x0080;
}

/// <summary>
/// Compression types used in the CFFOLDER <c>typeCompress</c> field.
/// </summary>
public enum CabCompressionType : ushort {
  /// <summary>No compression — data is stored verbatim.</summary>
  None = 0,

  /// <summary>MSZIP compression (Deflate with 32 KB blocks).</summary>
  MsZip = 1,

  /// <summary>Quantum compression.</summary>
  Quantum = 2,

  /// <summary>LZX compression (not implemented).</summary>
  Lzx = 3,
}
