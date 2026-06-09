#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Aomei;

/// <summary>
/// A single decoded INFO/INDEX record from the AOMEI payload. Carries the raw
/// 12-byte header, the verification result of the on-disk CRC and the raw
/// body bytes. Higher-level typed views are exposed via the
/// <c>TryGetXxx</c> helpers — they return <c>false</c> when the record's type
/// or size doesn't match the spec, so unknown/future records degrade
/// gracefully instead of throwing.
/// </summary>
public sealed class AomeiInfoRecord {

  /// <summary>Twelve-byte tagged header (Size / Type / Crc32).</summary>
  public BrStandardHeader Header { get; }

  /// <summary>True when the recomputed CRC matched the stored value.</summary>
  public bool CrcValid { get; }

  /// <summary>Body bytes excluding the 12-byte header. May be empty for
  /// header-only records (none observed, but the layout permits it).</summary>
  public byte[] Body { get; }

  /// <summary>Byte offset of the record relative to the start of the file
  /// (i.e. relative to the BIFH magic at file offset 0).</summary>
  public long FileOffset { get; }

  public AomeiInfoRecord(BrStandardHeader header, bool crcValid, byte[] body, long fileOffset) {
    this.Header = header;
    this.CrcValid = crcValid;
    this.Body = body ?? throw new ArgumentNullException(nameof(body));
    this.FileOffset = fileOffset;
  }

  /// <summary>Symbolic name for the record's <c>Type</c> tag, or
  /// <c>UNKNOWN_0xNNNN</c> for codes not in the recovered enumeration.
  /// Both INFO_TYPE_* (0x1xx) and INDEX_TYPE_* (0x2xx / 0x3xx) tags are
  /// surfaced — INDEX_TYPE_* indicates a sub-index record whose body holds
  /// a packed array of <c>BR_IMAGE_INDEX_ENTRY_*</c> entries rather than
  /// a single typed value.</summary>
  public string TypeName => this.Header.Type switch {
    AomeiConstants.InfoTypeImageCompress     => "INFO_TYPE_IMAGE_COMPRESS",
    AomeiConstants.InfoTypeImageEncrypt      => "INFO_TYPE_IMAGE_ENCRYPT",
    AomeiConstants.InfoTypeImagePassword     => "INFO_TYPE_IMAGE_PASSWORD",
    AomeiConstants.InfoTypeBackupType        => "INFO_TYPE_BACKUP_TYPE",
    AomeiConstants.InfoTypeImageSplitSize    => "INFO_TYPE_IMAGE_SPLIT_SIZE",
    AomeiConstants.InfoTypeImageComment      => "INFO_TYPE_IMAGE_COMMENT",
    AomeiConstants.InfoTypeBackupTime        => "INFO_TYPE_BACKUP_TIME",
    AomeiConstants.InfoTypeBackupOption      => "INFO_TYPE_BACKUP_OPTION",
    AomeiConstants.InfoTypeDiskInfo          => "INFO_TYPE_DISK_INFO",
    AomeiConstants.InfoTypeVolumeInfo        => "INFO_TYPE_VOLUME_INFO",
    AomeiConstants.InfoTypeFlbBackupOption   => "INFO_TYPE_FLB_BACKUP_OPTION",
    AomeiConstants.InfoTypeFlbBackupOptionEx => "INFO_TYPE_FLB_BACKUP_OPTION_EX",
    AomeiConstants.InfoTypeFlbPathList       => "INFO_TYPE_FLB_PATH_LIST",
    AomeiConstants.IndexTypeRoot             => "INDEX_TYPE_ROOT",
    AomeiConstants.IndexTypeVolume           => "INDEX_TYPE_VOLUME",
    AomeiConstants.IndexTypeDataBlock        => "INDEX_TYPE_DATABLOCK",
    AomeiConstants.IndexTypeDirTree          => "INDEX_TYPE_DIRTREE",
    AomeiConstants.IndexTypeDataArea         => "INDEX_TYPE_DATAAREA",
    _ => $"UNKNOWN_0x{this.Header.Type:X4}",
  };

  /// <summary>True when the record's type tag is one of the recovered
  /// <c>INDEX_TYPE_*</c> values (root / volume / datablock / dirtree /
  /// dataarea). The body of an index record holds a
  /// <c>BR_IMAGE_INDEX</c> header (EntryCount / EntrySize) followed by a
  /// packed entry array — see <see cref="BrImageIndex"/>.</summary>
  public bool IsIndex => this.Header.Type
    is AomeiConstants.IndexTypeRoot
    or AomeiConstants.IndexTypeVolume
    or AomeiConstants.IndexTypeDataBlock
    or AomeiConstants.IndexTypeDirTree
    or AomeiConstants.IndexTypeDataArea;

  /// <summary>Tries to decode this record as
  /// <see cref="AomeiConstants.InfoTypeImageCompress"/>. Returns
  /// <c>false</c> when the type tag or size doesn't match.</summary>
  public bool TryGetCompressInfo(out uint method, out uint level) {
    method = 0; level = 0;
    if (this.Header.Type != AomeiConstants.InfoTypeImageCompress) return false;
    if (this.Header.Size != 0x18 || this.Body.Length < 8) return false;
    method = BinaryPrimitives.ReadUInt32LittleEndian(this.Body.AsSpan(0, 4));
    level = BinaryPrimitives.ReadUInt32LittleEndian(this.Body.AsSpan(4, 4));
    return true;
  }

