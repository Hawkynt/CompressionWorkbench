using System.Buffers.Binary;
using FileFormat.Aomei;

namespace Compression.Tests.Aomei;

/// <summary>
/// Pinning tests for the AOMEI INDEX_TYPE_DATABLOCK / DATAAREA body layout
/// reverse-engineered from ImgFile.dll (source-tree codename BRCloudv2,
/// embedded PDB path E:\BRCloudv2\src\ImgFile\ImageFile.cpp).
///
/// Each test pins a recovered fact so a regression to the prior partial state
/// — or a future refactor that breaks the layout — fails loudly. The
/// constants pinned here are the result of correlating assert-string xrefs
/// with `cmp r/m32, imm32` preambles and `push imm32` / `mov [...], imm32`
/// instructions immediately preceding the matching vendor callsite, plus
/// PDB-path inspection of the installer payload.
///
/// What these tests DO pin:
/// - The 16-byte vendor BR_STANDARD_HEADER size (cmp ecx, 0x10 preamble).
/// - The five INDEX_TYPE_* numeric values: ROOT=0x200, VOLUME=0x201,
///   DATABLOCK=0x202, DIRTREE=0x300, DATAAREA=0x301 — all five values
///   pinned by cmp r/m32, imm32 preambles immediately before their
///   matching `==Head.Type` assert string xrefs.
/// - The complete INFO_TYPE_* enum past the four shipped tags:
///   DISK_INFO=0x102, VOLUME_INFO=0x103, IMAGE_SPLIT_SIZE=0x104,
///   IMAGE_COMMENT=0x108, BACKUP_TIME=0x10B, BACKUP_OPTION=0x10D,
///   FLB_PATH_LIST=0x112, FLB_BACKUP_OPTION=0x113,
///   FLB_BACKUP_OPTION_EX=0x116.
/// - The BR_IMAGE_INDEX layout: EntryCount at +0x14, EntrySize at +0x18,
///   entries packed starting at +0x1C, with sizeof(VDB entry) = 0x20.
/// - The four-character vendor source-tree codename "BRCloudv2".
/// - The plausible-but-unconfirmed natural-alignment field layout sketch
///   for BR_IMAGE_INDEX_ENTRY_VDB.
///
/// What these tests do NOT pin (because the byte offsets aren't decided by
/// passive RE):
/// - The exact byte offset of each field within the 0x20-byte VDB entry.
/// - The byte offsets of DataOffInSet / DataLenInSet within the file tail.
/// - The size of BR_IMAGE_INDEX_ENTRY_FDB (no `cmp` preamble visible).
/// - Disambiguation among the {0x110, 0x111, 0x128} multi-candidate
///   numeric values for INFO_TYPE_FLB_SUB_ENTRY_LIST and
///   INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST.
/// </summary>
[TestFixture]
public class AomeiDataBlockBodyTests {

