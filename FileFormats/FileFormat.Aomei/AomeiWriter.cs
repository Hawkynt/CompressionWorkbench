#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// Builder for AOMEI <c>.adi</c>/<c>.afi</c> images. Produces a
/// well-formed <c>BIFH</c>-magic + <c>BR_STANDARD_HEADER</c> + <c>BIFT</c>
/// container that round-trips through <see cref="AomeiReader"/>.
///
/// <para>
/// <b>What this writer ships:</b> a real wire-format-correct outer
/// container. The 12-byte standard headers (Size/Type/Crc32) and the
/// BIFH/BIFT magics are emitted per the recovered spec, and the CRC32
/// fields are sealed against the on-disk bytes.
/// </para>
///
/// <para>
/// <b>What this writer does <i>not</i> ship:</b>
/// </para>
/// <list type="bullet">
///   <item><description>The 0x650 / 0x668-byte head/tail bodies are left
///         zeroed because the field layout past the first 12 bytes is TODO
///         per spec §10.1. Containers produced by this writer therefore
///         round-trip through our own reader but will not necessarily be
///         accepted by the AOMEI Backupper application — the application
///         very likely expects specific GUID / version / index-offset
///         fields in those body regions.</description></item>
///   <item><description>The <c>INDEX_TYPE_*</c> record bodies are not built
///         (only the type-code enumeration is recovered; the layouts are
///         TODO per spec §10.5). Inputs are emitted as raw byte sequences
///         wrapped in <c>BR_STANDARD_HEADER</c>-prefixed envelopes with a
///         vendor-namespace type code (<see cref="UserDataTypeTag"/>); the
///         reader walks them as opaque records.</description></item>
///   <item><description>Compression and encryption are advertised via INFO
///         records (so the round-trip captures the intent) but the payload
///         bytes are stored verbatim — implementing the on-the-wire
///         LZ4/zlib/AES wrappers without a reference sample to validate
///         against would be speculative.</description></item>
/// </list>
/// </summary>
public sealed class AomeiWriter {

  /// <summary>Vendor-namespace type tag used for opaque user-data envelopes
  /// produced by this writer. Sits in the <c>0xF000+</c> range to avoid
  /// collisions with any recovered or yet-to-be-recovered AOMEI
  /// <c>INFO_TYPE_*</c> / <c>INDEX_TYPE_*</c> code (all observed values are
  /// below <c>0x0200</c>). A reader produced by this project recognises
  /// the tag; the AOMEI application will reject it, which is the honest
  /// behaviour — we are not claiming on-wire compatibility with the
  /// vendor.</summary>
  public const ushort UserDataTypeTag = 0xF001;

  /// <summary>32-byte filename prefix written before the user-data payload
  /// inside the <see cref="UserDataTypeTag"/> envelope. ASCII, NUL-padded.</summary>
  public const int UserDataNameLength = 32;

  /// <summary>Optional backup-type kind code embedded as
  /// <see cref="AomeiConstants.InfoTypeBackupType"/>. Null means no record
  /// emitted.</summary>
  public uint? BackupTypeKind { get; init; }

  /// <summary>Optional compress method (and level) — null means no
  /// <see cref="AomeiConstants.InfoTypeImageCompress"/> record emitted.</summary>
  public (uint Method, uint Level)? CompressInfo { get; init; }

  /// <summary>Optional encrypt method (and key length) — null means no
  /// <see cref="AomeiConstants.InfoTypeImageEncrypt"/> record emitted.</summary>
  public (uint Method, uint KeyLen)? EncryptInfo { get; init; }

  /// <summary>Optional password — when non-null an
  /// <see cref="AomeiConstants.InfoTypeImagePassword"/> record carrying
  /// MD5(UTF-16LE(password)) is emitted, matching
  /// <c>ImageWriter::AddPassword</c> at <c>ImgFile.dll!FUN_180014a30</c>.</summary>
  public string? Password { get; init; }

  /// <summary>User-data payload records to embed between the head and the
  /// tail. Each tuple is (name, bytes). Empty list means no user data —
  /// the resulting container is just a sealed head/tail pair, which is
  /// still a valid round-trip baseline.</summary>
  public IReadOnlyList<(string Name, byte[] Data)> UserData { get; init; } = [];

  /// <summary>Builds the full image bytes ready to write to disk.</summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    // 1. Head — sealed CRC.
    ms.Write(BrFileHead.BuildEmpty());

    // 2. INFO records describing the container.
    if (this.BackupTypeKind is uint kind)
      ms.Write(AomeiInfoRecord.BuildBackupType(kind));
    if (this.CompressInfo is { } ci)
      ms.Write(AomeiInfoRecord.BuildCompress(ci.Method, ci.Level));
    if (this.EncryptInfo is { } ei)
      ms.Write(AomeiInfoRecord.BuildEncrypt(ei.Method, ei.KeyLen));
    if (!string.IsNullOrEmpty(this.Password))
      ms.Write(AomeiInfoRecord.BuildPassword(this.Password));

    // 3. User-data envelopes for each input file.
    foreach (var (name, data) in this.UserData)
      ms.Write(BuildUserDataRecord(name, data));

    // 4. Tail — sealed CRC.
    ms.Write(BrFileTail.BuildEmpty());

    return ms.ToArray();
  }

  /// <summary>
  /// Wraps a single user input in a <see cref="UserDataTypeTag"/>
  /// envelope:
  /// <code>
  /// BR_STANDARD_HEADER { Size, Type=0xF001, Crc32 }
  ///   + filename[32] (UTF-8, NUL-padded)
  ///   + raw payload bytes
  /// </code>
  /// </summary>
  internal static byte[] BuildUserDataRecord(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var nameBytes = new byte[UserDataNameLength];
    var src = System.Text.Encoding.UTF8.GetBytes(name);
    // Truncate at 31 to keep the trailing NUL terminator.
    Array.Copy(src, nameBytes, Math.Min(src.Length, UserDataNameLength - 1));
    var totalSize = AomeiConstants.StandardHeaderSize + UserDataNameLength + data.Length;
    var buf = new byte[totalSize];
    new BrStandardHeader((uint)totalSize, UserDataTypeTag, 0).Write(buf);
    nameBytes.CopyTo(buf.AsSpan(AomeiConstants.StandardHeaderSize, UserDataNameLength));
    data.CopyTo(buf.AsSpan(AomeiConstants.StandardHeaderSize + UserDataNameLength));
    BrStandardHeader.SealCrc(buf);
    return buf;
  }

  /// <summary>Reads back the filename embedded by
  /// <see cref="BuildUserDataRecord"/>. Returns the empty string when the
  /// body is too short.</summary>
  public static string ReadUserDataName(ReadOnlySpan<byte> body) {
    if (body.Length < UserDataNameLength) return string.Empty;
    var nameSpan = body[..UserDataNameLength];
    var nul = nameSpan.IndexOf((byte)0);
    if (nul < 0) nul = UserDataNameLength;
    return System.Text.Encoding.UTF8.GetString(nameSpan[..nul]);
  }

  /// <summary>Reads back the payload bytes embedded by
  /// <see cref="BuildUserDataRecord"/>. Returns an empty array when the
  /// body is too short to contain the name prefix.</summary>
  public static byte[] ReadUserDataPayload(ReadOnlySpan<byte> body) {
    if (body.Length <= UserDataNameLength) return [];
    return body[UserDataNameLength..].ToArray();
  }
}