  /// <summary>Tries to decode this record as
  /// <see cref="AomeiConstants.InfoTypeImageEncrypt"/>.</summary>
  public bool TryGetEncryptInfo(out uint method, out uint keyLen) {
    method = 0; keyLen = 0;
    if (this.Header.Type != AomeiConstants.InfoTypeImageEncrypt) return false;
    if (this.Header.Size != 0x18 || this.Body.Length < 8) return false;
    method = BinaryPrimitives.ReadUInt32LittleEndian(this.Body.AsSpan(0, 4));
    keyLen = BinaryPrimitives.ReadUInt32LittleEndian(this.Body.AsSpan(4, 4));
    return true;
  }

  /// <summary>Tries to decode this record as
  /// <see cref="AomeiConstants.InfoTypeImagePassword"/>. Surfaces the
  /// 16-byte MD5 hash that the AOMEI reader compares against
  /// <c>IsPswEqual(sPassword, PswLen, ((BR_IMAGE_INFO_PASSWORD*)pInfo)-&gt;MD5, 16)</c>.</summary>
  public bool TryGetPasswordMd5(out byte[] md5) {
    md5 = [];
    if (this.Header.Type != AomeiConstants.InfoTypeImagePassword) return false;
    if (this.Header.Size != 0x20 || this.Body.Length < 0x10) return false;
    md5 = this.Body.AsSpan(0, 16).ToArray();
    return true;
  }

  /// <summary>Tries to decode this record as
  /// <see cref="AomeiConstants.InfoTypeBackupType"/>.</summary>
  public bool TryGetBackupType(out uint kind) {
    kind = 0;
    if (this.Header.Type != AomeiConstants.InfoTypeBackupType) return false;
    if (this.Header.Size != 0x14 || this.Body.Length < 4) return false;
    kind = BinaryPrimitives.ReadUInt32LittleEndian(this.Body.AsSpan(0, 4));
    return true;
  }

  /// <summary>
  /// Builds an <see cref="AomeiConstants.InfoTypeImageCompress"/> record
  /// (0x18 bytes total) with the supplied <paramref name="method"/> and
  /// <paramref name="level"/>. The CRC is sealed in place.
  /// </summary>
  public static byte[] BuildCompress(uint method, uint level) {
    var buf = new byte[0x18];
    new BrStandardHeader(0x18, AomeiConstants.InfoTypeImageCompress, 0).Write(buf);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), method);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), level);
    // bytes 20..23 = trailing pad — left zero
    BrStandardHeader.SealCrc(buf);
    return buf;
  }

  /// <summary>
  /// Builds an <see cref="AomeiConstants.InfoTypeImageEncrypt"/> record
  /// (0x18 bytes total).
  /// </summary>
  public static byte[] BuildEncrypt(uint method, uint keyLen) {
    var buf = new byte[0x18];
    new BrStandardHeader(0x18, AomeiConstants.InfoTypeImageEncrypt, 0).Write(buf);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), method);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), keyLen);
    BrStandardHeader.SealCrc(buf);
    return buf;
  }

  /// <summary>
  /// Builds an <see cref="AomeiConstants.InfoTypeImagePassword"/> record
  /// (0x20 bytes total) by MD5-hashing the supplied password's UTF-16LE
  /// bytes, matching <c>ImageWriter::AddPassword</c> at
  /// <c>ImgFile.dll!FUN_180014a30</c>. Passwords that match the literal
  /// <see cref="AomeiConstants.SchedulerMagicPassword"/> are <b>not</b>
  /// substituted here — the substitution requires the runtime scheduled-task
  /// context which is not available offline.
  /// </summary>
  public static byte[] BuildPassword(string password) {
    ArgumentNullException.ThrowIfNull(password);
    var utf16 = Encoding.Unicode.GetBytes(password);
    return BuildPasswordFromBytes(utf16);
  }

  /// <summary>
  /// Builds the password record from already-encoded password bytes — useful
  /// when round-tripping a sample whose original encoding isn't UTF-16LE or
  /// when testing the scheduled-task substitution against a real context
  /// struct.
  /// </summary>
  public static byte[] BuildPasswordFromBytes(ReadOnlySpan<byte> passwordBytes) {
    var hash = MD5.HashData(passwordBytes);
    var buf = new byte[0x20];
    new BrStandardHeader(0x20, AomeiConstants.InfoTypeImagePassword, 0).Write(buf);
    hash.AsSpan(0, 16).CopyTo(buf.AsSpan(12, 16));
    // bytes 28..31 = trailing pad — left zero
    BrStandardHeader.SealCrc(buf);
    return buf;
  }

  /// <summary>
  /// Builds an <see cref="AomeiConstants.InfoTypeBackupType"/> record
  /// (0x14 bytes total).
  /// </summary>
  public static byte[] BuildBackupType(uint kind) {
    var buf = new byte[0x14];
    new BrStandardHeader(0x14, AomeiConstants.InfoTypeBackupType, 0).Write(buf);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), kind);
    BrStandardHeader.SealCrc(buf);
    return buf;
  }
}
