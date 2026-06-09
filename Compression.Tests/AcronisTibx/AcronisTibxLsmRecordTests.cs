using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using FileFormat.Acronis;
using FileFormat.AcronisTibx;

namespace Compression.Tests.AcronisTibx;

/// <summary>
///   Stage-3 acceptance gate for the LSM record-stream decoder added on top of
///   <see cref="AcronisTibxReader"/> — pins the LZ4-chained-stream body shape recovered from
///   <c>libarchive3.so</c> <c>0x54fb0</c> and the forensic ItemCommon-attribute scanner that
///   bridges to <see cref="AcronisFileMetaBodyDecoder"/> from <c>FileFormat.Acronis</c>.
/// </summary>
/// <remarks>
///   <para>
///     Tests use synthetic fixtures only — there is no real <c>.tibx</c> archive in the test
///     resources. The fixtures pin (a) the chunked-LZ4 body framing
///     <c>(BE32 zchunk, BE32 chunk, LZ4-block)</c> at page <c>+0x20</c>, (b) the ItemCommon
///     attribute layout the scanner recognises (44-byte fixed header + UTF-16 name +
///     FILETIME sanity check), and (c) the Golomb-Rice <c>k=8</c> codec used by GOLOMB-page
///     membership filters per <c>golomb_decode_mod256</c> / <c>golomb_encode_mod256</c>.
///   </para>
/// </remarks>
[TestFixture]
public class AcronisTibxLsmRecordTests {

  private const int PageSize = AcronisTibxPage.PageSize;
  private const int HeaderPageSize = AcronisTibxReader.HeaderPageSize;
  private const int LeafBodyOffset = AcronisTibxLsmRecord.LeafBodyOffset;

