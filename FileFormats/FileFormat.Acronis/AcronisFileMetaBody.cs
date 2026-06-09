#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Acronis;

/// <summary>
/// Identifiers for resident attribute types found inside Acronis classic .tib FileMeta record
/// bodies (record types 102, 1, 2, 5). Values are reverse-engineered from the attribute dispatch
/// in <c>ti_tools.dll</c> 32-bit (Acronis True Image 2018), specifically the per-id
/// <c>TakeAttribute*</c> / <c>PreloadAttribute*</c> handlers in
/// <c>archive\ver2\file\item_supp.cpp</c>. The InputItem model wraps each backed-up entity as a
/// stream of (id+flags, size, body) tuples and dispatches by id at load time.
/// </summary>
/// <remarks>
/// <para>
/// The attribute id is stored as a uint32 in which bit 23 (<c>0x00800000</c>) is reserved as the
/// "deduplicated" / "stored-by-hash" flag — when set, the 16-byte body is an MD5 hash that names
/// a deduplicated attribute body kept in a separate table. The effective id used for dispatch is
/// <c>raw &amp; 0xff7fffff</c> (<see cref="AcronisAttributeRaw.UnmaskedId"/>). This mask matches
/// the implementation of <c>ArchiveApi::InputResidentAttributeEnumerator::GetId</c>.
/// </para>
/// <para>
/// Only the ids whose body shape is decoded from the binary RE pass are enumerated below. Other
/// ids encountered in the wild are surfaced as raw <see cref="AcronisRawAttribute"/> entries so
/// callers can still see them.
/// </para>
/// </remarks>
public enum AcronisAttributeId : uint {
  /// <summary>
  /// File/directory common attributes — carries the entry's primary UTF-16LE name and optional
  /// alternate (8.3) name, plus a 44-byte fixed header that includes the two name lengths and
  /// other still-undecoded fields. Source: <c>CommonAttributesImpl::TakeAttributeItemCommon</c>.
  /// </summary>
  ItemCommon = 0x10,
  /// <summary>
  /// Hard-link group id (8-byte body, treated as a uint64 cookie). Two entries with the same
  /// HardLinkId belong to the same hard-link group. Source:
  /// <c>FileItemImpl::TakeAttributeHardLinkId</c>.
  /// </summary>
  HardLinkId = 0x14,
  /// <summary>
  /// Replica handle (24-byte body, structure not fully decoded). Source:
  /// <c>ReplicaItemImpl::TakeAttributeReplica</c>.
  /// </summary>
  Replica = 0x17,
  /// <summary>
  /// ItemCommon secondary — an 8-byte uint64 cookie tied to the ItemCommon record.
  /// Source: tail branch of <c>CommonAttributesImpl::TakeAttributeItemCommon</c>.
  /// </summary>
  ItemCommonExtra = 0x18,
  /// <summary>
  /// Source-item path attribute — 8-byte fixed header (uint16 pathLength, uint16 kind, uint32
  /// id) followed by <c>pathLength * 2</c> UTF-16LE bytes of source path. Source:
  /// <c>SourceItemImpl::PreloadAttributeSourceItem</c>.
  /// </summary>
  SourceItem = 0x40,
  /// <summary>
  /// Backup time (8 bytes, FILETIME-ish — same wall-clock semantics as Listing's
  /// <see cref="AcronisFileEntry.Time"/> 48-bit timestamp). Source:
  /// <c>BackupTimeItemImpl::PreloadAttributeBackupTime</c>.
  /// </summary>
  BackupTime = 0x50,
  /// <summary>
  /// Time-zone offset (4 bytes, int32 minutes-from-UTC). Source:
  /// <c>TimeZoneItemImpl::PreloadAttributeTimeZone</c>.
  /// </summary>
  TimeZone = 0x60,
  /// <summary>
  /// Slice-item attribute — 0x19-byte fixed header plus a UTF-16LE name. Source:
  /// <c>SliceItemImpl::PreloadAttributes</c> id-0x80 branch.
  /// </summary>
  SliceItem = 0x80,
  /// <summary>
  /// Slice-item comment/blob (variable length). Source: <c>SliceItemImpl::PreloadAttributes</c>
  /// id-0x90 branch.
  /// </summary>
  SliceItemBlob = 0x90,
}

