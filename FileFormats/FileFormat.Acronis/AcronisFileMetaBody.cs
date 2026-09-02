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
/// and optional <see cref="AltName"/>, plus the typed fields decoded from the 44-byte fixed
/// header.
/// </summary>
/// <remarks>
/// <para>
/// The 44-byte fixed header layout is reverse-engineered from
/// <c>ArchiveApi::ItemBackuperImpl::BackupCommonAttributes</c> in <c>ti_tools.dll</c>
/// (<c>k:\9202\archive\ver2\file\backup_operation.cpp</c>), which is the symmetric writer for
/// the ItemCommon record. The writer emits the body via the following 11 contiguous writes:
/// </para>
/// <code>
///   offset 0  : uint16 nameLength      ; UTF-16 code units of primary name
///   offset 2  : uint16 altNameLength   ; UTF-16 code units of alt (8.3) name
///   offset 4  : uint32 dosAttributes   ; Windows file attribute bits (FILE_ATTRIBUTE_*)
///   offset 8  : uint64 creationTime    ; FILETIME — written from FileItem vtable[0x18]
///   offset 16 : uint64 lastWriteTime   ; FILETIME — written from FileItem vtable[0x5c]
///   offset 24 : uint64 lastAccessTime  ; FILETIME — written from FileItem vtable[0x24]
///   offset 32 : uint64 changeTime      ; FILETIME — written from FileItem vtable[0x28]
///   offset 40 : uint32 trailer         ; final dword — written from FileItem vtable[0x34]
///   offset 44 : UTF-16LE name (nameLength chars)
///   offset 44 + nameLength*2: UTF-16LE altName (altNameLength chars)
/// </code>
/// <para>
/// The four FILETIMEs are stored in their native 100-ns-since-1601 Windows wall-clock encoding.
/// The trailer dword at offset 40 carries a still-undecoded field (likely a flags byte or fork
/// count); its raw value is surfaced via <see cref="TrailerDword"/> for round-tripping.
/// </para>
/// <para>
/// The semantic order (CreationTime, LastWriteTime, LastAccessTime, ChangeTime) matches the
/// NTFS <c>$STANDARD_INFORMATION</c> layout and the related
/// <c>ContinuousAttributeWrapperImpl::ReadStandardAttributes</c> path which reads a 28-byte
/// short form (DOS attributes + first 3 FILETIMEs).
/// </para>
/// </remarks>
/// <param name="Name">Primary name (UTF-16LE, length <see cref="NameLength"/> chars).</param>
/// <param name="AltName">Alternate 8.3 name (UTF-16LE), or <c>null</c> when absent.</param>
/// <param name="NameLength">Name length in UTF-16 code units (i.e., chars).</param>
/// <param name="AltNameLength">Alt-name length in UTF-16 code units (zero when absent).</param>
/// <param name="DosAttributes">
/// Windows file attribute bits at offset 4 (FILE_ATTRIBUTE_NORMAL, _DIRECTORY, _READONLY, etc.).
/// </param>
/// <param name="CreationTime">
/// FILETIME at offset 8 — wall-clock creation time of the source file at backup capture.
/// </param>
/// <param name="LastWriteTime">
/// FILETIME at offset 16 — wall-clock last-write time of the source file at backup capture.
/// </param>
/// <param name="LastAccessTime">
/// FILETIME at offset 24 — wall-clock last-access time of the source file at backup capture.
/// </param>
/// <param name="ChangeTime">
/// FILETIME at offset 32 — wall-clock NTFS change time (mft-change) of the source file at
/// backup capture.
/// </param>
/// <param name="TrailerDword">
/// Trailer uint32 at offset 40 — semantics not fully decoded; preserved verbatim.
/// </param>
/// <param name="FixedHeader">
/// Verbatim copy of the entire 44-byte fixed header preceding the names — kept for diagnostic /
/// round-trip purposes alongside the typed fields above.
/// </param>
public sealed record AcronisItemCommonAttribute(
  string Name,
  string? AltName,
  ushort NameLength,
  ushort AltNameLength,
  uint DosAttributes,
  ulong CreationTime,
  ulong LastWriteTime,
  ulong LastAccessTime,
  ulong ChangeTime,
  uint TrailerDword,
  byte[] FixedHeader
) {
  /// <summary>
  /// <see cref="CreationTime"/> as a <see cref="DateTime"/> in UTC, or <c>null</c> when the
  /// FILETIME is zero (unset) or outside the representable range.
  /// </summary>
  public DateTime? CreationTimeUtc => TryToDateTimeUtc(this.CreationTime);

  /// <summary>
  /// <see cref="LastWriteTime"/> as a <see cref="DateTime"/> in UTC, or <c>null</c> when the
  /// FILETIME is zero (unset) or outside the representable range.
  /// </summary>
  public DateTime? LastWriteTimeUtc => TryToDateTimeUtc(this.LastWriteTime);

  /// <summary>
  /// <see cref="LastAccessTime"/> as a <see cref="DateTime"/> in UTC, or <c>null</c> when the
  /// FILETIME is zero (unset) or outside the representable range.
  /// </summary>
  public DateTime? LastAccessTimeUtc => TryToDateTimeUtc(this.LastAccessTime);

  /// <summary>
  /// <see cref="ChangeTime"/> as a <see cref="DateTime"/> in UTC, or <c>null</c> when the
  /// FILETIME is zero (unset) or outside the representable range.
  /// </summary>
  public DateTime? ChangeTimeUtc => TryToDateTimeUtc(this.ChangeTime);

  private static DateTime? TryToDateTimeUtc(ulong filetime) {
    if (filetime == 0) return null;
    try {
      return DateTime.FromFileTimeUtc((long)filetime);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}

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
/// Decoded Replica (id 0x17) attribute body — carries a 16-byte GUID and two trailing uint32s.
/// </summary>
/// <remarks>
/// <para>
/// Layout from <c>ArchiveApi::ReplicaItemImpl::TakeAttributeReplica</c>:
/// </para>
/// <code>
///   byte[16] guid          ; read directly, then byte-swapped to canonical GUID form
///                          ; (Data1 32-bit BE↔LE, Data2 16-bit BE↔LE, Data3 16-bit BE↔LE,
///                          ;  Data4 8-byte tail untouched) — same conversion that
///                          ;  FUN_00fed680 in ti_tools.dll applies on the read path
///   uint32   value1        ; semantics not fully decoded (cookie A)
///   uint32   value2        ; semantics not fully decoded (cookie B)
/// </code>
/// <para>
/// Body size on the wire is 0x18 (24) bytes — the binary's reader throws on any other size.
/// </para>
/// </remarks>
/// <param name="Guid">Replica's source GUID, canonicalized for display.</param>
/// <param name="RawGuidBytes">Verbatim 16-byte GUID block as it appears on disk (pre-swap).</param>
/// <param name="Value1">First trailing uint32 — semantics not fully decoded.</param>
/// <param name="Value2">Second trailing uint32 — semantics not fully decoded.</param>
public sealed record AcronisReplicaAttribute(
  Guid Guid,
  byte[] RawGuidBytes,
  uint Value1,
  uint Value2
);

/// <summary>
/// Decoded ItemCommonExtra (id 0x18) attribute body — an 8-byte cookie tied to the ItemCommon
/// record, written by <c>BackupCommonAttributes</c> via FileItem vtable[0x2c].
/// </summary>
/// <remarks>
/// <para>
/// The body is exactly 8 bytes on the wire (the binary's reader throws on any other size).
/// Treated as a uint64; the most likely semantics is a USN / change-id / object-id cookie
/// (note: <c>FileItemImpl::TakeAttributeHardLinkId</c> uses a near-identical 8-byte read path
/// for the hard-link group id stored at <c>+0xc8</c>; ItemCommonExtra writes to a different
/// slot in the parent item struct).
/// </para>
/// </remarks>
/// <param name="Value">8-byte cookie value, interpreted as uint64 little-endian.</param>
public sealed record AcronisItemCommonExtraAttribute(ulong Value);

/// <summary>
/// Decoded SliceItem (id 0x80) attribute body — carries a 16-byte slice GUID, two uint32 cookies,
/// a 1-byte flag, and (optionally) a trailing UTF-16LE name.
/// </summary>
/// <remarks>
/// <para>
/// Layout from <c>ArchiveApi::SliceItemImpl::PreloadAttributes</c> id-0x80 branch:
/// </para>
/// <code>
///   byte[16] guid          ; 16-byte slice GUID, byte-swapped on read (same as Replica's)
///   uint32   value1        ; cookie A — written to parent slot +0x10
///   uint32   value2        ; cookie B — written to parent slot +0x14
///   byte     flag          ; written to parent slot +0x30 as a bool
///                          ;   (the reader uses (local_1c == 1) to coerce to bool)
///   ; --- 25-byte fixed body ends here ---
///   ; optional extended tail (when the body is longer than 25 bytes):
///   uint8    pad           ; reader's local_1b sentinel slot
///   uint16   nameLength    ; UTF-16 code units of the trailing name
///   byte[nameLength*2] name (UTF-16LE)
/// </code>
/// <para>
/// In the common 25-byte short form, <see cref="Name"/> is <c>null</c>. When the body extends
/// past 25 bytes and the reader's sentinel marker indicates an attached name, the name is
/// surfaced verbatim.
/// </para>
/// </remarks>
/// <param name="Guid">Slice GUID, canonicalized for display.</param>
/// <param name="RawGuidBytes">Verbatim 16-byte GUID block as it appears on disk (pre-swap).</param>
/// <param name="Value1">First trailing uint32 — semantics not fully decoded.</param>
/// <param name="Value2">Second trailing uint32 — semantics not fully decoded.</param>
/// <param name="Flag">Single-byte flag at offset 24, written as a bool by the reader.</param>
/// <param name="Name">Optional UTF-16LE trailing name when the body carries the extended tail.</param>
/// <param name="NameLength">UTF-16 code units of <see cref="Name"/>, or zero when absent.</param>
public sealed record AcronisSliceItemAttribute(
  Guid Guid,
  byte[] RawGuidBytes,
  uint Value1,
  uint Value2,
  byte Flag,
  string? Name,
  ushort NameLength
);

/// <summary>
/// Decoded SliceItemBlob (id 0x90) attribute body — a variable-length opaque payload, surfaced
/// verbatim. The blob is read via <c>SliceItemImpl::PreloadAttributes</c>' id-0x90 branch with
/// no internal framing decoded; consumers typically interpret it as a sequence of UTF-16 code
/// units for slice-comment text but the binary's reader makes no such assumption.
/// </summary>
/// <param name="Bytes">Verbatim blob bytes as they appear on the wire.</param>
public sealed record AcronisSliceItemBlobAttribute(byte[] Bytes);

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
/// <param name="Replica">Decoded Replica attribute body, or <c>null</c> when absent.</param>
/// <param name="ItemCommonExtra">Decoded ItemCommonExtra cookie, or <c>null</c> when absent.</param>
/// <param name="SliceItem">Decoded SliceItem attribute body, or <c>null</c> when absent.</param>
/// <param name="SliceItemBlob">Decoded SliceItemBlob attribute body, or <c>null</c> when absent.</param>
public sealed record AcronisFileMetaBody(
  uint AttributeCount,
  IReadOnlyList<AcronisRawAttribute> Attributes,
  AcronisItemCommonAttribute? ItemCommon,
  AcronisSourceItemAttribute? SourceItem,
  ulong? HardLinkId,
  ulong? BackupTime,
  int? TimeZoneMinutes,
  AcronisReplicaAttribute? Replica,
  AcronisItemCommonExtraAttribute? ItemCommonExtra,
  AcronisSliceItemAttribute? SliceItem,
  AcronisSliceItemBlobAttribute? SliceItemBlob
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
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
    AcronisReplicaAttribute? replica = null;
    AcronisItemCommonExtraAttribute? itemCommonExtra = null;
    AcronisSliceItemAttribute? sliceItem = null;
    AcronisSliceItemBlobAttribute? sliceItemBlob = null;
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
        case (uint)AcronisAttributeId.Replica:
          replica ??= DecodeReplica(a.Body);
          break;
        case (uint)AcronisAttributeId.ItemCommonExtra:
          if (a.Body.Length >= 8)
            itemCommonExtra ??= new AcronisItemCommonExtraAttribute(
              BinaryPrimitives.ReadUInt64LittleEndian(a.Body));
          break;
        case (uint)AcronisAttributeId.SliceItem:
          sliceItem ??= DecodeSliceItem(a.Body);
          break;
        case (uint)AcronisAttributeId.SliceItemBlob:
          sliceItemBlob ??= new AcronisSliceItemBlobAttribute(a.Body.ToArray());
          break;
      }
    }

    return new AcronisFileMetaBody(
      count, attrs, itemCommon, sourceItem, hardLink, backupTime, timeZone,
      replica, itemCommonExtra, sliceItem, sliceItemBlob);
  }

  /// <summary>
  /// Decodes an ItemCommon (id 0x10) attribute body. Layout reverse-engineered from
  /// <c>ArchiveApi::ItemBackuperImpl::BackupCommonAttributes</c> (the symmetric writer in
  /// <c>ti_tools.dll</c> at <c>k:\9202\archive\ver2\file\backup_operation.cpp</c>):
  /// <code>
  ///   uint16 nameLength       ; UTF-16 code units of primary name
  ///   uint16 altNameLength    ; UTF-16 code units of alt (8.3) name
  ///   uint32 dosAttributes    ; Windows file attribute bits
  ///   uint64 creationTime     ; FILETIME (100-ns ticks since 1601, UTC)
  ///   uint64 lastWriteTime    ; FILETIME
  ///   uint64 lastAccessTime   ; FILETIME
  ///   uint64 changeTime       ; FILETIME
  ///   uint32 trailer          ; final dword — semantics not fully decoded
  ///   byte[nameLength*2]    name   (UTF-16LE)
  ///   byte[altNameLength*2] altName (UTF-16LE; empty when altNameLength == 0)
  /// </code>
  /// The fixed header is exactly 44 bytes (4 + 4 + 8*4 + 4). The four FILETIMEs are reported in
  /// their raw uint64 form for round-tripping; <see cref="AcronisItemCommonAttribute"/> also
  /// exposes them as nullable <see cref="DateTime"/> properties for ergonomics.
  /// Returns <c>null</c> when the body is too short to hold the 44-byte fixed header or when
  /// the declared name length overflows the body.
  /// </summary>
  public static AcronisItemCommonAttribute? DecodeItemCommon(ReadOnlySpan<byte> body) {
    const int FixedHeaderLength = 44;
    if (body.Length < FixedHeaderLength) return null;
    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var altLen = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
    var dosAttrs = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
    var creationTime = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
    var lastWriteTime = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
    var lastAccessTime = BinaryPrimitives.ReadUInt64LittleEndian(body[24..]);
    var changeTime = BinaryPrimitives.ReadUInt64LittleEndian(body[32..]);
    var trailer = BinaryPrimitives.ReadUInt32LittleEndian(body[40..]);
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
    return new AcronisItemCommonAttribute(
      name, altName, nameLen, altLen,
      dosAttrs, creationTime, lastWriteTime, lastAccessTime, changeTime, trailer,
      fixedHeader);
  }

  /// <summary>
  /// Decodes a Replica (id 0x17) attribute body. Layout reverse-engineered from
  /// <c>ArchiveApi::ReplicaItemImpl::TakeAttributeReplica</c> in <c>ti_tools.dll</c>
  /// (<c>k:\9202\archive\ver2\file\item_supp.cpp</c>):
  /// <code>
  ///   byte[16] guid          ; 16-byte GUID — read raw, then byte-swapped to canonical
  ///                          ;   GUID form (Data1 32-bit BE↔LE, Data2 16-bit BE↔LE,
  ///                          ;   Data3 16-bit BE↔LE, Data4 8-byte tail untouched)
  ///   uint32   value1        ; cookie A
  ///   uint32   value2        ; cookie B
  /// </code>
  /// Returns <c>null</c> when the body is shorter than 24 bytes — the binary's reader throws
  /// in that case; we surface a soft <c>null</c> so the outer attribute walk keeps going.
  /// </summary>
  public static AcronisReplicaAttribute? DecodeReplica(ReadOnlySpan<byte> body) {
    const int Expected = 24;
    if (body.Length < Expected) return null;
    var rawGuid = body[..16].ToArray();
    var guid = SwapGuidByteOrder(body[..16]);
    var v1 = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
    var v2 = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
    return new AcronisReplicaAttribute(guid, rawGuid, v1, v2);
  }

  /// <summary>
  /// Decodes a SliceItem (id 0x80) attribute body. Layout reverse-engineered from
  /// <c>ArchiveApi::SliceItemImpl::PreloadAttributes</c> id-0x80 branch in <c>ti_tools.dll</c>
  /// (<c>k:\9202\archive\ver2\file\item_supp.cpp</c>):
  /// <code>
  ///   byte[16] guid          ; slice GUID, byte-swapped on read
  ///   uint32   value1        ; cookie A
  ///   uint32   value2        ; cookie B
  ///   byte     flag          ; bool flag
  ///   ; 25-byte short form ends here
  ///   ; optional extended tail (when body.Length > 25):
  ///   byte     pad           ; reader's local_1b sentinel
  ///   uint16   nameLength    ; UTF-16 code units of trailing name
  ///   byte[nameLength*2] name (UTF-16LE)
  /// </code>
  /// Returns <c>null</c> when the body is shorter than 25 bytes.
  /// </summary>
  public static AcronisSliceItemAttribute? DecodeSliceItem(ReadOnlySpan<byte> body) {
    const int FixedLen = 25;
    if (body.Length < FixedLen) return null;
    var rawGuid = body[..16].ToArray();
    var guid = SwapGuidByteOrder(body[..16]);
    var v1 = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
    var v2 = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
    var flag = body[24];
    string? name = null;
    ushort nameLen = 0;
    // Extended tail: at least 4 more bytes (1 pad + 2 nameLen + at least one UTF-16 char).
    if (body.Length >= FixedLen + 3) {
      nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body[(FixedLen + 1)..]);
      var nameStart = FixedLen + 3;
      var nameBytes = (int)nameLen * 2;
      if (nameLen > 0 && nameStart + nameBytes <= body.Length)
        name = Encoding.Unicode.GetString(body.Slice(nameStart, nameBytes));
    }
    return new AcronisSliceItemAttribute(guid, rawGuid, v1, v2, flag, name, nameLen);
  }

  /// <summary>
  /// Swaps the byte order of a 16-byte GUID block from on-disk encoding to canonical
  /// .NET <see cref="Guid"/> form. Matches the conversion that
  /// <c>FUN_00fed680</c> in <c>ti_tools.dll</c> applies on Replica / SliceItem GUID reads:
  /// the 4-byte Data1 word is bit-rotated (BE↔LE), the two 2-byte words Data2/Data3 are
  /// likewise bit-rotated, the trailing 8 bytes are copied verbatim.
  /// </summary>
  private static Guid SwapGuidByteOrder(ReadOnlySpan<byte> on_disk) {
    if (on_disk.Length < 16) return Guid.Empty;
    Span<byte> canonical = stackalloc byte[16];
    // Data1: 4 bytes BE-on-disk → LE-canonical (.NET Guid is mixed-endian, matches Windows GUID).
    canonical[0] = on_disk[3];
    canonical[1] = on_disk[2];
    canonical[2] = on_disk[1];
    canonical[3] = on_disk[0];
    // Data2: 2 bytes BE → LE
    canonical[4] = on_disk[5];
    canonical[5] = on_disk[4];
    // Data3: 2 bytes BE → LE
    canonical[6] = on_disk[7];
    canonical[7] = on_disk[6];
    // Data4: 8 bytes verbatim
    on_disk[8..].CopyTo(canonical[8..]);
    return new Guid(canonical);
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