  // ─── Golomb-Rice k=8 codec ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Golomb_RoundTrip_SmallValues() {
    var values = new ulong[] { 0, 1, 5, 42, 100, 255, 256, 511, 1024, 2047 };
    var packed = Golomb.EncodeSequenceMod256(values);
    var decoded = Golomb.DecodeSequenceMod256(packed, values.Length);
    Assert.That(decoded, Is.EqualTo(values),
      "Round-trip must preserve every value in the small-value regime (quotient < 8).");
  }

  [Test, Category("HappyPath")]
  public void Golomb_RoundTrip_Zero() {
    var packed = Golomb.EncodeSequenceMod256([0UL]);
    var decoded = Golomb.DecodeSequenceMod256(packed, 1);
    Assert.That(decoded[0], Is.EqualTo(0UL),
      "Zero encodes as a single 0-bit + 8 zero remainder bits; decoder must round-trip.");
  }

  [Test, Category("HappyPath")]
  public void Golomb_RoundTrip_AtQuotientBoundary() {
    // q=7 uses the cheap form (7 1-bits + 0 + 8 remainder bits). q=8 hits the escape path
    // (8 1-bit sentinel + 64-bit raw value).
    var values = new ulong[] { 7 * 256UL + 17, 8 * 256UL + 33, 100UL * 256UL };
    var packed = Golomb.EncodeSequenceMod256(values);
    var decoded = Golomb.DecodeSequenceMod256(packed, values.Length);
    Assert.That(decoded, Is.EqualTo(values),
      "Boundary at quotient=8 must round-trip cleanly across the escape path.");
  }

  [Test, Category("HappyPath")]
  public void Golomb_RoundTrip_LargeEscape() {
    // Value far past the 8-quotient escape — exercises the 64-bit literal path.
    var values = new ulong[] { 0x123456789ABCDEF0UL, 0xFEDCBA9876543210UL };
    var packed = Golomb.EncodeSequenceMod256(values);
    var decoded = Golomb.DecodeSequenceMod256(packed, values.Length);
    Assert.That(decoded, Is.EqualTo(values),
      "64-bit escape path must preserve full 64-bit values.");
  }

  [Test, Category("HappyPath")]
  public void Golomb_Constants_PinRiceParameters() {
    Assert.That(Golomb.RiceK, Is.EqualTo(8),
      "Acronis golomb_*_mod256 family uses Rice k=8 (divisor 256).");
    Assert.That(Golomb.Divisor, Is.EqualTo(256));
    Assert.That(Golomb.QuotientEscape, Is.EqualTo(8),
      "Decoder caps quotient at 8 per cmp $0x8 at libarchive3.so 0x53f53.");
  }

  [Test, Category("Sad")]
  public void Golomb_BitReader_NullData_Throws() {
    Assert.Throws<ArgumentNullException>(() => new Golomb.BitReader(null!));
  }

  [Test, Category("Sad")]
  public void Golomb_BitWriter_OutOfRangeCount_Throws() {
    var w = new Golomb.BitWriter();
    Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteBits(0, -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteBits(0, 99));
  }

  [Test, Category("Sad")]
  public void Golomb_DecodeSequence_NegativeCount_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      Golomb.DecodeSequenceMod256([], -1));
  }

  [Test, Category("HappyPath")]
  public void Golomb_BitReader_TruncatedStream_PadsWithZero() {
    var r = new Golomb.BitReader([0x00]); // 8 zero bits
    Assert.That(r.ReadBits(8), Is.EqualTo(0UL));
    Assert.That(r.ReadBits(8), Is.EqualTo(0UL),
      "Reading past the end returns zero (soft-fail; the binary's reader panics here).");
  }

  // ─── LSM record decoder — synthetic LEAF body ─────────────────────

  /// <summary>
  ///   Builds a synthetic <c>.tibx</c> container with one HDR page and one LSM_LEAF page
  ///   whose body is an LZ4-chained-stream-encoded copy of <paramref name="leafBodyPlaintext"/>.
  /// </summary>
  private static byte[] BuildContainerWithLeafBody(byte[] leafBodyPlaintext, byte ctreeId = 0) {
    // Compress as a single chunk via Lz4BlockCompressor and pack as one (BE32 zlen, BE32 len)
    // triple followed by the LZ4 block — the chained-stream format the binary's
    // LZ4_decompress_safe_continue walk at 0x54fb0 consumes.
    var compressed = Lz4BlockCompressor.Compress(leafBodyPlaintext);
    var leafBody = new byte[8 + compressed.Length];
    BinaryPrimitives.WriteUInt32BigEndian(leafBody.AsSpan(0, 4), (uint)compressed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(leafBody.AsSpan(4, 4), (uint)leafBodyPlaintext.Length);
    compressed.CopyTo(leafBody.AsSpan(8));

    var buf = new byte[HeaderPageSize + PageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    var leaf = buf.AsSpan(HeaderPageSize, PageSize);
    leaf[0] = 0x41;
    leaf[1] = (byte)AcronisTibxPageType.LsmLeaf;
    Encoding.ASCII.GetBytes("LEAF").CopyTo(leaf.Slice(8, 4));
    // Sub-header: version=2, encoding=3 (LZ4 chained), count=1, len=plaintext, zlen=leafBody.Length
    leaf[0xC] = 2;
    leaf[0xD] = AcronisTibxLsmRecord.EncodingLz4ChainedStream;
    BinaryPrimitives.WriteUInt16BigEndian(leaf.Slice(0xE, 2), 1);
    BinaryPrimitives.WriteUInt32BigEndian(leaf.Slice(0x10, 4), (uint)leafBodyPlaintext.Length);
    BinaryPrimitives.WriteUInt32BigEndian(leaf.Slice(0x14, 4), (uint)leafBody.Length);
    BinaryPrimitives.WriteUInt32BigEndian(leaf.Slice(0x18, 4), 1u);
    leaf[0x1C] = ctreeId;
    leafBody.CopyTo(leaf[LeafBodyOffset..]);
    return buf;
  }

  /// <summary>
  ///   Builds a synthetic <c>InputItem</c> ItemCommon attribute body matching the layout
  ///   that <see cref="AcronisFileMetaBodyDecoder.DecodeItemCommon"/> understands.
  /// </summary>
  private static byte[] BuildItemCommonBody(string name, ulong creationTime = 0,
    ulong lastWriteTime = 0) {
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var body = new byte[44 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)name.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), 0); // altLen = 0
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), 0x20); // ARCHIVE bit
    // Use 2024-01-15 UTC = 133497792000000000 FILETIME as a realistic anchor.
    var ct = creationTime != 0 ? creationTime : 133_497_792_000_000_000UL;
    var lwt = lastWriteTime != 0 ? lastWriteTime : 133_497_792_000_000_000UL;
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8, 8), ct);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16, 8), lwt);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(24, 8), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(32, 8), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40, 4), 0); // trailer
    nameBytes.CopyTo(body.AsSpan(44));
    return body;
  }

  // ─── DecodeLeafBody — happy path ──────────────────────────────────

  [Test, Category("HappyPath")]
  public void DecodeLeafBody_RoundTripsPlaintext() {
    var plaintext = new byte[] {
      0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE,
      0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08
    };
    var container = BuildContainerWithLeafBody(plaintext);
    var page = container.AsSpan(HeaderPageSize, PageSize);
    var sub = AcronisTibxLsmPageSubHeader.Parse(page);
    Assert.That(sub, Is.Not.Null);
    var decoded = AcronisTibxLsmRecord.DecodeLeafBody(page, sub!);
    Assert.That(decoded.Status, Is.EqualTo("ok"),
      "LZ4 chained-stream decoder must accept a single-chunk body produced by Lz4BlockCompressor.");
    Assert.That(decoded.DecompressedBody, Is.EqualTo(plaintext),
      "Decompressed bytes must round-trip the original plaintext.");
    Assert.That(decoded.ChunkCount, Is.EqualTo(1),
      "Synthetic fixture uses a single LZ4 chunk.");
  }

  [Test, Category("HappyPath")]
  public void DecodeLeafBody_ScansForItemCommonAttribute() {
    var icBody = BuildItemCommonBody("hello.txt");
    var container = BuildContainerWithLeafBody(icBody);
    var page = container.AsSpan(HeaderPageSize, PageSize);
    var sub = AcronisTibxLsmPageSubHeader.Parse(page);
    var decoded = AcronisTibxLsmRecord.DecodeLeafBody(page, sub!);
    Assert.That(decoded.Status, Is.EqualTo("ok"));
    Assert.That(decoded.CandidateItemNames, Is.Not.Empty,
      "Scanner must find the ItemCommon attribute body at offset 0 of the decompressed buffer.");
    Assert.That(decoded.CandidateItemNames[0].Name, Is.EqualTo("hello.txt"),
      "Recovered name must match the original UTF-16LE filename.");
  }

  [Test, Category("HappyPath")]
  public void Reader_WalksContainerWithEmbeddedItemCommon() {
    var icBody = BuildItemCommonBody("report.docx");
    var container = BuildContainerWithLeafBody(icBody);
    using var ms = new MemoryStream(container);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.ScannedItemNames, Has.Count.GreaterThanOrEqualTo(1),
      "Reader must surface the scanned ItemCommon candidate through its public property.");
    Assert.That(r.ScannedItemNames[0].Name, Is.EqualTo("report.docx"));
    Assert.That(r.DecodedLeaves, Has.Count.EqualTo(1),
      "One LSM_LEAF page in the synthetic container.");
    Assert.That(r.DecodedLeaves[0].Status, Is.EqualTo("ok"));
    Assert.That(r.DecodedLeaves[0].ScannedItemNameCount, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_SurfacesLsmRecordsTsv() {
    var icBody = BuildItemCommonBody("Document.pdf");
    var container = BuildContainerWithLeafBody(icBody, ctreeId: 1);
    using var ms = new MemoryStream(container);
    using var r = new AcronisTibxReader(ms);
    var tsv = r.Entries.Single(e => e.Name == "lsm-records.tsv");
    var text = Encoding.UTF8.GetString(tsv.Data);
    Assert.That(text, Does.Contain("# Stage-3 LSM record-stream decoder"),
      "lsm-records.tsv must include the Stage-3 header comment.");
    Assert.That(text, Does.Contain("Document.pdf"),
      "Scanned ItemCommon candidate filename must appear in the second section.");
    Assert.That(text, Does.Contain("\t1\t0x03\t"),
      "Per-LEAF row must include ctree_id=1 and encoding=0x03 columns.");
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsStage3DecodedAndBlockerSurface() {
    var icBody = BuildItemCommonBody("x.txt");
    var container = BuildContainerWithLeafBody(icBody);
    using var ms = new MemoryStream(container);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("decoded_5=leaf_body_lz4_chained_stream"),
      "Stage-3 metadata must surface the LZ4 chained-stream decoder.");
    Assert.That(text, Does.Contain("decoded_6=itemcommon_attribute_scan"),
      "Stage-3 metadata must surface the ItemCommon attribute scanner.");
    Assert.That(text, Does.Contain("decoded_7=golomb_rice_codec_mod256"),
      "Stage-3 metadata must surface the Golomb-Rice codec for GOLOMB-page filter bodies.");
    Assert.That(text, Does.Contain("re_target_6=lsm_page_read"),
      "RE provenance must cite the lsm_page_read entrypoint.");
    Assert.That(text, Does.Contain("re_target_7=lz4_chained_stream_decoder"),
      "RE provenance must cite the LZ4 chained-stream decoder.");
    Assert.That(text, Does.Contain("re_target_8=golomb_decode_mod256"),
      "RE provenance must cite the Golomb codec entrypoint.");
    Assert.That(text, Does.Contain("lsm_leaf_decode_attempts=1"));
    Assert.That(text, Does.Contain("lsm_leaf_decode_succeeded=1"));
    Assert.That(text, Does.Contain("lsm_scanned_item_name_count=1"));
  }

  // ─── DecodeLeafBody — sad paths ───────────────────────────────────

  [Test, Category("Sad")]
  public void DecodeLeafBody_UnsupportedEncoding_ReturnsSoftFailure() {
    // Encoding=4 is the alternative path we haven't decoded.
    var sub = new AcronisTibxLsmPageSubHeader(2, 4, 1, 32, 16, 1, 0);
    var page = new byte[PageSize];
    var decoded = AcronisTibxLsmRecord.DecodeLeafBody(page, sub);
    Assert.That(decoded.Status, Does.StartWith("unsupported_encoding"),
      "Encoding=4 must produce a soft-failure status, not throw.");
    Assert.That(decoded.DecompressedBody, Is.Null);
    Assert.That(decoded.CandidateItemNames, Is.Empty);
  }

  [Test, Category("Sad")]
  public void DecodeLeafBody_EmptyBody_ReturnsEmptyStatus() {
    var sub = new AcronisTibxLsmPageSubHeader(2, 3, 0, 0, 0, 0, 0);
    var page = new byte[PageSize];
    var decoded = AcronisTibxLsmRecord.DecodeLeafBody(page, sub);
    Assert.That(decoded.Status, Is.EqualTo("empty_body"));
    Assert.That(decoded.DecompressedBody, Is.Empty,
      "Empty-body soft-fail returns an empty byte[] for the caller.");
  }

  [Test, Category("Sad")]
  public void DecodeLeafBody_BufferUnderrun_ReturnsSoftFailure() {
    // Declared zlen larger than the page buffer past the body offset.
    var sub = new AcronisTibxLsmPageSubHeader(2, 3, 1, 64, 0x2000, 1, 0);
    var page = new byte[PageSize];
    var decoded = AcronisTibxLsmRecord.DecodeLeafBody(page, sub);
    Assert.That(decoded.Status, Does.StartWith("buffer_underrun"),
      "Truncated body must produce buffer_underrun status.");
  }

  // ─── ScanForItemCommonAttributes — false-positive rejection ───────

  [Test, Category("Sad")]
  public void ScanForItemCommonAttributes_RejectsAllZeroNoise() {
    var noise = new byte[256];
    var hits = AcronisTibxLsmRecord.ScanForItemCommonAttributes(noise);
    Assert.That(hits, Is.Empty,
      "All-zero noise has no realistic FILETIMEs — scanner must skip.");
  }

  [Test, Category("Sad")]
  public void ScanForItemCommonAttributes_RejectsFiletimeOutsideRealisticRange() {
    // FILETIME 1 = 1601-01-01 + 100ns — far outside the [1980, 2080] window.
    var icBody = BuildItemCommonBody("hello.txt", creationTime: 1, lastWriteTime: 1);
    var hits = AcronisTibxLsmRecord.ScanForItemCommonAttributes(icBody);
    Assert.That(hits, Is.Empty,
      "FILETIMEs outside [1980, 2080] cause the scanner to reject the candidate.");
  }

  [Test, Category("Sad")]
  public void ScanForItemCommonAttributes_RejectsForbiddenNtfsCharsInName() {
    // Build a body whose name contains '<' which is a forbidden NTFS character.
    var icBody = BuildItemCommonBody("bad<name.txt");
    var hits = AcronisTibxLsmRecord.ScanForItemCommonAttributes(icBody);
    Assert.That(hits, Is.Empty,
      "Name with forbidden '<' character must be rejected as a filename.");
  }

  [Test, Category("HappyPath")]
  public void ScanForItemCommonAttributes_RejectsZeroLengthName() {
    var body = new byte[44];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), 0); // nameLen = 0
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16, 8), 133_497_792_000_000_000UL);
    var hits = AcronisTibxLsmRecord.ScanForItemCommonAttributes(body);
    Assert.That(hits, Is.Empty,
      "Zero name length is filtered (real filenames are at least 1 char).");
  }

  [Test, Category("HappyPath")]
  public void ScanForItemCommonAttributes_AcceptsValidItemCommonInLargerBuffer() {
    // Embed the ItemCommon at offset 32 inside random padding to prove the scanner walks.
    var ic = BuildItemCommonBody("nested.bin");
    var buffer = new byte[32 + ic.Length + 32];
    ic.CopyTo(buffer.AsSpan(32));
    var hits = AcronisTibxLsmRecord.ScanForItemCommonAttributes(buffer);
    Assert.That(hits, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(hits[0].Name, Is.EqualTo("nested.bin"));
  }

  // ─── Constants pinned to RE'd values ──────────────────────────────

  [Test, Category("HappyPath")]
  public void LsmRecord_Constants_PinEncodingAndBodyOffset() {
    Assert.That(AcronisTibxLsmRecord.EncodingLz4ChainedStream, Is.EqualTo(3),
      "Encoding byte 3 = LZ4 chained stream (libarchive3.so 0x55404).");
    Assert.That(AcronisTibxLsmRecord.EncodingAlternative, Is.EqualTo(4),
      "Encoding byte 4 = alternative path (not yet decoded).");
    Assert.That(AcronisTibxLsmRecord.LeafBodyOffset, Is.EqualTo(0x20),
      "LEAF body begins at page+0x20, immediately after the sub-header at +0xC..+0x1F.");
  }
}
