using System.Buffers.Binary;
using System.Text;
using FileFormat.AcronisTibx;

namespace Compression.Tests.AcronisTibx;

/// <summary>
///   Stage-2 acceptance gate for the page-frame walk added on top of <see cref="AcronisTibxReader"/>.
///
///   <para>
///     Pins the page-frame layout recovered from binary RE of <c>ar_page_verify</c>
///     (<c>libarchive3.so</c> at <c>0x6bef0</c>) — sentinel <c>'A'</c> at <c>+0x0</c>, page-type
///     tag at <c>+0x1</c>, BE32 CRC at <c>+0x4</c>, content magic at <c>+0x8</c> — and the LSM
///     sub-header at <c>+0xC..+0x1C</c> recovered from <c>lsm_dump_ctrees</c> at <c>0x590f7</c>
///     (version, encoding, BE16 count, BE32 len/zlen/seq, byte ctree-id).
///   </para>
///
///   <para>
///     Tests use synthetic fixtures only — there is no real <c>.tibx</c> archive in the test
///     resources. The fixtures pin the on-disk layout that the next stage (LSM record-stream
///     decoder) will rely on.
///   </para>
/// </summary>
[TestFixture]
public class AcronisTibxLsmWalkTests {

  private const int PageSize = AcronisTibxPage.PageSize;
  private const int HeaderPageSize = AcronisTibxReader.HeaderPageSize;

  /// <summary>
  ///   Builds a synthetic page-zero ARCH header followed by a sequence of fully-typed page
  ///   frames. Each entry in <paramref name="extraPages"/> describes one trailing 4 KiB page —
  ///   page-type tag, content magic, and optional LSM sub-header field values.
  /// </summary>
  private static byte[] BuildContainer(IEnumerable<SyntheticPage> extraPages) {
    var pages = extraPages.ToList();
    var buf = new byte[HeaderPageSize + pages.Count * PageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    for (var i = 0; i < pages.Count; i++) {
      var pageOffset = HeaderPageSize + i * PageSize;
      var span = buf.AsSpan(pageOffset, PageSize);
      pages[i].WriteInto(span);
    }
    return buf;
  }

  private sealed class SyntheticPage {
    public AcronisTibxPageType Type { get; init; }
    public byte[]? ContentMagic { get; init; }
    public uint StoredCrc { get; init; }
    public byte LsmVersion { get; init; }
    public byte LsmEncoding { get; init; }
    public ushort LsmCount { get; init; }
    public uint LsmLen { get; init; }
    public uint LsmZlen { get; init; }
    public uint LsmSeq { get; init; }
    public byte LsmCtreeId { get; init; }

    public void WriteInto(Span<byte> page) {
      page[0] = 0x41; // 'A' sentinel
      page[1] = (byte)this.Type;
      page[2] = 0;
      page[3] = 0;
      BinaryPrimitives.WriteUInt32BigEndian(page.Slice(4, 4), this.StoredCrc);
      if (this.ContentMagic is { Length: 4 })
        this.ContentMagic.CopyTo(page.Slice(8, 4));
      // LSM sub-header at +0xC..+0x1C
      page[0xC] = this.LsmVersion;
      page[0xD] = this.LsmEncoding;
      BinaryPrimitives.WriteUInt16BigEndian(page.Slice(0xE, 2), this.LsmCount);
      BinaryPrimitives.WriteUInt32BigEndian(page.Slice(0x10, 4), this.LsmLen);
      BinaryPrimitives.WriteUInt32BigEndian(page.Slice(0x14, 4), this.LsmZlen);
      BinaryPrimitives.WriteUInt32BigEndian(page.Slice(0x18, 4), this.LsmSeq);
      page[0x1C] = this.LsmCtreeId;
    }
  }

  // ─── Page-type enumeration ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void PageType_HasExpectedTagValues() {
    Assert.That((byte)AcronisTibxPageType.Unknown, Is.EqualTo(0),
      "Tag 0 = Unknown (string-table position 0).");
    Assert.That((byte)AcronisTibxPageType.Hdr, Is.EqualTo(1),
      "Tag 1 = HDR (string-table position 1).");
    Assert.That((byte)AcronisTibxPageType.LsmLeaf, Is.EqualTo(2),
      "Tag 2 = LSM_LEAF (string-table position 2).");
    Assert.That((byte)AcronisTibxPageType.LsmDir, Is.EqualTo(3),
      "Tag 3 = LSM_DIR (string-table position 3).");
    Assert.That((byte)AcronisTibxPageType.Golomb, Is.EqualTo(4),
      "Tag 4 = GOLOMB (string-table position 4).");
    Assert.That((byte)AcronisTibxPageType.Data, Is.EqualTo(5),
      "Tag 5 = DATA (string-table position 5).");
    Assert.That((byte)AcronisTibxPageType.Ci, Is.EqualTo(6),
      "Tag 6 = CI (string-table position 6).");
  }

