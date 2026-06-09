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