/// <summary>
/// Raw 6-byte attribute header as it appears on-disk in an Acronis classic .tib FileMeta record
/// body. Each attribute is <c>[6-byte header][size bytes of body]</c>.
/// </summary>
/// <param name="RawIdAndFlags">
/// Combined id-plus-flags uint32. Bit 23 is the "deduplicated" / stored-by-hash flag.
/// </param>
/// <param name="Size">Body length in bytes (uint16). Includes the body only, NOT the header.</param>
public readonly record struct AcronisAttributeRaw(uint RawIdAndFlags, ushort Size) {
  /// <summary>
  /// Logical attribute id with bit 23 masked off — matches the binary-RE'd
  /// <c>InputResidentAttributeEnumerator::GetId</c> implementation.
  /// </summary>
  public uint UnmaskedId => this.RawIdAndFlags & 0xff7fffffu;

  /// <summary><c>true</c> iff bit 23 is set (deduplicated / stored-by-hash body).</summary>
  public bool IsDeduplicated => (this.RawIdAndFlags & 0x00800000u) != 0;
}

/// <summary>
/// One parsed attribute from a FileMeta record body.
/// </summary>
/// <param name="Header">The 6-byte on-disk header.</param>
/// <param name="Body">
/// Body bytes. When <see cref="AcronisAttributeRaw.IsDeduplicated"/> is true and
/// <c>Size == 16</c>, this is the 16-byte MD5 referring to a deduplicated body kept in the
/// archive's attribute-hash table (not currently dereferenced — the table itself lives elsewhere
/// in the .tib).
/// </param>
public sealed record AcronisRawAttribute(AcronisAttributeRaw Header, byte[] Body) {
  /// <summary>Convenience accessor — same as <c>Header.UnmaskedId</c>.</summary>
  public uint Id => this.Header.UnmaskedId;
}

/// <summary>
/// Decoded ItemCommon (id 0x10) attribute body — carries the file/directory <see cref="Name"/>
/// and optional <see cref="AltName"/>, plus the 44-byte fixed header whose remaining fields are
/// kept verbatim in <see cref="FixedHeader"/> for diagnostic/round-trip purposes.
/// </summary>
/// <param name="Name">Primary name (UTF-16LE, length <see cref="NameLength"/> chars).</param>
/// <param name="AltName">Alternate 8.3 name (UTF-16LE), or <c>null</c> when absent.</param>
/// <param name="NameLength">Name length in UTF-16 code units (i.e., chars).</param>
/// <param name="AltNameLength">Alt-name length in UTF-16 code units (zero when absent).</param>
/// <param name="FixedHeader">Verbatim copy of the 44-byte fixed header preceding the names.</param>
public sealed record AcronisItemCommonAttribute(
  string Name,
  string? AltName,
  ushort NameLength,
  ushort AltNameLength,
  byte[] FixedHeader
);

/// <summary>
/// Decoded SourceItem (id 0x40) attribute body — carries the source <see cref="Path"/> string
/// plus the 8-byte fixed header.
/// </summary>
/// <param name="Path">Source path (UTF-16LE).</param>
/// <param name="PathLength">Path length in UTF-16 code units.</param>
/// <param name="Kind">Second uint16 in the fixed header — semantics not fully decoded.</param>
/// <param name="Id">uint32 immediately after the (length, kind) pair — source-item handle.</param>
public sealed record AcronisSourceItemAttribute(
  string Path,
  ushort PathLength,
  ushort Kind,
  uint Id
);

/// <summary>
/// Decoded body of a FileMeta record (102, 1, 2, or 5). Carries every parsed attribute
/// (<see cref="Attributes"/>), the unmasked-id index (<see cref="AttributesById"/>), and the
/// high-level decoded fields whose layouts are documented in the type comments below.
/// </summary>
/// <param name="AttributeCount">Declared attribute count read from the body's leading uint32.</param>
/// <param name="Attributes">Every attribute encountered, in on-disk order.</param>
/// <param name="ItemCommon">Decoded ItemCommon attribute body, or <c>null</c> when absent.</param>
/// <param name="SourceItem">Decoded SourceItem attribute body, or <c>null</c> when absent.</param>
/// <param name="HardLinkId">Decoded 8-byte HardLinkId, or <c>null</c> when absent.</param>
/// <param name="BackupTime">Decoded 8-byte BackupTime, or <c>null</c> when absent.</param>
/// <param name="TimeZoneMinutes">Decoded TimeZone (4-byte int32 minutes), or <c>null</c> when absent.</param>
public sealed record AcronisFileMetaBody(
  uint AttributeCount,
  IReadOnlyList<AcronisRawAttribute> Attributes,
  AcronisItemCommonAttribute? ItemCommon,
  AcronisSourceItemAttribute? SourceItem,
  ulong? HardLinkId,
  ulong? BackupTime,
  int? TimeZoneMinutes
) {
  /// <summary>Look up attributes by their unmasked id.</summary>
  public IReadOnlyList<AcronisRawAttribute> AttributesById(uint id)
    => this.Attributes.Where(a => a.Id == id).ToList();
}

