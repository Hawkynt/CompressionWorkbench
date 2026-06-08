#pragma warning disable CS1591
namespace FileFormat.Aomei;

/// <summary>
/// Wire-format constants for the AOMEI Backupper image format, recovered from
/// the binary reverse engineering of <c>ambakdrv.sys</c>, <c>ammntdrv.sys</c>,
/// <c>ImgFile.dll</c>, <c>Compress.dll</c> and <c>Encrypt.dll</c> (see
/// <c>docs/AOMEI_FORMAT_SPEC.md</c>).
/// </summary>
public static class AomeiConstants {

  /// <summary>5-byte ASCII signature <c>BIFH\</c> ("Backup Image File Header").
  /// Bytes <c>0x42 0x49 0x46 0x48 0x5C</c>. Doubles as the family-detection
  /// magic for both <c>.adi</c> and <c>.afi</c>.</summary>
  public static readonly byte[] BifhMagicAscii = [0x42, 0x49, 0x46, 0x48, 0x5C];

  /// <summary>Four-byte little-endian <c>'BIFH'</c> = 0x48464942 — the
  /// <see cref="BrFileHead.Flag"/> field at offset 0 of the head struct.</summary>
  public const uint BifhFlag = 0x48464942u;

  /// <summary>Four-byte little-endian <c>'BIFT'</c> = 0x54464942 — the
  /// <see cref="BrFileTail.Flag"/> field at offset 0 of the tail struct.</summary>
  public const uint BiftFlag = 0x54464942u;

  /// <summary><c>BR_IMAGE_FILE_HEAD</c> size: 0x65C (1628) bytes. Verified at
  /// the <c>ASSERT(Head.Size == sizeof(BR_IMAGE_FILE_HEAD))</c> check in
  /// <c>ammntdrv.sys!FUN_00015e90</c> and <c>ImgFile.dll!ReadHead</c>.</summary>
  public const int BifhSize = 0x65C;

  /// <summary><c>BR_IMAGE_FILE_TAIL</c> size: 0x674 (1652) bytes. Verified at
  /// <c>ammntdrv.sys!FUN_0001601c</c> tail read.</summary>
  public const int BiftSize = 0x674;

  /// <summary>Size of the <c>BR_STANDARD_HEADER</c> {Size, Type, Crc32} prefix
  /// shared by the file head, file tail and every INFO/INDEX record.</summary>
  public const int StandardHeaderSize = 12;

  /// <summary>Offset within <see cref="StandardHeaderSize"/> of the
  /// <c>Crc32</c> field. Used by the verifier to zero it before
  /// re-computing.</summary>
  public const int Crc32FieldOffset = 8;

  // INFO_TYPE_* values — confirmed via function-offset arguments seen in
  // ImgFile.dll!FUN_180014820 / 1800148d0 / 180014a30 / 180003490.
  /// <summary><c>INFO_TYPE_IMAGE_COMPRESS</c> — 0x18-byte record
  /// <c>{Size, Type=0x105, Crc32, method:u32, level:u32, pad:u32}</c>.</summary>
  public const ushort InfoTypeImageCompress = 0x0105;

  /// <summary><c>INFO_TYPE_IMAGE_ENCRYPT</c> — 0x18-byte record
  /// <c>{Size, Type=0x106, Crc32, method:u32, key_len:u32, pad:u32}</c>.</summary>
  public const ushort InfoTypeImageEncrypt = 0x0106;

  /// <summary><c>INFO_TYPE_IMAGE_PASSWORD</c> — 0x20-byte record
  /// <c>{Size, Type=0x107, Crc32, md5:byte[0x10], pad:u32}</c>. The 16-byte
  /// payload is MD5(password) — interactively typed passwords are MD5'd
  /// directly; the literal UTF-16 string <c>"AomeiTech.SchduleTask"</c>
  /// triggers an MD5 substitution from a runtime context struct.</summary>
  public const ushort InfoTypeImagePassword = 0x0107;

  /// <summary><c>INFO_TYPE_BACKUP_TYPE</c> — 0x14-byte record
  /// <c>{Size, Type=0x10C, Crc32, kind:u32}</c>.</summary>
  public const ushort InfoTypeBackupType = 0x010C;

  /// <summary>Compress method codes recovered from the <c>BRCompress</c>
  /// dispatch in <c>Compress.dll!FUN_180001040</c>. The numeric mapping for
  /// LZ4 vs zlib is only proven by the threshold check
  /// <c>method &gt;= 0x1000B</c> selecting the zlib path; treat unknown
  /// values as opaque.</summary>
  public const uint CompressMethodNone = 0;
  /// <summary>LZ4 raw-block compressor — the small-buffer path.</summary>
  public const uint CompressMethodLz4 = 1;
  /// <summary>Threshold above which the zlib inflate path is selected.</summary>
  public const uint CompressMethodZlibThreshold = 0x1000B;

  /// <summary>UTF-16 magic string that, when MD5-substituted via the
  /// scheduled-task context, lets the AOMEI service decrypt unattended
  /// backups. The literal misspelling ("Schdule") is preserved from the
  /// binary at <c>ImgFile.dll!18006baa0</c>.</summary>
  public const string SchedulerMagicPassword = "AomeiTech.SchduleTask";
}
