using System.Buffers.Binary;
using FileFormat.Aomei;

namespace Compression.Tests.Aomei;

/// <summary>
/// Pinning tests for the per-field byte offsets within the 0x20-byte
/// BR_IMAGE_INDEX_ENTRY_VDB descriptor, plus the
/// {DataLenInSet, DataOffInSet} u64 byte offsets within the 0x674-byte
/// BR_IMAGE_FILE_TAIL body, plus the three newly-pinned INFO_TYPE_*
/// numeric values.
///
/// <para>
/// Each test pins a single layout fact of the AOMEI image format.
/// </para>
///
/// <para>
/// What these tests pin (newly-decoded in this commit):
/// </para>
/// <list type="bullet">
///   <item><description>VDB entry field layout
///         <c>{RegNo:u32@0x00, BlockNo:u64@0x04, ImgOffset:u64@0x0C,
///         OldSize:u32@0x14, NewSize:u32@0x18, Crc32:u32@0x1C}</c>.</description></item>
///   <item><description>BR_IMAGE_FILE_TAIL body field offsets
///         <c>DataLenInSet@+0x620</c> and <c>DataOffInSet@+0x628</c>.</description></item>
///   <item><description>BR_IMAGE_FILE_TAIL trailing
///         BR_STANDARD_HEADER position — <c>Flag@+0x670, Size@+0x66C,
///         Crc32@+0x668, Reserved@+0x664</c> — the tail's standard
///         header sits at the END of the 0x674-byte struct, mirror-image
///         of the head's layout where it sits at the start.</description></item>
///   <item><description>INFO_TYPE_VOLUME_DATA_REGION = 0x109 plus
///         sizeof(BR_IMAGE_INFO_VOLUME_DATA_REGION) = 0x30.</description></item>
///   <item><description>INFO_TYPE_FLB_SUB_ENTRY_LIST = 0x110 and
///         INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST = 0x111 (resolves the
///         {0x110, 0x111, 0x128} candidate set).</description></item>
///   <item><description>End-to-end round trip through
///         <see cref="BrImageIndexEntryVdb.Read"/> /
///         <see cref="BrImageIndexEntryVdb.Write"/> over a hand-crafted
///         0x20-byte fixture.</description></item>
/// </list>
///
/// <para>
/// What these tests do NOT pin (documented-TODO):
/// </para>
/// <list type="bullet">
///   <item><description><c>sizeof(BR_IMAGE_INDEX_ENTRY_FDB)</c> — not yet
///         known.</description></item>
///   <item><description>The head / tail body layout past the recovered
///         DataOffInSet / DataLenInSet — head body bytes
///         0x10..0x65B and tail body bytes 0x10..0x61F remain
///         undocumented (the head's BR_STANDARD_HEADER sits at offset 0
///         per the prior pinning; everything between is unmapped).</description></item>
///   <item><description>The AES variant + IV derivation for encrypted
///         INFO_TYPE_IMAGE_ENCRYPT records.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AomeiVdbLayoutTests {

  // ─── INFO_TYPE_FLB_SUB_ENTRY_LIST / FLB_FILE_DATA_BLOCK_LIST ───────────

  [Test, Category("HappyPath")]
  public void InfoTypeFlbSubEntryList_PinsTo_0x110() {
    Assert.That(AomeiConstants.InfoTypeFlbSubEntryList, Is.EqualTo((ushort)0x110));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeFlbFileDataBlockList_PinsTo_0x111() {
    Assert.That(AomeiConstants.InfoTypeFlbFileDataBlockList, Is.EqualTo((ushort)0x111));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeVolumeDataRegion_PinsTo_0x109() {
    Assert.That(AomeiConstants.InfoTypeVolumeDataRegion, Is.EqualTo((ushort)0x109));
  }

  [Test, Category("HappyPath")]
  public void VendorVolumeDataRegionSize_PinsTo_0x30() {
    Assert.That(AomeiConstants.VendorVolumeDataRegionSize, Is.EqualTo(0x30));
  }

  [Test, Category("EquivalenceClass")]
  public void NewInfoTypes_DontCollideWithExistingInfoOrIndexTypes() {
    var newInfoTypes = new[] {
      AomeiConstants.InfoTypeVolumeDataRegion,
      AomeiConstants.InfoTypeFlbSubEntryList,
      AomeiConstants.InfoTypeFlbFileDataBlockList,
    };
    var allOthers = new[] {
      AomeiConstants.InfoTypeDiskInfo,
      AomeiConstants.InfoTypeVolumeInfo,
      AomeiConstants.InfoTypeImageSplitSize,
      AomeiConstants.InfoTypeImageCompress,
      AomeiConstants.InfoTypeImageEncrypt,
      AomeiConstants.InfoTypeImagePassword,
      AomeiConstants.InfoTypeImageComment,
      AomeiConstants.InfoTypeBackupTime,
      AomeiConstants.InfoTypeBackupType,
      AomeiConstants.InfoTypeBackupOption,
      AomeiConstants.InfoTypeFlbPathList,
      AomeiConstants.InfoTypeFlbBackupOption,
      AomeiConstants.InfoTypeFlbBackupOptionEx,
      AomeiConstants.IndexTypeRoot,
      AomeiConstants.IndexTypeVolume,
      AomeiConstants.IndexTypeDataBlock,
      AomeiConstants.IndexTypeDirTree,
      AomeiConstants.IndexTypeDataArea,
    };
    foreach (var n in newInfoTypes)
      Assert.That(allOthers, Does.Not.Contain(n),
        $"New INFO_TYPE_* tag 0x{n:X3} must not collide with any prior tag");
    Assert.That(newInfoTypes, Is.Unique);
  }

  [Test, Category("EquivalenceClass")]
  public void NewInfoTypes_InsideInfoEnumRange() {
    // 0x100 <= INFO_TYPE_* < 0x200 invariant — what makes the 0xF001
    // UserDataTypeTag namespace safe.
    Assert.That(AomeiConstants.InfoTypeVolumeDataRegion, Is.InRange(0x100, 0x1FF));
    Assert.That(AomeiConstants.InfoTypeFlbSubEntryList, Is.InRange(0x100, 0x1FF));
    Assert.That(AomeiConstants.InfoTypeFlbFileDataBlockList, Is.InRange(0x100, 0x1FF));
  }

  // ─── BR_IMAGE_INDEX_ENTRY_VDB per-field byte offsets ───────────────────

  [Test, Category("HappyPath")]
  public void VdbEntry_RegNoOffset_PinsTo_0x00() {
    Assert.That(AomeiConstants.VendorVdbEntryRegNoOffset, Is.EqualTo(0x00));
    Assert.That(BrImageIndexEntryVdb.RegNoOffset, Is.EqualTo(0x00));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_BlockNoOffset_PinsTo_0x04() {
    Assert.That(AomeiConstants.VendorVdbEntryBlockNoOffset, Is.EqualTo(0x04));
    Assert.That(BrImageIndexEntryVdb.BlockNoOffset, Is.EqualTo(0x04));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_ImgOffsetOffset_PinsTo_0x0C() {
    Assert.That(AomeiConstants.VendorVdbEntryImgOffsetOffset, Is.EqualTo(0x0C));
    Assert.That(BrImageIndexEntryVdb.ImgOffsetOffset, Is.EqualTo(0x0C));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_OldSizeOffset_PinsTo_0x14() {
    Assert.That(AomeiConstants.VendorVdbEntryOldSizeOffset, Is.EqualTo(0x14));
    Assert.That(BrImageIndexEntryVdb.OldSizeOffset, Is.EqualTo(0x14));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_NewSizeOffset_PinsTo_0x18() {
    Assert.That(AomeiConstants.VendorVdbEntryNewSizeOffset, Is.EqualTo(0x18));
    Assert.That(BrImageIndexEntryVdb.NewSizeOffset, Is.EqualTo(0x18));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_Crc32Offset_PinsTo_0x1C() {
    Assert.That(AomeiConstants.VendorVdbEntryCrc32Offset, Is.EqualTo(0x1C));
    Assert.That(BrImageIndexEntryVdb.Crc32Offset, Is.EqualTo(0x1C));
  }

  [Test, Category("EquivalenceClass")]
  public void VdbEntry_FieldOffsets_AreNonOverlapping_AndSumToVendorSize() {
    // Each field must occupy its expected width without overlap, and the
    // total must equal the pinned VendorVdbEntrySize.
    Assert.That(BrImageIndexEntryVdb.RegNoOffset, Is.EqualTo(0));
    Assert.That(BrImageIndexEntryVdb.BlockNoOffset, Is.EqualTo(BrImageIndexEntryVdb.RegNoOffset + 4));
    Assert.That(BrImageIndexEntryVdb.ImgOffsetOffset, Is.EqualTo(BrImageIndexEntryVdb.BlockNoOffset + 8));
    Assert.That(BrImageIndexEntryVdb.OldSizeOffset, Is.EqualTo(BrImageIndexEntryVdb.ImgOffsetOffset + 8));
    Assert.That(BrImageIndexEntryVdb.NewSizeOffset, Is.EqualTo(BrImageIndexEntryVdb.OldSizeOffset + 4));
    Assert.That(BrImageIndexEntryVdb.Crc32Offset, Is.EqualTo(BrImageIndexEntryVdb.NewSizeOffset + 4));
    Assert.That(BrImageIndexEntryVdb.Crc32Offset + 4, Is.EqualTo(AomeiConstants.VendorVdbEntrySize));
  }

  // ─── BR_IMAGE_INDEX_ENTRY_VDB round-trip over hand-crafted bytes ───────

  [Test, Category("HappyPath")]
  public void VdbEntry_Write_PlacesFieldsAtPinnedOffsets() {
    var vdb = new BrImageIndexEntryVdb {
      RegNo = 0x11223344,
      BlockNo = 0x5566778899AABBCCul,
      ImgOffset = 0xDEADBEEFCAFEBABEul,
      OldSize = 0x1A2B3C4D,
      NewSize = 0x5E6F7081,
      Crc32 = 0x92A3B4C5,
    };
    Span<byte> buf = stackalloc byte[AomeiConstants.VendorVdbEntrySize];
    vdb.Write(buf);

    // RegNo at +0x00 (u32 LE)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf[0x00..]), Is.EqualTo(0x11223344u));
    // BlockNo at +0x04 (u64 LE)
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(buf[0x04..]), Is.EqualTo(0x5566778899AABBCCul));
    // ImgOffset at +0x0C (u64 LE)
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(buf[0x0C..]), Is.EqualTo(0xDEADBEEFCAFEBABEul));
    // OldSize at +0x14 (u32 LE)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf[0x14..]), Is.EqualTo(0x1A2B3C4Du));
    // NewSize at +0x18 (u32 LE)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf[0x18..]), Is.EqualTo(0x5E6F7081u));
    // Crc32 at +0x1C (u32 LE)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf[0x1C..]), Is.EqualTo(0x92A3B4C5u));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_Read_ParsesFromHexLiteralFixture() {
    // Hand-crafted 32-byte fixture with each field set to a distinct value
    // so a wrong byte offset surfaces as a wrong field value.
    var fixture = new byte[] {
      0x44, 0x33, 0x22, 0x11,                                 // +0x00 RegNo = 0x11223344
      0xCC, 0xBB, 0xAA, 0x99, 0x88, 0x77, 0x66, 0x55,         // +0x04 BlockNo = 0x5566778899AABBCC
      0xBE, 0xBA, 0xFE, 0xCA, 0xEF, 0xBE, 0xAD, 0xDE,         // +0x0C ImgOffset = 0xDEADBEEFCAFEBABE
      0x4D, 0x3C, 0x2B, 0x1A,                                 // +0x14 OldSize = 0x1A2B3C4D
      0x81, 0x70, 0x6F, 0x5E,                                 // +0x18 NewSize = 0x5E6F7081
      0xC5, 0xB4, 0xA3, 0x92,                                 // +0x1C Crc32 = 0x92A3B4C5
    };
    Assert.That(fixture.Length, Is.EqualTo(AomeiConstants.VendorVdbEntrySize));

    var vdb = BrImageIndexEntryVdb.Read(fixture);
    Assert.That(vdb.RegNo, Is.EqualTo(0x11223344u));
    Assert.That(vdb.BlockNo, Is.EqualTo(0x5566778899AABBCCul));
    Assert.That(vdb.ImgOffset, Is.EqualTo(0xDEADBEEFCAFEBABEul));
    Assert.That(vdb.OldSize, Is.EqualTo(0x1A2B3C4Du));
    Assert.That(vdb.NewSize, Is.EqualTo(0x5E6F7081u));
    Assert.That(vdb.Crc32, Is.EqualTo(0x92A3B4C5u));
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_RoundTrip_WriteThenRead_PreservesAllFields() {
    var src = new BrImageIndexEntryVdb {
      RegNo = 42,
      BlockNo = 1024ul * 1024ul * 1024ul,
      ImgOffset = 0x00010002000300040ul,
      OldSize = 1 << 16,
      NewSize = 1 << 15,
      Crc32 = 0xCAFEBABE,
    };
    Span<byte> buf = stackalloc byte[AomeiConstants.VendorVdbEntrySize];
    src.Write(buf);
    var dst = BrImageIndexEntryVdb.Read(buf);
    Assert.That(dst.RegNo, Is.EqualTo(src.RegNo));
    Assert.That(dst.BlockNo, Is.EqualTo(src.BlockNo));
    Assert.That(dst.ImgOffset, Is.EqualTo(src.ImgOffset));
    Assert.That(dst.OldSize, Is.EqualTo(src.OldSize));
    Assert.That(dst.NewSize, Is.EqualTo(src.NewSize));
    Assert.That(dst.Crc32, Is.EqualTo(src.Crc32));
  }

  [Test, Category("Boundary")]
  public void VdbEntry_Read_RejectsBufferSmallerThanEntrySize() {
    var tooShort = new byte[AomeiConstants.VendorVdbEntrySize - 1];
    Assert.That(() => BrImageIndexEntryVdb.Read(tooShort), Throws.ArgumentException);
  }

  [Test, Category("Boundary")]
  public void VdbEntry_Write_RejectsBufferSmallerThanEntrySize() {
    var vdb = new BrImageIndexEntryVdb();
    Assert.That(() => {
      // Note: Span<byte> can't be a local var captured by lambda directly;
      // construct inside the lambda to keep this a Throws assertion.
      var tooShort = new byte[AomeiConstants.VendorVdbEntrySize - 1];
      vdb.Write(tooShort);
    }, Throws.ArgumentException);
  }

  [Test, Category("HappyPath")]
  public void VdbEntry_PlausibleLayout_NowAdvertisesPinnedOffsets() {
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("RegNo:u32@0x00"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("BlockNo:u64@0x04"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("ImgOffset:u64@0x0C"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("OldSize:u32@0x14"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("NewSize:u32@0x18"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("Crc32:u32@0x1C"));
  }

  // ─── BR_IMAGE_FILE_TAIL body field offsets ─────────────────────────────

  [Test, Category("HappyPath")]
  public void TailBody_DataLenInSetOffset_PinsTo_0x620() {
    Assert.That(AomeiConstants.VendorTailBodyDataLenInSetOffset, Is.EqualTo(0x620));
  }

  [Test, Category("HappyPath")]
  public void TailBody_DataOffInSetOffset_PinsTo_0x628() {
    Assert.That(AomeiConstants.VendorTailBodyDataOffInSetOffset, Is.EqualTo(0x628));
  }

  [Test, Category("EquivalenceClass")]
  public void TailBody_DataOffAndLen_AreConsecutiveU64s() {
    // The two u64 fields sit adjacent in memory: low offset is DataLenInSet,
    // high offset is DataOffInSet, separated by exactly 8 bytes.
    Assert.That(AomeiConstants.VendorTailBodyDataOffInSetOffset
                - AomeiConstants.VendorTailBodyDataLenInSetOffset,
                Is.EqualTo(8));
  }

  [Test, Category("EquivalenceClass")]
  public void TailBody_FieldsFitInsideTailSize() {
    // Both u64 fields must fit within the 0x674-byte BR_IMAGE_FILE_TAIL.
    Assert.That(AomeiConstants.VendorTailBodyDataLenInSetOffset + 8,
                Is.LessThanOrEqualTo(AomeiConstants.BiftSize));
    Assert.That(AomeiConstants.VendorTailBodyDataOffInSetOffset + 8,
                Is.LessThanOrEqualTo(AomeiConstants.BiftSize));
  }

  // ─── BR_IMAGE_FILE_TAIL trailing BR_STANDARD_HEADER position ───────────

  [Test, Category("HappyPath")]
  public void TailTrailer_FlagOffset_PinsTo_0x670() {
    Assert.That(AomeiConstants.VendorTailTrailerFlagOffset, Is.EqualTo(0x670));
  }

  [Test, Category("HappyPath")]
  public void TailTrailer_SizeOffset_PinsTo_0x66C() {
    Assert.That(AomeiConstants.VendorTailTrailerSizeOffset, Is.EqualTo(0x66C));
  }

  [Test, Category("HappyPath")]
  public void TailTrailer_Crc32Offset_PinsTo_0x668() {
    Assert.That(AomeiConstants.VendorTailTrailerCrc32Offset, Is.EqualTo(0x668));
  }

  [Test, Category("HappyPath")]
  public void TailTrailer_ReservedOffset_PinsTo_0x664() {
    // Mirrors the head's documented Reserved offset; placed by inference
    // from the {Reserved, Crc32, Size, Flag} 16-byte trailer.
    Assert.That(AomeiConstants.VendorTailTrailerReservedOffset, Is.EqualTo(0x664));
  }

  [Test, Category("EquivalenceClass")]
  public void TailTrailer_FieldsAreContiguousAndEndAtTailSize() {
    // Reserved -> Crc32 -> Size -> Flag, each 4 bytes apart, with Flag
    // ending precisely at the tail size (0x674).
    Assert.That(AomeiConstants.VendorTailTrailerCrc32Offset
                - AomeiConstants.VendorTailTrailerReservedOffset, Is.EqualTo(4));
    Assert.That(AomeiConstants.VendorTailTrailerSizeOffset
                - AomeiConstants.VendorTailTrailerCrc32Offset, Is.EqualTo(4));
    Assert.That(AomeiConstants.VendorTailTrailerFlagOffset
                - AomeiConstants.VendorTailTrailerSizeOffset, Is.EqualTo(4));
    Assert.That(AomeiConstants.VendorTailTrailerFlagOffset + 4,
                Is.EqualTo(AomeiConstants.BiftSize));
  }

  [Test, Category("EquivalenceClass")]
  public void TailTrailer_StartsImmediatelyAfterBodyFields() {
    // The DataOffInSet u64 ends at +0x630; the trailing 16-byte
    // BR_STANDARD_HEADER starts at +0x664. The 0x34-byte gap between
    // them remains undocumented (other body fields may live there).
    Assert.That(AomeiConstants.VendorTailBodyDataOffInSetOffset + 8,
                Is.LessThanOrEqualTo(AomeiConstants.VendorTailTrailerReservedOffset));
  }

  // ─── TypeName recognises the three newly-pinned INFO_TYPE_* values ─────

  [Test, Category("HappyPath")]
  public void AomeiInfoRecord_TypeName_RecognisesNewInfoTypes() {
    AssertTypeName(AomeiConstants.InfoTypeFlbSubEntryList,        "INFO_TYPE_FLB_SUB_ENTRY_LIST");
    AssertTypeName(AomeiConstants.InfoTypeFlbFileDataBlockList,   "INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST");
    AssertTypeName(AomeiConstants.InfoTypeVolumeDataRegion,       "INFO_TYPE_VOLUME_DATA_REGION");
  }

  // ─── Reader metadata.ini surfaces the new facts ────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_MetadataIni_SurfacesNewVdbAndTailFacts() {
    var d = new AomeiFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new Compression.Registry.FormatCreateOptions());
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_vdblayout_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      // VDB per-field byte offsets.
      Assert.That(meta, Does.Contain(
        "vdb_entry_field_offsets=RegNo:+0x00:u32,BlockNo:+0x04:u64,ImgOffset:+0x0C:u64,OldSize:+0x14:u32,NewSize:+0x18:u32,Crc32:+0x1C:u32"));
      // BIFT tail body layout.
      Assert.That(meta, Does.Contain("tail_body_layout=DataLenInSet:u64@+0x620,DataOffInSet:u64@+0x628"));
      Assert.That(meta, Does.Contain("tail_trailing_header_offsets=Reserved:+0x664,Crc32:+0x668,Size:+0x66C,Flag:+0x670"));
      // VOLUME_DATA_REGION size.
      Assert.That(meta, Does.Contain("volume_data_region_size=0x30"));
      // New INFO_TYPE_* tags surfaced in the enum list.
      Assert.That(meta, Does.Contain("0x109:VOLUME_DATA_REGION"));
      Assert.That(meta, Does.Contain("0x110:FLB_SUB_ENTRY_LIST"));
      Assert.That(meta, Does.Contain("0x111:FLB_FILE_DATA_BLOCK_LIST"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ─── Helpers ───────────────────────────────────────────────────────────

  private static void AssertTypeName(ushort type, string expected) {
    var hdr = new BrStandardHeader(0x10, type, 0);
    var rec = new AomeiInfoRecord(hdr, crcValid: true, body: [], fileOffset: 0);
    Assert.That(rec.TypeName, Is.EqualTo(expected),
      $"INFO_TYPE_* tag 0x{type:X3} must map to {expected}");
  }
}