  // ─── INDEX_TYPE_* numeric values ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void IndexTypeRoot_PinsTo_0x200() {
    Assert.That(AomeiConstants.IndexTypeRoot, Is.EqualTo((ushort)0x200));
  }

  [Test, Category("HappyPath")]
  public void IndexTypeVolume_PinsTo_0x201() {
    Assert.That(AomeiConstants.IndexTypeVolume, Is.EqualTo((ushort)0x201));
  }

  [Test, Category("HappyPath")]
  public void IndexTypeDataBlock_PinsTo_0x202() {
    Assert.That(AomeiConstants.IndexTypeDataBlock, Is.EqualTo((ushort)0x202));
  }

  [Test, Category("HappyPath")]
  public void IndexTypeDirTree_PinsTo_0x300() {
    Assert.That(AomeiConstants.IndexTypeDirTree, Is.EqualTo((ushort)0x300));
  }

  [Test, Category("HappyPath")]
  public void IndexTypeDataArea_PinsTo_0x301() {
    Assert.That(AomeiConstants.IndexTypeDataArea, Is.EqualTo((ushort)0x301));
  }

  [Test, Category("EquivalenceClass")]
  public void IndexTypes_AreAllDistinct() {
    var all = new[] {
      AomeiConstants.IndexTypeRoot,
      AomeiConstants.IndexTypeVolume,
      AomeiConstants.IndexTypeDataBlock,
      AomeiConstants.IndexTypeDirTree,
      AomeiConstants.IndexTypeDataArea,
    };
    Assert.That(all, Is.Unique);
  }

  [Test, Category("EquivalenceClass")]
  public void IndexTypes_DontCollideWithInfoTypes() {
    var indexTypes = new[] {
      AomeiConstants.IndexTypeRoot,
      AomeiConstants.IndexTypeVolume,
      AomeiConstants.IndexTypeDataBlock,
      AomeiConstants.IndexTypeDirTree,
      AomeiConstants.IndexTypeDataArea,
    };
    var infoTypes = new[] {
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
    };
    foreach (var i in indexTypes)
      Assert.That(infoTypes, Does.Not.Contain(i),
        $"INDEX_TYPE_* tag 0x{i:X3} must not collide with any INFO_TYPE_* tag");
  }

  [Test, Category("EquivalenceClass")]
  public void IndexTypes_PartitionedByFamily() {
    // 0x2xx = disk-image (volume) family, 0x3xx = file-image (FLB) family
    // per the vendor's grouping (disk backups have ROOT->VOLUME->DATABLOCK;
    // file backups have ROOT->DIRTREE->DATAAREA).
    Assert.That(AomeiConstants.IndexTypeRoot       & 0xF00, Is.EqualTo(0x200));
    Assert.That(AomeiConstants.IndexTypeVolume     & 0xF00, Is.EqualTo(0x200));
    Assert.That(AomeiConstants.IndexTypeDataBlock  & 0xF00, Is.EqualTo(0x200));
    Assert.That(AomeiConstants.IndexTypeDirTree    & 0xF00, Is.EqualTo(0x300));
    Assert.That(AomeiConstants.IndexTypeDataArea   & 0xF00, Is.EqualTo(0x300));
  }

  // ─── INFO_TYPE_* numeric values (the new ones past the original four) ──

  [Test, Category("HappyPath")]
  public void InfoTypeDiskInfo_PinsTo_0x102() {
    Assert.That(AomeiConstants.InfoTypeDiskInfo, Is.EqualTo((ushort)0x102));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeVolumeInfo_PinsTo_0x103() {
    Assert.That(AomeiConstants.InfoTypeVolumeInfo, Is.EqualTo((ushort)0x103));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeImageSplitSize_PinsTo_0x104() {
    Assert.That(AomeiConstants.InfoTypeImageSplitSize, Is.EqualTo((ushort)0x104));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeImageComment_PinsTo_0x108() {
    Assert.That(AomeiConstants.InfoTypeImageComment, Is.EqualTo((ushort)0x108));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeBackupTime_PinsTo_0x10B() {
    Assert.That(AomeiConstants.InfoTypeBackupTime, Is.EqualTo((ushort)0x10B));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeBackupOption_PinsTo_0x10D() {
    Assert.That(AomeiConstants.InfoTypeBackupOption, Is.EqualTo((ushort)0x10D));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeFlbPathList_PinsTo_0x112() {
    Assert.That(AomeiConstants.InfoTypeFlbPathList, Is.EqualTo((ushort)0x112));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeFlbBackupOption_PinsTo_0x113() {
    Assert.That(AomeiConstants.InfoTypeFlbBackupOption, Is.EqualTo((ushort)0x113));
  }

  [Test, Category("HappyPath")]
  public void InfoTypeFlbBackupOptionEx_PinsTo_0x116() {
    Assert.That(AomeiConstants.InfoTypeFlbBackupOptionEx, Is.EqualTo((ushort)0x116));
  }

  [Test, Category("EquivalenceClass")]
  public void InfoTypes_AreAllDistinct() {
    var all = new[] {
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
    };
    Assert.That(all, Is.Unique);
  }

  [Test, Category("EquivalenceClass")]
  public void InfoTypes_AllInsideKnownEnumRange() {
    // 0x100 <= INFO_TYPE_* < 0x200 per the vendor's grouping (the
    // INDEX_TYPE_* tags are 0x200..0x3FF). This invariant is what makes
    // the 0xF001 UserDataTypeTag namespace safe.
    var allInfo = new[] {
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
    };
    foreach (var v in allInfo)
      Assert.That(v, Is.InRange(0x100, 0x1FF),
        $"INFO_TYPE_* value 0x{v:X3} expected in [0x100, 0x1FF]");
  }

  // ─── BR_STANDARD_HEADER vendor size ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void VendorStandardHeaderSize_PinsTo_16() {
    // Recovered from `cmp ecx, 0x10` preamble at the
    // `Length>=sizeof(BR_STANDARD_HEADER)` assert site in FlbImageWriter.cpp.
    Assert.That(AomeiConstants.VendorStandardHeaderSize, Is.EqualTo(16));
  }

  [Test, Category("HappyPath")]
  public void ShippedHeaderAlias_IsSmallerThanVendor() {
    // Documented intentional difference: our 12-byte alias is wire-incompatible
    // with the AOMEI application but lets us round-trip through our own reader.
    Assert.That(AomeiConstants.StandardHeaderSize, Is.LessThan(AomeiConstants.VendorStandardHeaderSize));
    Assert.That(AomeiConstants.VendorStandardHeaderSize - AomeiConstants.StandardHeaderSize, Is.EqualTo(4),
      "vendor adds a 4-byte trailing Reserved field");
  }

  // ─── BR_IMAGE_INDEX header layout ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void IndexHeader_EntryCountOffset_PinsTo_0x14() {
    Assert.That(AomeiConstants.VendorIndexEntryCountOffset, Is.EqualTo(0x14));
  }

  [Test, Category("HappyPath")]
  public void IndexHeader_EntrySizeOffset_PinsTo_0x18() {
    Assert.That(AomeiConstants.VendorIndexEntrySizeOffset, Is.EqualTo(0x18));
  }

  [Test, Category("HappyPath")]
  public void IndexHeader_EntriesOffset_PinsTo_0x1C() {
    Assert.That(AomeiConstants.VendorIndexEntriesOffset, Is.EqualTo(0x1C));
    Assert.That(AomeiConstants.VendorIndexEntriesOffset,
                Is.EqualTo(AomeiConstants.VendorIndexEntrySizeOffset + 4));
  }

  [Test, Category("HappyPath")]
  public void VdbEntrySize_PinsTo_0x20() {
    // Recovered from `cmp dword ptr [edx+0x18], 0x20` preamble at the
    // `pIndex->EntrySize==sizeof(BR_IMAGE_INDEX_ENTRY_VDB)` assert in
    // ImageVolume.cpp.
    Assert.That(AomeiConstants.VendorVdbEntrySize, Is.EqualTo(0x20));
  }

  // ─── BrImageIndex parser surface ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BrImageIndex_TryReadVendor_ParsesEntryCountAndSize() {
    // Synthesise a 0x1C-byte vendor index header by hand: 16 bytes of
    // BR_STANDARD_HEADER + 4 reserved + EntryCount + EntrySize.
    var record = new byte[AomeiConstants.VendorIndexEntriesOffset];
    // Type at +0, Size at +4, Crc32 at +8, Reserved at +12 — all skipped.
    BinaryPrimitives.WriteUInt32LittleEndian(
      record.AsSpan(AomeiConstants.VendorIndexEntryCountOffset, 4), 7u);
    BinaryPrimitives.WriteUInt32LittleEndian(
      record.AsSpan(AomeiConstants.VendorIndexEntrySizeOffset, 4),
      (uint)AomeiConstants.VendorVdbEntrySize);

    Assert.That(BrImageIndex.TryReadVendor(record, out var count, out var size), Is.True);
    Assert.That(count, Is.EqualTo(7u));
    Assert.That(size, Is.EqualTo((uint)AomeiConstants.VendorVdbEntrySize));
  }

  [Test, Category("Boundary")]
  public void BrImageIndex_TryReadVendor_RejectsShortRecord() {
    // A record shorter than 0x1C (the minimum index-header size) returns false.
    var tooShort = new byte[AomeiConstants.VendorIndexEntriesOffset - 1];
    Assert.That(BrImageIndex.TryReadVendor(tooShort, out _, out _), Is.False);
  }

  [Test, Category("HappyPath")]
  public void BrImageIndex_RoundTripsFieldsViaConstructor() {
    var idx = new BrImageIndex(AomeiConstants.IndexTypeDataBlock, 42, 0x20);
    Assert.That(idx.Type, Is.EqualTo(AomeiConstants.IndexTypeDataBlock));
    Assert.That(idx.EntryCount, Is.EqualTo(42u));
    Assert.That(idx.EntrySize, Is.EqualTo(0x20u));
  }

  [Test, Category("HappyPath")]
  public void BrImageIndex_OffsetConstants_MatchAomeiConstants() {
    Assert.That(BrImageIndex.VendorEntryCountOffset, Is.EqualTo(AomeiConstants.VendorIndexEntryCountOffset));
    Assert.That(BrImageIndex.VendorEntrySizeOffset, Is.EqualTo(AomeiConstants.VendorIndexEntrySizeOffset));
    Assert.That(BrImageIndex.VendorEntriesOffset, Is.EqualTo(AomeiConstants.VendorIndexEntriesOffset));
  }

  // ─── VDB / FDB entry data classes ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BrImageIndexEntryVdb_FieldsRoundTripViaInitializer() {
    var vdb = new BrImageIndexEntryVdb {
      RegNo = 1,
      BlockNo = 42,
      ImgOffset = 0xDEADBEEFCAFEu,
      NewSize = 0x10000,
      OldSize = 0x20000,
      Crc32 = 0x12345678,
    };
    Assert.That(vdb.RegNo, Is.EqualTo(1u));
    Assert.That(vdb.BlockNo, Is.EqualTo(42ul));
    Assert.That(vdb.ImgOffset, Is.EqualTo(0xDEADBEEFCAFEul));
    Assert.That(vdb.NewSize, Is.EqualTo(0x10000u));
    Assert.That(vdb.OldSize, Is.EqualTo(0x20000u));
    Assert.That(vdb.Crc32, Is.EqualTo(0x12345678u));
  }

  [Test, Category("HappyPath")]
  public void BrImageIndexEntryFdb_FieldsRoundTripViaInitializer() {
    var fdb = new BrImageIndexEntryFdb {
      BlockNo = 100,
      ImgOffset = 0x1000,
      NewSize = 256,
      OldSize = 512,
      Crc32 = 0xCAFEBABE,
    };
    Assert.That(fdb.BlockNo, Is.EqualTo(100ul));
    Assert.That(fdb.ImgOffset, Is.EqualTo(0x1000ul));
    Assert.That(fdb.NewSize, Is.EqualTo(256u));
    Assert.That(fdb.OldSize, Is.EqualTo(512u));
    Assert.That(fdb.Crc32, Is.EqualTo(0xCAFEBABEu));
  }

  [Test, Category("HappyPath")]
  public void VdbPlausibleLayout_DescribedAsFieldList() {
    // The plausible-but-unconfirmed natural-alignment layout must remain
    // documented as a non-empty hint string. If a real-sample-validated
    // exact layout is ever pinned, this should be promoted to numeric
    // offset constants and this test deleted.
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Is.Not.Empty);
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("RegNo"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("BlockNo"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("ImgOffset"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("NewSize"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("OldSize"));
    Assert.That(BrImageIndexEntryVdb.PlausibleLayout, Does.Contain("Crc32"));
  }

  // ─── AomeiInfoRecord.TypeName / IsIndex extensions ─────────────────────

  [Test, Category("HappyPath")]
  public void AomeiInfoRecord_TypeName_RecognisesAllNewInfoTypes() {
    AssertTypeName(AomeiConstants.InfoTypeImageSplitSize,   "INFO_TYPE_IMAGE_SPLIT_SIZE");
    AssertTypeName(AomeiConstants.InfoTypeImageComment,     "INFO_TYPE_IMAGE_COMMENT");
    AssertTypeName(AomeiConstants.InfoTypeBackupTime,       "INFO_TYPE_BACKUP_TIME");
    AssertTypeName(AomeiConstants.InfoTypeBackupOption,     "INFO_TYPE_BACKUP_OPTION");
    AssertTypeName(AomeiConstants.InfoTypeDiskInfo,         "INFO_TYPE_DISK_INFO");
    AssertTypeName(AomeiConstants.InfoTypeVolumeInfo,       "INFO_TYPE_VOLUME_INFO");
    AssertTypeName(AomeiConstants.InfoTypeFlbBackupOption,  "INFO_TYPE_FLB_BACKUP_OPTION");
    AssertTypeName(AomeiConstants.InfoTypeFlbBackupOptionEx,"INFO_TYPE_FLB_BACKUP_OPTION_EX");
    AssertTypeName(AomeiConstants.InfoTypeFlbPathList,      "INFO_TYPE_FLB_PATH_LIST");
  }

  [Test, Category("HappyPath")]
  public void AomeiInfoRecord_TypeName_RecognisesAllIndexTypes() {
    AssertTypeName(AomeiConstants.IndexTypeRoot,            "INDEX_TYPE_ROOT");
    AssertTypeName(AomeiConstants.IndexTypeVolume,          "INDEX_TYPE_VOLUME");
    AssertTypeName(AomeiConstants.IndexTypeDataBlock,       "INDEX_TYPE_DATABLOCK");
    AssertTypeName(AomeiConstants.IndexTypeDirTree,         "INDEX_TYPE_DIRTREE");
    AssertTypeName(AomeiConstants.IndexTypeDataArea,        "INDEX_TYPE_DATAAREA");
  }

  [Test, Category("HappyPath")]
  public void AomeiInfoRecord_IsIndex_OnlyTrueForIndexTypes() {
    foreach (ushort idx in new ushort[] {
      AomeiConstants.IndexTypeRoot,
      AomeiConstants.IndexTypeVolume,
      AomeiConstants.IndexTypeDataBlock,
      AomeiConstants.IndexTypeDirTree,
      AomeiConstants.IndexTypeDataArea,
    }) {
      Assert.That(MakeRecord(idx).IsIndex, Is.True, $"0x{idx:X3} should be IsIndex");
    }
    // Info types: IsIndex must be false.
    foreach (ushort info in new ushort[] {
      AomeiConstants.InfoTypeDiskInfo,
      AomeiConstants.InfoTypeImageCompress,
      AomeiConstants.InfoTypeBackupType,
      AomeiConstants.InfoTypeFlbPathList,
    }) {
      Assert.That(MakeRecord(info).IsIndex, Is.False, $"0x{info:X3} should NOT be IsIndex");
    }
  }

  [Test, Category("Boundary")]
  public void AomeiInfoRecord_TypeName_UnknownReportsHex() {
    var rec = MakeRecord(0xABCD);
    Assert.That(rec.TypeName, Is.EqualTo("UNKNOWN_0xABCD"));
  }

  // ─── Vendor codename ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void VendorSourceTreeCodename_PinsToBRCloudv2() {
    Assert.That(AomeiConstants.VendorSourceTreeCodename, Is.EqualTo("BRCloudv2"));
  }

  // ─── Reader metadata.ini surfaces the new enums ────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_MetadataIni_SurfacesNewEnumeration() {
    var d = new AomeiFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new Compression.Registry.FormatCreateOptions());
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_blockbody_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("vendor_standard_header_size=0x10"));
      Assert.That(meta, Does.Contain("info_types_enum="));
      Assert.That(meta, Does.Contain("0x202:DATABLOCK"));
      Assert.That(meta, Does.Contain("0x301:DATAAREA"));
      Assert.That(meta, Does.Contain("0x200:ROOT"));
      Assert.That(meta, Does.Contain("0x102:DISK_INFO"));
      Assert.That(meta, Does.Contain("0x104:IMAGE_SPLIT_SIZE"));
      Assert.That(meta, Does.Contain("0x108:IMAGE_COMMENT"));
      Assert.That(meta, Does.Contain("vdb_entry_size=0x20"));
      Assert.That(meta, Does.Contain("index_entry_layout_offsets=entry_count:+0x14,entry_size:+0x18,entries:+0x1C"));
      Assert.That(meta, Does.Contain("vdb_entry_field_names=RegNo,BlockNo,ImgOffset,NewSize,OldSize,Crc32"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ─── Helpers ───────────────────────────────────────────────────────────

  private static AomeiInfoRecord MakeRecord(ushort type) {
    var hdr = new BrStandardHeader(0x10, type, 0);
    return new AomeiInfoRecord(hdr, crcValid: true, body: [], fileOffset: 0);
  }

  private static void AssertTypeName(ushort type, string expected) {
    var rec = MakeRecord(type);
    Assert.That(rec.TypeName, Is.EqualTo(expected),
      $"INFO/INDEX_TYPE_ tag 0x{type:X3} must map to {expected}");
  }
}