  // ─── Page-frame parse: HDR page-zero ──────────────────────────────

  [Test, Category("HappyPath")]
  public void Page_Parse_PageZero_DetectsArchMagicAsHdr() {
    var page = new byte[PageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(page.AsSpan(0, 4));
    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.Hdr));
    Assert.That(Encoding.ASCII.GetString(p.ContentMagic), Is.EqualTo("ARCH"));
    Assert.That(p.StoredCrc, Is.EqualTo(0u),
      "HDR page-zero uses a different layout — surfaced CRC is synthetic zero.");
    Assert.That(p.LsmSubHeader, Is.Null,
      "HDR doesn't carry the LSM sub-header.");
  }

  [Test, Category("HappyPath")]
  public void Page_Parse_LeafPage_DecodesLsmSubHeader() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.LsmLeaf;
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(4, 4), 0xDEADBEEFu);
    Encoding.ASCII.GetBytes("LEAF").CopyTo(page.AsSpan(8, 4));
    page[0xC] = 2;
    page[0xD] = 3;
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(0xE, 2), 42);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x10, 4), 1024);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x14, 4), 512);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x18, 4), 0x12345678);
    page[0x1C] = 1;

    var p = AcronisTibxPage.Parse(page, pageIndex: 17, fileOffset: 17 * PageSize);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.LsmLeaf));
    Assert.That(p.PageIndex, Is.EqualTo(17));
    Assert.That(p.FileOffset, Is.EqualTo(17L * PageSize));
    Assert.That(p.StoredCrc, Is.EqualTo(0xDEADBEEFu),
      "Stored CRC must be the BE32 word at +0x4.");
    Assert.That(Encoding.ASCII.GetString(p.ContentMagic), Is.EqualTo("LEAF"),
      "LEAF content magic lives at +0x8.");
    Assert.That(p.LsmSubHeader, Is.Not.Null);
    Assert.That(p.LsmSubHeader!.Version, Is.EqualTo((byte)2));
    Assert.That(p.LsmSubHeader.Encoding, Is.EqualTo((byte)3));
    Assert.That(p.LsmSubHeader.Count, Is.EqualTo((ushort)42));
    Assert.That(p.LsmSubHeader.Len, Is.EqualTo(1024u));
    Assert.That(p.LsmSubHeader.Zlen, Is.EqualTo(512u));
    Assert.That(p.LsmSubHeader.Seq, Is.EqualTo(0x12345678u));
    Assert.That(p.LsmSubHeader.Id, Is.EqualTo((byte)1));
  }

  [Test, Category("HappyPath")]
  public void Page_Parse_DirPage_DecodesLsmSubHeader() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.LsmDir;
    Encoding.ASCII.GetBytes("LDIR").CopyTo(page.AsSpan(8, 4));
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(0xE, 2), 7);

    var p = AcronisTibxPage.Parse(page, pageIndex: 2, fileOffset: 0x2000);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.LsmDir));
    Assert.That(Encoding.ASCII.GetString(p.ContentMagic), Is.EqualTo("LDIR"));
    Assert.That(p.LsmSubHeader, Is.Not.Null);
    Assert.That(p.LsmSubHeader!.Count, Is.EqualTo((ushort)7));
  }

  [Test, Category("HappyPath")]
  public void Page_Parse_CiPage_SurfacesArciMagicWithoutLsmSubHeader() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.Ci;
    Encoding.ASCII.GetBytes("ARCI").CopyTo(page.AsSpan(8, 4));

    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.Ci));
    Assert.That(Encoding.ASCII.GetString(p.ContentMagic), Is.EqualTo("ARCI"));
    Assert.That(p.LsmSubHeader, Is.Null,
      "CI page-type doesn't carry the LSM sub-header layout we decoded.");
  }

  [Test, Category("HappyPath")]
  public void Page_Parse_DataPage_HasNoLsmSubHeader() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.Data;
    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.Data));
    Assert.That(p.LsmSubHeader, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void Page_Parse_UnknownTagAboveCi_IsClampedToUnknown() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = 0x7F; // arbitrary type tag above CI=6
    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Not.Null);
    Assert.That(p!.PageType, Is.EqualTo(AcronisTibxPageType.Unknown));
  }

  // ─── Page-frame parse: sad paths ──────────────────────────────────

  [Test, Category("Sad")]
  public void Page_Parse_NonASentinel_ReturnsNull() {
    var page = new byte[PageSize];
    page[0] = 0x42; // not 'A' (0x41) — fails ar_page_verify sentinel
    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Null);
  }

  [Test, Category("Sad")]
  public void Page_Parse_TooSmallBuffer_ReturnsNull() {
    var page = new byte[PageSize - 1];
    page[0] = 0x41;
    var p = AcronisTibxPage.Parse(page, pageIndex: 1, fileOffset: 0);
    Assert.That(p, Is.Null);
  }

  // ─── Reader: WalkPages aggregate ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_WalkPages_HeaderOnly_SurfacesOneHdrPage() {
    var buf = new byte[HeaderPageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.PageCount, Is.EqualTo(1));
    Assert.That(r.LsmEntries[0].PageType, Is.EqualTo(AcronisTibxPageType.Hdr));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.Hdr], Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_WalkPages_MixedContainer_CountsEachPageType() {
    var buf = BuildContainer([
      new SyntheticPage {
        Type = AcronisTibxPageType.LsmDir,
        ContentMagic = Encoding.ASCII.GetBytes("LDIR"),
        LsmCount = 3,
      },
      new SyntheticPage {
        Type = AcronisTibxPageType.LsmLeaf,
        ContentMagic = Encoding.ASCII.GetBytes("LEAF"),
        LsmCount = 10,
        LsmLen = 1000,
        LsmZlen = 600,
        LsmCtreeId = 0,
      },
      new SyntheticPage {
        Type = AcronisTibxPageType.LsmLeaf,
        ContentMagic = Encoding.ASCII.GetBytes("LEAF"),
        LsmCount = 15,
        LsmLen = 2000,
        LsmZlen = 1200,
        LsmCtreeId = 1,
      },
      new SyntheticPage {
        Type = AcronisTibxPageType.Data,
        ContentMagic = [0, 0, 0, 0],
      },
      new SyntheticPage {
        Type = AcronisTibxPageType.Ci,
        ContentMagic = Encoding.ASCII.GetBytes("ARCI"),
      },
      new SyntheticPage {
        Type = AcronisTibxPageType.Golomb,
        ContentMagic = [0, 0, 0, 0],
      },
    ]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);

    Assert.That(r.PageCount, Is.EqualTo(7),
      "1 HDR + 6 typed pages = 7 page frames total.");
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.Hdr], Is.EqualTo(1));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.LsmLeaf], Is.EqualTo(2));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.LsmDir], Is.EqualTo(1));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.Data], Is.EqualTo(1));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.Ci], Is.EqualTo(1));
    Assert.That(r.PageTypeCounts[AcronisTibxPageType.Golomb], Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_LsmEntries_PreserveFileOffsetsAndIndices() {
    var buf = BuildContainer([
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF") },
      new SyntheticPage { Type = AcronisTibxPageType.Data, ContentMagic = [0, 0, 0, 0] },
    ]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);

    Assert.That(r.LsmEntries[0].PageIndex, Is.EqualTo(1));
    Assert.That(r.LsmEntries[0].FileOffset, Is.EqualTo(0));
    Assert.That(r.LsmEntries[1].PageIndex, Is.EqualTo(2));
    Assert.That(r.LsmEntries[1].FileOffset, Is.EqualTo(HeaderPageSize));
    Assert.That(r.LsmEntries[2].PageIndex, Is.EqualTo(3));
    Assert.That(r.LsmEntries[2].FileOffset, Is.EqualTo(HeaderPageSize + PageSize));
  }

  [Test, Category("HappyPath")]
  public void Reader_Metadata_SurfacesPageWalkCounts() {
    var buf = BuildContainer([
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmCount = 12, LsmLen = 4000, LsmZlen = 2500, LsmCtreeId = 0 },
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmCount = 8, LsmLen = 3000, LsmZlen = 1500, LsmCtreeId = 0 },
      new SyntheticPage { Type = AcronisTibxPageType.LsmDir, ContentMagic = Encoding.ASCII.GetBytes("LDIR"), LsmCount = 4 },
    ]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("page_count=4"),
      "1 HDR + 3 typed pages = 4 page frames.");
    Assert.That(text, Does.Contain("page_count_hdr=1"));
    Assert.That(text, Does.Contain("page_count_lsmleaf=2"));
    Assert.That(text, Does.Contain("page_count_lsmdir=1"));
    Assert.That(text, Does.Contain("lsm_leaf_pages=2"));
    Assert.That(text, Does.Contain("lsm_dir_pages=1"));
    Assert.That(text, Does.Contain("lsm_leaf_record_count_sum=20"),
      "Sum of LSM leaf record counts = 12 + 8 = 20.");
    Assert.That(text, Does.Contain("lsm_dir_record_count_sum=4"));
    Assert.That(text, Does.Contain("lsm_leaf_uncompressed_size_sum=7000"),
      "Sum of LSM leaf len fields = 4000 + 3000 = 7000.");
    Assert.That(text, Does.Contain("lsm_leaf_compressed_size_sum=4000"),
      "Sum of LSM leaf zlen fields = 2500 + 1500 = 4000.");
    Assert.That(text, Does.Contain("lsm_ctree_id_count=1"),
      "Only ctree id 0 appears.");
    Assert.That(text, Does.Contain("lsm_ctree_ids=0"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Metadata_SurfacesMultipleCtreeIds() {
    var buf = BuildContainer([
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmCtreeId = 0 },
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmCtreeId = 2 },
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmCtreeId = 1 },
    ]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("lsm_ctree_id_count=3"));
    Assert.That(text, Does.Contain("lsm_ctree_ids=0,1,2"),
      "Ctree ids must be deduplicated and sorted.");
  }

  // ─── Reader: pages.tsv ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_PagesTsv_HasOneRowPerPage() {
    var buf = BuildContainer([
      new SyntheticPage { Type = AcronisTibxPageType.LsmLeaf, ContentMagic = Encoding.ASCII.GetBytes("LEAF"), LsmVersion = 2, LsmEncoding = 3, LsmCount = 5, LsmLen = 100, LsmZlen = 60, LsmSeq = 7, LsmCtreeId = 0 },
      new SyntheticPage { Type = AcronisTibxPageType.Data, ContentMagic = [0xDE, 0xAD, 0xBE, 0xEF] },
    ]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var tsv = r.Entries.Single(e => e.Name == "pages.tsv");
    var text = Encoding.UTF8.GetString(tsv.Data);
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // 3 header/comment lines + 1 column header + 3 data rows = 7 lines
    var dataRows = lines.Where(l => !l.StartsWith("#") && !l.StartsWith("page_index")).ToList();
    Assert.That(dataRows, Has.Count.EqualTo(3),
      "1 HDR + 1 LSM_LEAF + 1 DATA = 3 page rows in pages.tsv.");
    Assert.That(dataRows[0], Does.StartWith("1\t0x0\tHdr\tARCH"),
      "First row is the page-zero ARCH header.");
    Assert.That(dataRows[1], Does.Contain("LsmLeaf\tLEAF"),
      "Second row surfaces the LSM_LEAF page-type + content magic.");
    Assert.That(dataRows[1], Does.EndWith("\t2\t0x03\t5\t100\t60\t7\t0"),
      "LSM_LEAF row carries the decoded sub-header fields (encoding rendered as 0x-prefixed byte).");
    Assert.That(dataRows[2], Does.Contain("Data\tDE AD BE EF"),
      "DATA row surfaces hex-magic when bytes are non-printable.");
  }

  [Test, Category("HappyPath")]
  public void Reader_PagesTsv_HasColumnHeaderRow() {
    var buf = BuildContainer([]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var tsv = r.Entries.Single(e => e.Name == "pages.tsv");
    var text = Encoding.UTF8.GetString(tsv.Data);
    Assert.That(text, Does.Contain(
      "page_index\tfile_offset\tpage_type\tcontent_magic\tstored_crc_be32\tlsm_version\tlsm_encoding\tlsm_count\tlsm_len\tlsm_zlen\tlsm_seq\tlsm_ctree_id"));
  }

  // ─── Reader: metadata.ini documents page-frame layout ────────────

  [Test, Category("HappyPath")]
  public void Reader_Metadata_DocumentsPageFrameOffsets() {
    var buf = BuildContainer([]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("page_size=4096"));
    Assert.That(text, Does.Contain("page_frame_offset_sentinel=0x000 (byte 'A' / 0x41)"));
    Assert.That(text, Does.Contain("page_frame_offset_type_tag=0x001"));
    Assert.That(text, Does.Contain("page_frame_offset_crc_be32=0x004"));
    Assert.That(text, Does.Contain("page_frame_offset_content_magic=0x008"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Metadata_DocumentsPageTypeTagMapping() {
    var buf = BuildContainer([]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("page_type_tag_0=Unknown"));
    Assert.That(text, Does.Contain("page_type_tag_1=HDR (page-zero ARCH)"));
    Assert.That(text, Does.Contain("page_type_tag_2=LSM_LEAF (LEAF magic at +0x8)"));
    Assert.That(text, Does.Contain("page_type_tag_3=LSM_DIR (LDIR magic at +0x8)"));
    Assert.That(text, Does.Contain("page_type_tag_4=GOLOMB (Golomb-coded index)"));
    Assert.That(text, Does.Contain("page_type_tag_5=DATA (extent payload)"));
    Assert.That(text, Does.Contain("page_type_tag_6=CI (ARCI magic at +0x8)"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Metadata_DocumentsWhatIsDecoded() {
    var buf = BuildContainer([]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("decoded_1=page_zero_header"));
    Assert.That(text, Does.Contain("decoded_2=page_frame (8-byte preamble"));
    Assert.That(text, Does.Contain("ar_page_verify @ libarchive3.so 0x6bef0"));
    Assert.That(text, Does.Contain("decoded_3=lsm_page_sub_header"));
    Assert.That(text, Does.Contain("lsm_dump_ctrees @ libarchive3.so 0x590f7"));
    Assert.That(text, Does.Contain("decoded_4=page_type_classification"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Metadata_DocumentsStretchGoal() {
    var buf = BuildContainer([]);
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("stretch_goal=link_LSM_record_attributes_to_DATA_page_extents"),
      "metadata.ini must surface the stretch goal so it doesn't fall off the radar.");
    Assert.That(text, Does.Contain("AcronisFileMetaBodyDecoder"),
      "Stretch goal must point at the FileFormat.Acronis decoder that can be reused.");
  }

  // ─── Sad-path: garbage container ─────────────────────────────────

  [Test, Category("Sad")]
  public void Reader_GarbageBetweenHeaderAndEnd_StillReturnsPageRows() {
    // A real archive truncated mid-page (not a valid LSM page) should still parse —
    // the walker should categorise the bad page as Unknown rather than throwing.
    var buf = new byte[HeaderPageSize + PageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    // Trailing 4 KiB is all zeros (page slot is unwritten — leading byte != 'A').
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.PageCount, Is.EqualTo(2));
    Assert.That(r.LsmEntries[1].PageType, Is.EqualTo(AcronisTibxPageType.Unknown),
      "Zero-filled trailing page is Unknown (leading byte != 'A').");
  }

  [Test, Category("Sad")]
  public void Reader_TruncatedTrailingFragment_IsIgnored() {
    // Container has 1 full HDR page + 200 leftover bytes (less than one page) — the walker
    // ignores the fragment and only surfaces the full pages.
    var buf = new byte[HeaderPageSize + 200];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    using var ms = new MemoryStream(buf);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.PageCount, Is.EqualTo(1),
      "Trailing partial page is ignored — only the full HDR page is surfaced.");
  }

  // ─── Descriptor sanity: 4 entries via List() (Stage-3 adds lsm-records.tsv) ─────

  [Test, Category("HappyPath")]
  public void Descriptor_List_SurfacesPagesTsvAlongsideMetadataAndBin() {
    var d = new AcronisTibxFormatDescriptor();
    var buf = new byte[HeaderPageSize];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(buf.AsSpan(0, 4));
    using var ms = new MemoryStream(buf);
    var entries = d.List(ms, password: null);
    Assert.That(entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "metadata.ini", "lsm-records.tsv", "pages.tsv", "acronis-tibx.bin" }));
  }

  // ─── LSM sub-header parse sad paths ───────────────────────────────

  [Test, Category("Sad")]
  public void LsmSubHeader_Parse_TooShortBuffer_ReturnsNull() {
    Assert.That(AcronisTibxLsmPageSubHeader.Parse(new byte[0x10]), Is.Null);
  }

  [Test, Category("HappyPath")]
  public void LsmSubHeader_Parse_ExactlyFullHeaderLength_Succeeds() {
    var buf = new byte[0x20];
    buf[0xC] = 1;
    buf[0xD] = 3;
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0xE, 2), 99);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x10, 4), 0xCAFE);
    var sub = AcronisTibxLsmPageSubHeader.Parse(buf);
    Assert.That(sub, Is.Not.Null);
    Assert.That(sub!.Version, Is.EqualTo((byte)1));
    Assert.That(sub.Encoding, Is.EqualTo((byte)3));
    Assert.That(sub.Count, Is.EqualTo((ushort)99));
    Assert.That(sub.Len, Is.EqualTo(0xCAFEu));
  }
}
