using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
/// Tests for the FileMeta record (102 / 1 / 2 / 5) body decoder.
/// </summary>
/// <remarks>
/// <para>
/// The body shape — uint32 attribute count plus a stream of (uint32 idAndFlags, uint16 size,
/// size-byte body) tuples — is reverse-engineered from the InputItem attribute-stream machinery
/// in <c>ti_tools.dll</c> 32-bit (Acronis True Image 2018). Specifically:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>ArchiveApi::InputItem::PreloadResidentAttributes</c> reads a 4-byte count, then loops
///   reading 6-byte headers; bit 0x800000 in the dword steers the dedup-by-hash vs.
///   inline-body branch.
///   </description></item>
///   <item><description>
///   <c>ArchiveApi::InputResidentAttributeEnumerator::GetId</c> returns the raw uint32 ANDed
///   with <c>0xff7fffff</c> — i.e., the id with the bit-23 dedup flag masked off.
///   </description></item>
///   <item><description>
///   <c>ArchiveApi::InputResidentAttributeEnumerator::Read</c> copies <c>size</c> bytes from
///   <c>body + 6</c> — i.e., right after the 6-byte header.
///   </description></item>
/// </list>
/// <para>
/// Per-id body shapes are taken from the <c>TakeAttribute*</c> / <c>PreloadAttribute*</c>
/// handlers in <c>archive\ver2\file\item_supp.cpp</c>: ItemCommon (0x10, contains the
/// filename), SourceItem (0x40, contains the source path), HardLinkId (0x14, 8 bytes),
/// BackupTime (0x50, 8 bytes), TimeZone (0x60, 4 bytes).
/// </para>
/// <para>
/// These tests pin the wire layout with hex-literal vectors (so future regressions are caught
/// at the byte level) and exercise the high-level end-to-end path: build a synthetic .tib with
/// a FileMeta102 carrying ItemCommon, decode the slice through <see cref="AcronisReader"/>,
/// verify the Listing record's <c>Name</c> matches the chain-walked decoded name.
/// </para>
/// </remarks>
[TestFixture]
public class AcronisFileMetaBodyTests {

  // ===== Building blocks (encoder for fixture bytes) =====