/// <summary>
/// Decoder for inflated FileMeta record bodies (record types 102, 1, 2, 5).
/// </summary>
/// <remarks>
/// <para>
/// On-disk shape (after the record's deflate has been decompressed):
/// </para>
/// <code>
/// uint32 attributeCount
/// foreach attribute:
///   uint32 idAndFlags    ; low 23 bits = id; bit 23 = "deduplicated" flag; high bits not seen
///   uint16 size          ; body length in bytes
///   byte[size] body      ; deduplicated path: 16-byte MD5 referring to a side table
/// </code>
/// <para>
/// This shape is reverse-engineered from the InputItem/InputResidentAttributeEnumerator
/// machinery in <c>ti_tools.dll</c> 32-bit (Acronis True Image 2018), specifically:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>InputItem::PreloadResidentAttributes</c> reads the leading 4-byte count then loops
///   reading 6-byte headers; bit 0x800000 in the dword steers the dedup-by-hash vs. inline branch.
///   </description></item>
///   <item><description>
///   <c>InputResidentAttributeEnumerator::GetId</c> returns <c>(raw &amp; 0xff7fffff)</c> — the
///   id with the bit-23 dedup flag masked off.
///   </description></item>
///   <item><description>
///   <c>InputResidentAttributeEnumerator::Read</c> copies <c>size</c> bytes from
///   <c>body + 6</c> — i.e., right after the 6-byte header.
///   </description></item>
/// </list>
/// <para>
/// The per-id body layouts (<see cref="AcronisAttributeId.ItemCommon"/>,
/// <see cref="AcronisAttributeId.SourceItem"/>, <see cref="AcronisAttributeId.HardLinkId"/>,
/// <see cref="AcronisAttributeId.BackupTime"/>, <see cref="AcronisAttributeId.TimeZone"/>) are
/// reverse-engineered from the per-id <c>TakeAttribute*</c> / <c>PreloadAttribute*</c> handlers
/// in <c>archive\ver2\file\item_supp.cpp</c>. Ids that aren't recognized are surfaced as raw
/// bodies so callers can still inspect them.
/// </para>
/// </remarks>
public static class AcronisFileMetaBodyDecoder {

  /// <summary>
  /// Parses an inflated FileMeta record body into a structured <see cref="AcronisFileMetaBody"/>.
  /// Returns <c>null</c> when the body is too short to even read the leading attribute count.
  /// Truncated or malformed attribute streams produce a partial result rather than throwing —
  /// the caller is expected to treat the partial result as best-effort diagnostic data.
  /// </summary>
  /// <param name="payload">Inflated record body (NOT the deflate-compressed on-disk bytes).</param>
  public static AcronisFileMetaBody? Decode(byte[] payload) {
    ArgumentNullException.ThrowIfNull(payload);
    return Decode(payload.AsSpan());
  }