  /// <summary>
  /// Builds an attribute-stream payload (<c>uint32 count</c> + N attribute tuples) for use as
  /// an inflated FileMeta record body. Each attribute is <c>(uint32 idAndFlags, uint16 size,
  /// byte[size] body)</c>.
  /// </summary>
  private static byte[] BuildAttributeStream(IReadOnlyList<(uint idAndFlags, byte[] body)> attrs) {
    using var ms = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)attrs.Count);
    ms.Write(u32);
    foreach (var (id, body) in attrs) {
      BinaryPrimitives.WriteUInt32LittleEndian(u32, id);
      ms.Write(u32);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)body.Length);
      ms.Write(u16);
      ms.Write(body);
    }
    return ms.ToArray();
  }

  /// <summary>Builds an ItemCommon (0x10) attribute body from a (name, altName) pair.</summary>
  private static byte[] BuildItemCommonBody(string name, string altName = "") {
    using var ms = new MemoryStream();
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)name.Length); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)altName.Length); ms.Write(u16);
    // Pad the 44-byte fixed header (4 bytes already written for name/alt lengths → 40 to go).
    ms.Write(new byte[40]);
    if (name.Length > 0) ms.Write(Encoding.Unicode.GetBytes(name));
    if (altName.Length > 0) ms.Write(Encoding.Unicode.GetBytes(altName));
    return ms.ToArray();
  }

  /// <summary>
  /// Builds an ItemCommon (0x10) attribute body with explicit values for the typed fields decoded
  /// from the 44-byte fixed header (DosAttributes + 4 FILETIMEs + trailer dword).
  /// </summary>
  private static byte[] BuildItemCommonBodyFull(
    string name,
    string altName,
    uint dosAttrs,
    ulong creationTime,
    ulong lastWriteTime,
    ulong lastAccessTime,
    ulong changeTime,
    uint trailer
  ) {
    using var ms = new MemoryStream();
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    Span<byte> u64 = stackalloc byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)name.Length); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)altName.Length); ms.Write(u16);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, dosAttrs); ms.Write(u32);
    BinaryPrimitives.WriteUInt64LittleEndian(u64, creationTime); ms.Write(u64);
    BinaryPrimitives.WriteUInt64LittleEndian(u64, lastWriteTime); ms.Write(u64);
    BinaryPrimitives.WriteUInt64LittleEndian(u64, lastAccessTime); ms.Write(u64);
    BinaryPrimitives.WriteUInt64LittleEndian(u64, changeTime); ms.Write(u64);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, trailer); ms.Write(u32);
    if (name.Length > 0) ms.Write(Encoding.Unicode.GetBytes(name));
    if (altName.Length > 0) ms.Write(Encoding.Unicode.GetBytes(altName));
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a Replica (0x17) attribute body: 16-byte GUID block (on-disk order, byte-swapped
  /// from .NET canonical) + 2× uint32.
  /// </summary>
  private static byte[] BuildReplicaBody(Guid guid, uint value1, uint value2) {
    using var ms = new MemoryStream();
    // .NET Guid → wire order: Data1 (LE u32) → BE bytes, Data2/Data3 (LE u16) → BE bytes,
    // Data4 verbatim. ToByteArray gives canonical LE form; swap the first 8 bytes.
    var canonical = guid.ToByteArray();
    Span<byte> wire = stackalloc byte[16];
    wire[0] = canonical[3]; wire[1] = canonical[2]; wire[2] = canonical[1]; wire[3] = canonical[0];
    wire[4] = canonical[5]; wire[5] = canonical[4];
    wire[6] = canonical[7]; wire[7] = canonical[6];
    canonical.AsSpan(8, 8).CopyTo(wire[8..]);
    ms.Write(wire);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, value1); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, value2); ms.Write(u32);
    return ms.ToArray();
  }

  /// <summary>Builds an ItemCommonExtra (0x18) attribute body (8 bytes, uint64 little-endian).</summary>
  private static byte[] BuildItemCommonExtraBody(ulong value) {
    var bytes = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    return bytes;
  }

  /// <summary>
  /// Builds a SliceItem (0x80) attribute body: 25-byte short form (GUID + 2× uint32 + 1 byte
  /// flag). When <paramref name="name"/> is non-null the extended 25 + 3 + name*2 layout is
  /// emitted with a sentinel pad byte before the name length.
  /// </summary>
  private static byte[] BuildSliceItemBody(Guid guid, uint value1, uint value2, byte flag, string? name = null) {
    using var ms = new MemoryStream();
    var canonical = guid.ToByteArray();
    Span<byte> wire = stackalloc byte[16];
    wire[0] = canonical[3]; wire[1] = canonical[2]; wire[2] = canonical[1]; wire[3] = canonical[0];
    wire[4] = canonical[5]; wire[5] = canonical[4];
    wire[6] = canonical[7]; wire[7] = canonical[6];
    canonical.AsSpan(8, 8).CopyTo(wire[8..]);
    ms.Write(wire);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, value1); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, value2); ms.Write(u32);
    ms.WriteByte(flag);
    if (name is not null) {
      ms.WriteByte(0); // sentinel pad
      Span<byte> u16 = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)name.Length); ms.Write(u16);
      if (name.Length > 0) ms.Write(Encoding.Unicode.GetBytes(name));
    }
    return ms.ToArray();
  }

  /// <summary>Builds a SourceItem (0x40) attribute body from (path, kind, id).</summary>
  private static byte[] BuildSourceItemBody(string path, ushort kind, uint id) {
    using var ms = new MemoryStream();
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)path.Length); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, kind); ms.Write(u16);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, id); ms.Write(u32);
    if (path.Length > 0) ms.Write(Encoding.Unicode.GetBytes(path));
    return ms.ToArray();
  }

  // ===== Raw-decoder unit tests =====

  [Test, Category("HappyPath")]
  public void Decode_EmptyAttributeStream_ReturnsZeroCountBody() {
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
    var body = AcronisFileMetaBodyDecoder.Decode(u32.ToArray());
    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(0u));
      Assert.That(body.Attributes, Is.Empty);
      Assert.That(body.ItemCommon, Is.Null);
    });
  }

  [Test, Category("EdgeCase")]
  public void Decode_TooShortForCount_ReturnsNull() {
    Assert.That(AcronisFileMetaBodyDecoder.Decode(new byte[3]), Is.Null);
  }

  [Test, Category("HappyPath")]
  public void Decode_SingleItemCommonAttribute_RoundTripsNameAndLengths() {
    // Hex-literal fixture: count=1; id=0x00000010; size=44 (header) + 2*9 (name) + 2*4 (alt);
    // name="filename1"; alt="ALT1".
    const string name = "filename1";
    const string alt = "ALT1";
    var payload = BuildAttributeStream([(0x10u, BuildItemCommonBody(name, alt))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(1u));
      Assert.That(body.Attributes, Has.Count.EqualTo(1));
      Assert.That(body.Attributes[0].Id, Is.EqualTo(0x10u));
      Assert.That(body.Attributes[0].Header.IsDeduplicated, Is.False);
      Assert.That(body.ItemCommon, Is.Not.Null);
      Assert.That(body.ItemCommon!.Name, Is.EqualTo(name));
      Assert.That(body.ItemCommon.AltName, Is.EqualTo(alt));
      Assert.That(body.ItemCommon.NameLength, Is.EqualTo((ushort)name.Length));
      Assert.That(body.ItemCommon.AltNameLength, Is.EqualTo((ushort)alt.Length));
      Assert.That(body.ItemCommon.FixedHeader, Has.Length.EqualTo(44));
    });
  }

  [Test, Category("HappyPath")]
  public void Decode_ItemCommon_WithoutAltName_LeavesAltNull() {
    var payload = BuildAttributeStream([(0x10u, BuildItemCommonBody("alpha.txt"))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.ItemCommon, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body.ItemCommon!.Name, Is.EqualTo("alpha.txt"));
      Assert.That(body.ItemCommon.AltName, Is.Null.Or.Empty);
      Assert.That(body.ItemCommon.AltNameLength, Is.EqualTo((ushort)0));
    });
  }

  [Test, Category("HappyPath")]
  public void Decode_SourceItemAttribute_RoundTripsPathKindAndId() {
    const string path = @"C:\Users\Test";
    const ushort kind = 0x0007;
    const uint id = 0xDEADBEEF;
    var payload = BuildAttributeStream([(0x40u, BuildSourceItemBody(path, kind, id))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.SourceItem, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body.SourceItem!.Path, Is.EqualTo(path));
      Assert.That(body.SourceItem.PathLength, Is.EqualTo((ushort)path.Length));
      Assert.That(body.SourceItem.Kind, Is.EqualTo(kind));
      Assert.That(body.SourceItem.Id, Is.EqualTo(id));
    });
  }

  [Test, Category("HappyPath")]
  public void Decode_HardLinkIdAttribute_RoundTripsUint64() {
    var body8 = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(body8, 0x1122334455667788UL);
    var payload = BuildAttributeStream([(0x14u, body8)]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.HardLinkId, Is.EqualTo(0x1122334455667788UL));
  }

  [Test, Category("HappyPath")]
  public void Decode_BackupTimeAttribute_RoundTripsUint64() {
    var body8 = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(body8, 0x01D9B79AAB000000UL);
    var payload = BuildAttributeStream([(0x50u, body8)]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.BackupTime, Is.EqualTo(0x01D9B79AAB000000UL));
  }

  [Test, Category("HappyPath")]
  public void Decode_TimeZoneAttribute_RoundTripsInt32Minutes() {
    var body4 = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(body4, -300);  // UTC-5
    var payload = BuildAttributeStream([(0x60u, body4)]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.TimeZoneMinutes, Is.EqualTo(-300));
  }

  [Test, Category("HappyPath")]
  public void Decode_MultipleAttributes_AllRoundTrip() {
    var hl = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(hl, 0x42UL);
    var bt = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bt, 0xCAFEBABEUL);
    var tz = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(tz, 60);
    var payload = BuildAttributeStream([
      (0x10u, BuildItemCommonBody("multi.bin")),
      (0x14u, hl),
      (0x50u, bt),
      (0x60u, tz),
    ]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(4u));
      Assert.That(body.Attributes, Has.Count.EqualTo(4));
      Assert.That(body.ItemCommon?.Name, Is.EqualTo("multi.bin"));
      Assert.That(body.HardLinkId, Is.EqualTo(0x42UL));
      Assert.That(body.BackupTime, Is.EqualTo(0xCAFEBABEUL));
      Assert.That(body.TimeZoneMinutes, Is.EqualTo(60));
    });
  }

  // ===== Dedup-flag handling =====

  [Test, Category("EdgeCase")]
  public void Decode_DeduplicatedAttribute_MasksFlagFromIdAndSkipsInlineDecode() {
    // Flag bit 23 is set in idAndFlags; size = 16 (an MD5 hash referring to a side table we
    // don't dereference). Decoder must (a) report Id = 0x10 (unmasked), (b) NOT mistake the
    // 16-byte hash for an inline ItemCommon body.
    var md5 = new byte[16];
    for (var i = 0; i < 16; i++) md5[i] = (byte)i;
    var payload = BuildAttributeStream([(0x10u | 0x00800000u, md5)]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.Attributes, Has.Count.EqualTo(1));
      Assert.That(body.Attributes[0].Id, Is.EqualTo(0x10u), "id must be unmasked");
      Assert.That(body.Attributes[0].Header.IsDeduplicated, Is.True);
      Assert.That(body.ItemCommon, Is.Null, "dedup bodies must NOT be decoded as inline ItemCommon");
    });
  }

  // ===== Hex-literal byte-vector fixture (regression-proof) =====

  [Test, Category("HappyPath")]
  public void Decode_HexLiteralFixture_ItemCommonWithName_DecodesAsExpected() {
    // Hand-rolled bytes — fixture freezes the wire layout against future regressions.
    //
    // Layout:
    //   uint32 count = 1
    //   uint32 idAndFlags = 0x00000010
    //   uint16 size = 44 + 2*4 = 52 (the ItemCommon body length)
    //   --- body (52 bytes) ---
    //   uint16 nameLength = 4
    //   uint16 altLength  = 0
    //   byte[40] fixed-header padding (all zero)
    //   "name" in UTF-16LE (8 bytes)
    var fixture = new byte[] {
      0x01, 0x00, 0x00, 0x00, // count = 1
      0x10, 0x00, 0x00, 0x00, // idAndFlags = 0x10
      0x34, 0x00,             // size = 52
      0x04, 0x00,             // nameLength = 4
      0x00, 0x00,             // altLength = 0
      // 40 bytes of fixed-header padding:
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      // "name" UTF-16LE: 'n','a','m','e'
      0x6E, 0x00, 0x61, 0x00, 0x6D, 0x00, 0x65, 0x00,
    };

    var body = AcronisFileMetaBodyDecoder.Decode(fixture);

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(1u));
      Assert.That(body.ItemCommon?.Name, Is.EqualTo("name"));
      Assert.That(body.ItemCommon?.AltNameLength, Is.EqualTo((ushort)0));
    });
  }

  // ===== Truncation / robustness =====

  [Test, Category("ErrorHandling")]
  public void Decode_TruncatedAttributeHeader_ReturnsPartialResult() {
    // count says 2 but only one full attribute + 3 bytes of the next header are present.
    Span<byte> u32 = stackalloc byte[4];
    using var ms = new MemoryStream();
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 2); ms.Write(u32);
    // First attribute: complete (id 0x50, 8 bytes, body all zero)
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0x50u); ms.Write(u32);
    ms.WriteByte(0x08); ms.WriteByte(0x00);
    ms.Write(new byte[8]);
    // Second attribute header: only 3 bytes — truncated.
    ms.Write(new byte[] { 0xAB, 0xCD, 0xEF });

    var body = AcronisFileMetaBodyDecoder.Decode(ms.ToArray());

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(2u), "declared count is surfaced as-is");
      Assert.That(body.Attributes, Has.Count.EqualTo(1), "only the complete attribute is returned");
      Assert.That(body.BackupTime, Is.EqualTo(0UL));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Decode_TruncatedAttributeBody_StopsAtFirstShortBody() {
    Span<byte> u32 = stackalloc byte[4];
    using var ms = new MemoryStream();
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 1); ms.Write(u32);
    // Attribute header says size=20 but only 5 body bytes follow.
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0x12345678u); ms.Write(u32);
    ms.WriteByte(0x14); ms.WriteByte(0x00); // size = 20
    ms.Write(new byte[] { 1, 2, 3, 4, 5 });

    var body = AcronisFileMetaBodyDecoder.Decode(ms.ToArray());

    Assert.That(body, Is.Not.Null);
    Assert.That(body!.Attributes, Is.Empty,
      "truncated body must NOT be partially appended — decoder stops at first short body");
  }

  // ===== ItemCommon-specific defensive decode =====

  [Test, Category("ErrorHandling")]
  public void DecodeItemCommon_BodyShorterThan44Bytes_ReturnsNull() {
    var tooShort = new byte[40];
    Assert.That(AcronisFileMetaBodyDecoder.DecodeItemCommon(tooShort), Is.Null);
  }

  [Test, Category("ErrorHandling")]
  public void DecodeItemCommon_NameLengthOverflowsBody_ReturnsNull() {
    var body = new byte[44 + 4]; // claim nameLength = 10 chars → 20 bytes; have 4 → out of range
    BinaryPrimitives.WriteUInt16LittleEndian(body, 10);
    Assert.That(AcronisFileMetaBodyDecoder.DecodeItemCommon(body), Is.Null);
  }

  // ===== End-to-end: reader surfaces DecodedNamesByEntry from the chained 102 body =====

  /// <summary>
  /// Builds a single-file .tib slice where the FirstFileMetaRecord(102) body carries an
  /// ItemCommon attribute (id 0x10) with the supplied <paramref name="decodedName"/>. The
  /// Listing record records a DIFFERENT name (<paramref name="listingName"/>) so we can pin
  /// which name the reader surfaces from which record.
  /// </summary>
  private static byte[] BuildTibWithFileMeta102Carrying(
      string listingName, string decodedName, byte[] content) {
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);

    var metaStart = (long)ms.Position;
    var ffmOffset = ms.Position - HeaderLength;

    // 102 with a real attribute stream carrying ItemCommon.
    var metaPayload = BuildAttributeStream([(0x10u, BuildItemCommonBody(decodedName))]);
    WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, metaPayload);
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, [0, 0, 0, 0]);
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, [0, 0, 0, 0]);
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, [0, 0, 0, 0]);

    var blobAbs = ms.Position;
    WriteZlibRecord(ms, AcronisRecordType.Blob, content);
    var idxPayload = BuildRecordIndexPayload(content.LongLength,
      [(0L, blobAbs - HeaderLength, MD5.HashData(content))]);
    WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idxPayload);

    // Listing with the OTHER name — used as the proxy when DecodedNamesByEntry is null.
    var listingPayload = BuildListingPayload([(listingName, content.LongLength, ffmOffset)]);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Reader_SurfacesDecodedName_FromAnchored102Body() {
    var content = Encoding.UTF8.GetBytes("hello acronis");
    var tib = BuildTibWithFileMeta102Carrying("listing_name.txt", "real_filename.bin", content);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.ChainWalkComplete, Is.True);
      Assert.That(r.DecodedNamesByEntry, Has.Count.EqualTo(1));
      Assert.That(r.DecodedNamesByEntry[0], Is.EqualTo("real_filename.bin"),
        "DecodedNamesByEntry must reflect the 102-body ItemCommon name");
      Assert.That(r.Entries[0].Name, Is.EqualTo("listing_name.txt"),
        "Listing-record name stays untouched — both views are surfaced");
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_DecodedName_MatchesListingName_WhenBothCarrySameName() {
    var content = Encoding.UTF8.GetBytes("agreement case");
    var tib = BuildTibWithFileMeta102Carrying("same.txt", "same.txt", content);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.Entries[0].Name, Is.EqualTo("same.txt"));
      Assert.That(r.DecodedNamesByEntry[0], Is.EqualTo("same.txt"));
      Assert.That(r.DecodedNamesByEntry[0], Is.EqualTo(r.Entries[0].Name),
        "decoded name and Listing name agree in the common case");
    });
  }

  [Test, Category("EdgeCase")]
  public void Reader_DecodedNameNull_WhenChainWalkUnresolved() {
    // Legacy builder writes MetaOffset=0 with no anchor 102 at that offset.
    var content = Encoding.UTF8.GetBytes("legacy");
    var tib = BuildLegacyMetaOffsetZero("x.txt", content);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.ChainWalkComplete, Is.False, "MetaOffset=0 doesn't anchor a 102");
      Assert.That(r.DecodedNamesByEntry[0], Is.Null,
        "no chain walk anchor → no decoded name");
    });
  }

  [Test, Category("EdgeCase")]
  public void Reader_FileMetaBodyDecodingTolerant_OfNonAttributePayload() {
    // Builder writes raw ASCII text into the 102 body (the legacy AcronisExtractionTests
    // fixture). Decoder must NOT throw — it should surface either null or a partial body that
    // doesn't claim to have an ItemCommon.
    var content = Encoding.UTF8.GetBytes("ascii body");
    var tib = BuildLegacyAsciiMeta102("x.txt", content);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.That(r.Records, Is.Not.Empty);
    // The 102 record exists; the body either parses partially (no ItemCommon) or is null.
    Assert.That(r.DecodedNamesByEntry, Is.Not.Null);
  }

  // ===== Helpers (copy of the synthetic builder bits) =====

  private static void WriteRawDeflateRecord(MemoryStream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    Span<byte> sum = stackalloc byte[4];
    ms.Write(sum);
  }

  private static void WriteZlibRecord(MemoryStream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, ComputeAdler32(payload));
    ms.Write(trailer);
  }

  private static uint ComputeAdler32(byte[] data) {
    const uint MOD = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % MOD; b = (b + a) % MOD; }
    return (b << 16) | a;
  }

  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]);
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    foreach (var h in handles) {
      WriteUInt48Bytes(ms, (ulong)h.startOffset); ms.WriteByte(0); ms.WriteByte(0);
      WriteUInt48Bytes(ms, (ulong)h.recordOffset); ms.WriteByte(0); ms.WriteByte(0);
      ms.Write(h.md5);
    }
    return ms.ToArray();
  }

  private static byte[] BuildListingPayload(IReadOnlyList<(string Name, long Size, long MetaOffset)> entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)entries.Count);
    foreach (var (name, size, mo) in entries) {
      WriteCountedUtf16(w, "");
      w.Write(0u);
      WriteCountedUtf16(w, name);
      WriteCountedUtf16(w, "");
      WriteUInt48(w, 0); w.Write((ushort)0);
      w.Write(0u);
      WriteUInt48(w, (ulong)size); w.Write((ushort)0);
      WriteUInt48(w, (ulong)size); w.Write((ushort)0);
      WriteUInt48(w, (ulong)mo); w.Write((ushort)0);
      w.Write(new byte[38]);
    }
    w.Flush();
    return ms.ToArray();
  }

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  private static void WriteUInt48Bytes(MemoryStream s, ulong v) {
    for (var i = 0; i < 6; i++) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
  }

  private static byte[] BuildLegacyMetaOffsetZero(string name, byte[] content) {
    // Listing first (MetaOffset=0), no 102 at offset 0 → chain walk incomplete.
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);
    var metaStart = (long)ms.Position;

    var listingPayload = BuildListingPayload([(name, content.LongLength, 0L)]);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);

    WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord,
      BuildAttributeStream([(0x10u, BuildItemCommonBody("anchored.bin"))]));
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, [0, 0, 0, 0]);
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, [0, 0, 0, 0]);
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, [0, 0, 0, 0]);

    var blobAbs = ms.Position;
    WriteZlibRecord(ms, AcronisRecordType.Blob, content);
    var idx = BuildRecordIndexPayload(content.LongLength,
      [(0L, blobAbs - HeaderLength, MD5.HashData(content))]);
    WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idx);
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }

  // ===== ItemCommon 44-byte fixed header: typed-field decode =====

  [Test, Category("HappyPath")]
  public void DecodeItemCommon_TypedFields_RoundTripDosAttributesAndFourFiletimes() {
    // Synthetic values pinned at the wire layout reverse-engineered from
    // ArchiveApi::ItemBackuperImpl::BackupCommonAttributes: offset 4 = DosAttributes,
    // offset 8/16/24/32 = 4× FILETIME, offset 40 = trailer dword.
    const uint dosAttrs = 0x00000020u; // FILE_ATTRIBUTE_ARCHIVE
    const ulong creation = 0x01D9B79AAB000000UL;   // ~2023-04-09 UTC
    const ulong lastWrite = 0x01DA0102_03040506UL;
    const ulong lastAccess = 0x01DB1112_13141516UL;
    const ulong change = 0x01DC2122_23242526UL;
    const uint trailer = 0xDEADBEEFu;
    var body = BuildItemCommonBodyFull(
      "file.bin", "FILE~1.BIN",
      dosAttrs, creation, lastWrite, lastAccess, change, trailer);

    var ic = AcronisFileMetaBodyDecoder.DecodeItemCommon(body);

    Assert.That(ic, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(ic!.Name, Is.EqualTo("file.bin"));
      Assert.That(ic.AltName, Is.EqualTo("FILE~1.BIN"));
      Assert.That(ic.DosAttributes, Is.EqualTo(dosAttrs));
      Assert.That(ic.CreationTime, Is.EqualTo(creation));
      Assert.That(ic.LastWriteTime, Is.EqualTo(lastWrite));
      Assert.That(ic.LastAccessTime, Is.EqualTo(lastAccess));
      Assert.That(ic.ChangeTime, Is.EqualTo(change));
      Assert.That(ic.TrailerDword, Is.EqualTo(trailer));
      Assert.That(ic.FixedHeader, Has.Length.EqualTo(44));
    });
  }

  [Test, Category("HappyPath")]
  public void DecodeItemCommon_CreationTimeUtc_ConvertsFromFiletime() {
    // 2024-01-15 12:30:00 UTC → FILETIME ticks
    var dt = new DateTime(2024, 1, 15, 12, 30, 0, DateTimeKind.Utc);
    var filetime = (ulong)dt.ToFileTimeUtc();
    var body = BuildItemCommonBodyFull("a", "", 0u, filetime, 0UL, 0UL, 0UL, 0u);

    var ic = AcronisFileMetaBodyDecoder.DecodeItemCommon(body);

    Assert.That(ic!.CreationTimeUtc, Is.EqualTo(dt));
  }

  [Test, Category("EdgeCase")]
  public void DecodeItemCommon_AllTimestampsZero_DateTimePropertiesAreNull() {
    var body = BuildItemCommonBodyFull("z.txt", "", 0u, 0UL, 0UL, 0UL, 0UL, 0u);

    var ic = AcronisFileMetaBodyDecoder.DecodeItemCommon(body);

    Assert.Multiple(() => {
      Assert.That(ic!.CreationTimeUtc, Is.Null, "FILETIME 0 → not yet set → null");
      Assert.That(ic.LastWriteTimeUtc, Is.Null);
      Assert.That(ic.LastAccessTimeUtc, Is.Null);
      Assert.That(ic.ChangeTimeUtc, Is.Null);
    });
  }

  [Test, Category("EdgeCase")]
  public void DecodeItemCommon_OutOfRangeFiletime_GracefullyReturnsNullDateTime() {
    // ulong.MaxValue is well past DateTime.MaxValue → FromFileTimeUtc throws,
    // decoder must swallow.
    var body = BuildItemCommonBodyFull("x", "", 0u, ulong.MaxValue, 0UL, 0UL, 0UL, 0u);
    var ic = AcronisFileMetaBodyDecoder.DecodeItemCommon(body);
    Assert.That(ic!.CreationTimeUtc, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void DecodeItemCommon_HexLiteralFixture_PinsFixedHeaderLayout() {
    // Hand-rolled hex fixture pinning the exact byte offsets reverse-engineered from the
    // BackupCommonAttributes writer. Any future regression that moves a field gets caught here.
    var fixture = new byte[] {
      0x03, 0x00,                                                 // nameLength = 3
      0x00, 0x00,                                                 // altNameLength = 0
      0x20, 0x00, 0x00, 0x00,                                     // DosAttributes = 0x20 (ARCHIVE)
      0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,             // CreationTime
      0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00,             // LastWriteTime
      0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80,             // LastAccessTime
      0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,             // ChangeTime
      0xEF, 0xBE, 0xAD, 0xDE,                                     // Trailer dword
      0x61, 0x00, 0x62, 0x00, 0x63, 0x00,                         // "abc" UTF-16LE
    };

    var ic = AcronisFileMetaBodyDecoder.DecodeItemCommon(fixture);

    Assert.That(ic, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(ic!.Name, Is.EqualTo("abc"));
      Assert.That(ic.DosAttributes, Is.EqualTo(0x20u));
      Assert.That(ic.CreationTime, Is.EqualTo(0x8877665544332211UL));
      Assert.That(ic.LastWriteTime, Is.EqualTo(0x00FFEEDDCCBBAA99UL));
      Assert.That(ic.LastAccessTime, Is.EqualTo(0x8070605040302010UL));
      Assert.That(ic.ChangeTime, Is.EqualTo(0x0807060504030201UL));
      Assert.That(ic.TrailerDword, Is.EqualTo(0xDEADBEEFu));
    });
  }

  // ===== Replica (0x17) decode =====

  [Test, Category("HappyPath")]
  public void DecodeReplica_RoundTripsGuidAndCookies() {
    var guid = new Guid("11223344-5566-7788-99AA-BBCCDDEEFF00");
    var body = BuildReplicaBody(guid, 0xCAFEBABEu, 0xDEADBEEFu);

    var replica = AcronisFileMetaBodyDecoder.DecodeReplica(body);

    Assert.That(replica, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(replica!.Guid, Is.EqualTo(guid));
      Assert.That(replica.Value1, Is.EqualTo(0xCAFEBABEu));
      Assert.That(replica.Value2, Is.EqualTo(0xDEADBEEFu));
      Assert.That(replica.RawGuidBytes, Has.Length.EqualTo(16));
    });
  }

  [Test, Category("ErrorHandling")]
  public void DecodeReplica_TooShortBody_ReturnsNull() {
    Assert.That(AcronisFileMetaBodyDecoder.DecodeReplica(new byte[23]), Is.Null);
  }

  [Test, Category("HappyPath")]
  public void Decode_ReplicaAttribute_SurfacesInTopLevelView() {
    var guid = new Guid("AABBCCDD-EEFF-0011-2233-445566778899");
    var payload = BuildAttributeStream([(0x17u, BuildReplicaBody(guid, 1, 2))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.Replica, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body.Replica!.Guid, Is.EqualTo(guid));
      Assert.That(body.Replica.Value1, Is.EqualTo(1u));
      Assert.That(body.Replica.Value2, Is.EqualTo(2u));
    });
  }

  // ===== ItemCommonExtra (0x18) decode =====

  [Test, Category("HappyPath")]
  public void Decode_ItemCommonExtraAttribute_RoundTripsUint64() {
    const ulong cookie = 0x0102030405060708UL;
    var payload = BuildAttributeStream([(0x18u, BuildItemCommonExtraBody(cookie))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.Multiple(() => {
      Assert.That(body!.ItemCommonExtra, Is.Not.Null);
      Assert.That(body.ItemCommonExtra!.Value, Is.EqualTo(cookie));
    });
  }

  [Test, Category("EdgeCase")]
  public void Decode_ItemCommonExtraAttribute_TooShortBody_IsIgnored() {
    var payload = BuildAttributeStream([(0x18u, new byte[7])]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.ItemCommonExtra, Is.Null,
      "decoder must NOT surface a partial 7-byte body — binary's reader throws on size != 8");
  }

  // ===== SliceItem (0x80) decode =====

  [Test, Category("HappyPath")]
  public void DecodeSliceItem_ShortForm_RoundTripsGuidCookiesAndFlag() {
    var guid = new Guid("12345678-9ABC-DEF0-1122-334455667788");
    var body = BuildSliceItemBody(guid, 0x10000000u, 0x20000000u, flag: 1);

    var slice = AcronisFileMetaBodyDecoder.DecodeSliceItem(body);

    Assert.That(slice, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(slice!.Guid, Is.EqualTo(guid));
      Assert.That(slice.Value1, Is.EqualTo(0x10000000u));
      Assert.That(slice.Value2, Is.EqualTo(0x20000000u));
      Assert.That(slice.Flag, Is.EqualTo((byte)1));
      Assert.That(slice.Name, Is.Null, "short form carries no name");
      Assert.That(slice.NameLength, Is.EqualTo((ushort)0));
    });
  }

  [Test, Category("HappyPath")]
  public void DecodeSliceItem_ExtendedForm_SurfacesTrailingName() {
    var guid = new Guid("DEADBEEF-1234-5678-9ABC-DEF012345678");
    const string sliceName = "slice-2025-01";
    var body = BuildSliceItemBody(guid, 0xAAu, 0xBBu, 0, sliceName);

    var slice = AcronisFileMetaBodyDecoder.DecodeSliceItem(body);

    Assert.That(slice, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(slice!.Guid, Is.EqualTo(guid));
      Assert.That(slice.Name, Is.EqualTo(sliceName));
      Assert.That(slice.NameLength, Is.EqualTo((ushort)sliceName.Length));
    });
  }

  [Test, Category("ErrorHandling")]
  public void DecodeSliceItem_TooShortBody_ReturnsNull() {
    Assert.That(AcronisFileMetaBodyDecoder.DecodeSliceItem(new byte[24]), Is.Null);
  }

  [Test, Category("HappyPath")]
  public void Decode_SliceItemAttribute_SurfacesInTopLevelView() {
    var guid = new Guid("FFEEDDCC-BBAA-9988-7766-554433221100");
    var payload = BuildAttributeStream([(0x80u, BuildSliceItemBody(guid, 7, 8, flag: 1))]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.SliceItem, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body.SliceItem!.Guid, Is.EqualTo(guid));
      Assert.That(body.SliceItem.Flag, Is.EqualTo((byte)1));
    });
  }

  // ===== SliceItemBlob (0x90) decode =====

  [Test, Category("HappyPath")]
  public void Decode_SliceItemBlobAttribute_SurfacesVerbatimBytes() {
    var blob = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04 };
    var payload = BuildAttributeStream([(0x90u, blob)]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.SliceItemBlob, Is.Not.Null);
    Assert.That(body.SliceItemBlob!.Bytes, Is.EqualTo(blob));
  }

  [Test, Category("EdgeCase")]
  public void Decode_SliceItemBlobAttribute_EmptyBlob_IsSurfaced() {
    var payload = BuildAttributeStream([(0x90u, [])]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body!.SliceItemBlob, Is.Not.Null);
    Assert.That(body.SliceItemBlob!.Bytes, Is.Empty);
  }

  // ===== Multi-attribute body carrying every newly-decoded id =====

  [Test, Category("HappyPath")]
  public void Decode_AllNewlyDecodedAttributes_SurfacedTogether() {
    var replicaGuid = new Guid("11111111-2222-3333-4444-555555555555");
    var sliceGuid = new Guid("66666666-7777-8888-9999-AAAAAAAAAAAA");
    var blob = new byte[] { 0xFF, 0xEE, 0xDD, 0xCC };
    var payload = BuildAttributeStream([
      (0x10u, BuildItemCommonBodyFull("multi.txt", "", 0x21u, 0x1000UL, 0x2000UL, 0x3000UL, 0x4000UL, 0xABCDu)),
      (0x17u, BuildReplicaBody(replicaGuid, 1, 2)),
      (0x18u, BuildItemCommonExtraBody(0xFEEDFACECAFEBEEFUL)),
      (0x80u, BuildSliceItemBody(sliceGuid, 3, 4, flag: 1)),
      (0x90u, blob),
    ]);

    var body = AcronisFileMetaBodyDecoder.Decode(payload);

    Assert.That(body, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(body!.AttributeCount, Is.EqualTo(5u));
      Assert.That(body.ItemCommon?.DosAttributes, Is.EqualTo(0x21u));
      Assert.That(body.ItemCommon?.CreationTime, Is.EqualTo(0x1000UL));
      Assert.That(body.Replica?.Guid, Is.EqualTo(replicaGuid));
      Assert.That(body.ItemCommonExtra?.Value, Is.EqualTo(0xFEEDFACECAFEBEEFUL));
      Assert.That(body.SliceItem?.Guid, Is.EqualTo(sliceGuid));
      Assert.That(body.SliceItemBlob?.Bytes, Is.EqualTo(blob));
    });
  }

  // ===== Dedup flag still masks per-id decode for the new ids =====

  [Test, Category("EdgeCase")]
  public void Decode_DeduplicatedSliceItem_NotDecodedAsInline() {
    var md5 = new byte[16];
    var payload = BuildAttributeStream([(0x80u | 0x00800000u, md5)]);
    var body = AcronisFileMetaBodyDecoder.Decode(payload);
    Assert.Multiple(() => {
      Assert.That(body!.Attributes[0].Header.IsDeduplicated, Is.True);
      Assert.That(body.SliceItem, Is.Null, "dedup hash must NOT be decoded as inline SliceItem");
    });
  }

  private static byte[] BuildLegacyAsciiMeta102(string name, byte[] content) {
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);
    var metaStart = (long)ms.Position;
    var ffmOffset = ms.Position - HeaderLength;

    WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord,
      Encoding.ASCII.GetBytes("meta102:legacy ascii payload that's not an attribute stream"));
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes("meta1:legacy"));
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes("meta2:legacy"));
    WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes("meta5:legacy"));

    var blobAbs = ms.Position;
    WriteZlibRecord(ms, AcronisRecordType.Blob, content);
    var idx = BuildRecordIndexPayload(content.LongLength,
      [(0L, blobAbs - HeaderLength, MD5.HashData(content))]);
    WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idx);

    var listing = BuildListingPayload([(name, content.LongLength, ffmOffset)]);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listing);
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }
}