  /// <inheritdoc cref="Decode(byte[])"/>
  public static AcronisFileMetaBody? Decode(ReadOnlySpan<byte> payload) {
    if (payload.Length < 4) return null;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
    var attrs = new List<AcronisRawAttribute>(count > 1024 ? 0 : (int)count);
    var p = 4;
    // Cap the loop by both the declared count and the buffer — defensive against forged counts.
    for (var i = 0u; i < count && i < int.MaxValue; i++) {
      if (p + 6 > payload.Length) break; // truncated header — stop here, surface what we have
      var raw = BinaryPrimitives.ReadUInt32LittleEndian(payload[p..]);
      var size = BinaryPrimitives.ReadUInt16LittleEndian(payload[(p + 4)..]);
      p += 6;
      if (p + size > payload.Length) break; // truncated body — stop here
      var body = payload.Slice(p, size).ToArray();
      p += size;
      attrs.Add(new AcronisRawAttribute(new AcronisAttributeRaw(raw, size), body));
    }

    // Decode the high-value ids that we have a binary-RE'd layout for.
    AcronisItemCommonAttribute? itemCommon = null;
    AcronisSourceItemAttribute? sourceItem = null;
    ulong? hardLink = null;
    ulong? backupTime = null;
    int? timeZone = null;
    foreach (var a in attrs) {
      // Deduplicated bodies are 16-byte MD5s in a side table we don't have here — skip them in
      // the high-level decode so we don't mistake the hash for an inline body.
      if (a.Header.IsDeduplicated) continue;
      switch (a.Id) {
        case (uint)AcronisAttributeId.ItemCommon:
          itemCommon ??= DecodeItemCommon(a.Body);
          break;
        case (uint)AcronisAttributeId.SourceItem:
          sourceItem ??= DecodeSourceItem(a.Body);
          break;
        case (uint)AcronisAttributeId.HardLinkId:
          if (a.Body.Length >= 8) hardLink ??= BinaryPrimitives.ReadUInt64LittleEndian(a.Body);
          break;
        case (uint)AcronisAttributeId.BackupTime:
          if (a.Body.Length >= 8) backupTime ??= BinaryPrimitives.ReadUInt64LittleEndian(a.Body);
          break;
        case (uint)AcronisAttributeId.TimeZone:
          if (a.Body.Length >= 4) timeZone ??= BinaryPrimitives.ReadInt32LittleEndian(a.Body);
          break;
      }
    }

    return new AcronisFileMetaBody(count, attrs, itemCommon, sourceItem, hardLink, backupTime, timeZone);
  }

  /// <summary>
  /// Decodes an ItemCommon (id 0x10) attribute body. Layout from
  /// <c>CommonAttributesImpl::TakeAttributeItemCommon</c>:
  /// <code>
  ///   uint16 nameLength       ; characters (UTF-16 code units)
  ///   uint16 altNameLength    ; characters
  ///   byte[40] fixedRest      ; remaining 40 bytes of the 44-byte fixed header — layout TBD
  ///   byte[nameLength*2]    name   (UTF-16LE)
  ///   byte[altNameLength*2] altName (UTF-16LE; empty when altNameLength == 0)
  /// </code>
  /// Returns <c>null</c> when the body is too short to hold even the 44-byte fixed header.
  /// </summary>
  public static AcronisItemCommonAttribute? DecodeItemCommon(ReadOnlySpan<byte> body) {
    const int FixedHeaderLength = 44;
    if (body.Length < FixedHeaderLength) return null;
    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var altLen = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
    var fixedHeader = body[..FixedHeaderLength].ToArray();
    var p = FixedHeaderLength;
    var nameBytes = (int)nameLen * 2;
    var altBytes = (int)altLen * 2;
    if (p + nameBytes > body.Length) return null;
    var name = nameLen == 0 ? string.Empty : Encoding.Unicode.GetString(body.Slice(p, nameBytes));
    p += nameBytes;
    string? altName = null;
    if (altLen > 0) {
      if (p + altBytes <= body.Length)
        altName = Encoding.Unicode.GetString(body.Slice(p, altBytes));
    }
    return new AcronisItemCommonAttribute(name, altName, nameLen, altLen, fixedHeader);
  }

  /// <summary>
  /// Decodes a SourceItem (id 0x40) attribute body. Layout from
  /// <c>SourceItemImpl::PreloadAttributeSourceItem</c>:
  /// <code>
  ///   uint16 pathLength    ; characters
  ///   uint16 kind          ; semantics not fully decoded; preserved verbatim
  ///   uint32 id            ; source-item handle
  ///   byte[pathLength*2] path (UTF-16LE)
  /// </code>
  /// Returns <c>null</c> when the body is too short to hold the 8-byte fixed header.
  /// </summary>
  public static AcronisSourceItemAttribute? DecodeSourceItem(ReadOnlySpan<byte> body) {
    if (body.Length < 8) return null;
    var pathLen = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var kind = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
    var id = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
    var pathBytes = (int)pathLen * 2;
    if (8 + pathBytes > body.Length) return null;
    var path = pathLen == 0 ? string.Empty : Encoding.Unicode.GetString(body.Slice(8, pathBytes));
    return new AcronisSourceItemAttribute(path, pathLen, kind, id);
  }
}
