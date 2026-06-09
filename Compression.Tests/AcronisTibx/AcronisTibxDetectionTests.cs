using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.AcronisTibx;

namespace Compression.Tests.AcronisTibx;

/// <summary>
///   Stage-1 R/O acceptance gate for <see cref="AcronisTibxFormatDescriptor"/>.
///
///   <para>
///     Pins the binary-RE-recovered "ARCH" magic, the page-zero header layout
///     (version BE16 at +0x008, mode BE32 at +0x174, dump-field cluster at +0x1e0,
///     UUID at +0x233), the metadata.ini surface, the synthetic container entry,
///     and the disjoint-from-classic-.tib invariant. .tibx is reverse-engineered
///     from binary inspection of archive3.dll (ATI 2018) and libarchive3.so
///     (ATI 2021 initrd64) — classic .tib uses an entirely separate codepath
///     (magic CE 24 B9 A2) and is handled by FileFormat.Acronis.
///   </para>
/// </summary>
[TestFixture]
public class AcronisTibxDetectionTests {

  private const int HeaderPageSize = AcronisTibxReader.HeaderPageSize;

  /// <summary>
  ///   Builds a synthetic 4 KiB page-zero header with the "ARCH" magic and
  ///   user-supplied values for the parsed fields. Any field not specified is
  ///   left as zero; the binary RE confirmed the writer zeroes the entire
  ///   page-zero buffer before populating fields, so all-zero values are a
  ///   realistic baseline.
  /// </summary>
  private static byte[] BuildHeader(
    ushort version = 0x0008,
    uint modeWord = 0,
    byte[]? uuid = null,
    uint[]? dumpFields = null,
    int trailingPad = 0
  ) {
    var page = new byte[HeaderPageSize + trailingPad];
    Encoding.ASCII.GetBytes("ARCH").CopyTo(page.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(AcronisTibxReader.VersionOffset, 2), version);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(AcronisTibxReader.ModeOffset, 4), modeWord);
    if (uuid is not null) {
      if (uuid.Length != AcronisTibxReader.UuidLength)
        throw new ArgumentException(
          $"uuid must be {AcronisTibxReader.UuidLength} bytes", nameof(uuid));
      uuid.CopyTo(page.AsSpan(AcronisTibxReader.UuidOffset, AcronisTibxReader.UuidLength));
    }
    if (dumpFields is not null) {
      for (var i = 0; i < dumpFields.Length && i < 8; i++)
        BinaryPrimitives.WriteUInt32BigEndian(
          page.AsSpan(AcronisTibxReader.DumpFieldsStart + i * 4, 4), dumpFields[i]);
    }
    return page;
  }

  // ─── Descriptor identity ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_HasExpectedIdentity() {
    var d = new AcronisTibxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AcronisTibx"));
    Assert.That(d.DisplayName, Is.EqualTo("Acronis True Image .tibx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".tibx"));
    Assert.That(d.Extensions, Is.EquivalentTo(new[] { ".tibx" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_PinsArchMagicAtOffsetZero() {
    var d = new AcronisTibxFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    var sig = d.MagicSignatures[0];
    Assert.That(sig.Offset, Is.EqualTo(0));
    Assert.That(sig.Bytes, Is.EqualTo(new byte[] { 0x41, 0x52, 0x43, 0x48 }),
      "Magic must be the ASCII 'ARCH' tag (41 52 43 48) recovered from binary RE.");
    Assert.That(sig.Confidence, Is.GreaterThanOrEqualTo(0.90));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DistinctFromClassicTibMagic() {
    // Classic .tib magic is CE 24 B9 A2 (LE 0xA2B924CE) at offset 0. The two
    // descriptors must be disjoint so the registry's first-match algorithm
    // never confuses one for the other.
    var d = new AcronisTibxFormatDescriptor();
    var firstByte = d.MagicSignatures[0].Bytes[0];
    Assert.That(firstByte, Is.Not.EqualTo((byte)0xCE),
      "First byte must be 0x41 ('A'), not the classic .tib 0xCE prefix.");
    Assert.That(firstByte, Is.EqualTo((byte)0x41));
  }

  [Test, Category("HappyPath")]
  public void Capabilities_AreReadOnly() {
    var d = new AcronisTibxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      ".tibx is R/O — no writer path is implemented.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  // ─── Reader: header parse ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_AcceptsMinimal4ByteArchTag() {
    // The reader requires only the 4-byte magic; smaller-than-real-header
    // buffers still parse what fields fit, which makes the test scaffolding
    // tractable and matches Veeam/Paragon's existing pattern.
    using var ms = new MemoryStream("ARCH"u8.ToArray());
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.ValidHeader, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersionAtOffset0x008_BigEndian() {
    var page = BuildHeader(version: 0x0008);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.Version, Is.EqualTo(0x0008),
      "Version is BE16 at +0x008 per the vendor writer (ror $8 + mov %ax, 0x8(%esi)).");
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersion0x0007_AlternativeWriterPath() {
    // The vendor writer has two paths: the "flag matches 8" path emits 0x0008,
    // the alternative emits 0x0007 or (0x0007 | 1) = 0x0008. Pin the alternative.
    var page = BuildHeader(version: 0x0007);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.Version, Is.EqualTo(0x0007));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesModeWordAtOffset0x174_BigEndian() {
    var page = BuildHeader(modeWord: 0xDEADBEEF);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.ModeWord, Is.EqualTo(0xDEADBEEFu),
      "Mode word is BE32 at +0x174 per the vendor's mov 0x174(%esi), %eax + bswap + ar_mode_to_string.");
  }

  [Test, Category("HappyPath")]
  public void Reader_CopiesUuidAtOffset0x233_16Bytes() {
    var uuid = new byte[] {
      0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
      0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
    };
    var page = BuildHeader(uuid: uuid);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.ArchiveUuid, Is.EqualTo(uuid),
      "16-byte UUID lives at +0x233 — the parser's 5 unaligned BE32 loads at 0x233/0x237/0x23b/0x23f/0x243 collapse to the canonical 16-byte UUID at +0x233..+0x242.");
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesEightDumpFieldsAtOffset0x1E0_BigEndian() {
    var fields = new uint[] {
      0x11111111, 0x22222222, 0x33333333, 0x44444444,
      0x55555555, 0x66666666, 0x77777777, 0x88888888,
    };
    var page = BuildHeader(dumpFields: fields);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.DumpFields, Is.EqualTo(fields),
      "8 BE32 dump fields (fsize / offset / aligned_size / size + 4 commit-id fields) at +0x1e0..+0x1ff.");
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsFullPageWithTrailingPadBytes() {
    // Real .tibx archives are at minimum 4 KiB (the page-zero header itself)
    // and typically megabytes-to-gigabytes large. Make sure the reader doesn't
    // care about anything past the 4 KiB header.
    var page = BuildHeader(version: 0x0008, modeWord: 1, trailingPad: 8192);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(0x0008));
    Assert.That(r.ImageSize, Is.EqualTo(HeaderPageSize + 8192));
  }

  // ─── Reader: synthesised entries ──────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_EmitsMetadataIniAndBinSyntheticEntries() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "lsm-records.tsv", "pages.tsv", "acronis-tibx.bin" }),
      "Stage-3 walk surfaces metadata.ini (parsed header + page-type counts), lsm-records.tsv (per-LEAF decode + scanned ItemCommon candidates), pages.tsv (per-page summary table), and acronis-tibx.bin (verbatim container).");
  }

  [Test, Category("HappyPath")]
  public void Reader_BinEntry_HasSizeOfWholeContainer() {
    var page = BuildHeader(trailingPad: 1024);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var bin = r.Entries.Single(e => e.Name == "acronis-tibx.bin");
    Assert.That(bin.Size, Is.EqualTo(HeaderPageSize + 1024));
    Assert.That(bin.Data.Length, Is.EqualTo(HeaderPageSize + 1024));
  }

  // ─── Reader: metadata.ini surface ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void Metadata_SurfacesParseStatusAndStageMarkers() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("parse_status=ro-metadata+page-walk+lsm-record-decode"));
    Assert.That(text, Does.Contain("stage=3"));
    Assert.That(text, Does.Contain("ro_promotion=page-frame-walk + lz4-chained-stream-leaf-bodies + itemcommon-scan"));
    Assert.That(text, Does.Contain("rw_promotion=blocked"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsMagicBytesAndOffset() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("magic_ascii=ARCH"));
    Assert.That(text, Does.Contain("magic_bytes_hex=41 52 43 48"));
    Assert.That(text, Does.Contain("magic_offset=0"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsParsedHeaderFields() {
    var page = BuildHeader(
      version: 0x0008,
      modeWord: 0xCAFEBABE,
      uuid: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11,
             0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99],
      dumpFields: [0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8]);
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("version_be16=0x0008"));
    Assert.That(text, Does.Contain("version_value=8"));
    Assert.That(text, Does.Contain("mode_be32=0xCAFEBABE"));
    Assert.That(text, Does.Contain("archive_uuid_hex=AABBCCDDEEFF0011223344556677"),
      "UUID hex must echo each byte in archive-uuid-offset order.");
    Assert.That(text, Does.Contain("dump_field_0_be32=0x00000001"));
    Assert.That(text, Does.Contain("dump_field_7_be32=0x00000008"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsHeaderFieldOffsets() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("hdr_offset_magic=0x000"));
    Assert.That(text, Does.Contain("hdr_offset_version=0x008"));
    Assert.That(text, Does.Contain("hdr_offset_mode=0x174"));
    Assert.That(text, Does.Contain("hdr_offset_dump_fields=0x1E0"));
    Assert.That(text, Does.Contain("hdr_offset_uuid=0x233"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsPageTypeMagicTable() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("page_type_arch=ARCH"));
    Assert.That(text, Does.Contain("page_type_arci=ARCI"));
    Assert.That(text, Does.Contain("page_type_ldir=LDIR"));
    Assert.That(text, Does.Contain("page_type_leaf=LEAF"));
    Assert.That(text, Does.Contain("page_type_data=DATA"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_PinsReProvenance() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("re_target_1=archive3.dll"),
      "RE provenance must cite the Windows binary (archive3.dll, ATI 2018).");
    Assert.That(text, Does.Contain("re_target_2=libarchive3.so"),
      "RE provenance must cite the Linux ELF (libarchive3.so, ATI 2021).");
    Assert.That(text, Does.Contain("archive_hdr.c"),
      "RE provenance must cite the source-tree path of the header writer/parser.");
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsBlockersOnLsmTreeWalk() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    // Stage-3 promoted what was blocker_1/2 in Stage-2 to decoded_5/6/7; blockers renumber:
    // 1 = formal per-record framing (not the LZ4 wrapping)
    // 2 = encoding=4 alternative path
    // 3 = LDIR record framing
    // 4 = optional AES wrap
    // 5 = commit-info chain
    // 6 = dedup index
    // 7 = deduplicated ItemCommon attributes (16-byte MD5 side table)
    Assert.That(text, Does.Contain("blocker_1=lsm_record_framing_inside_decompressed_leaf_body"));
    Assert.That(text, Does.Contain("blocker_2=encoding_4_leaf_body_path"));
    Assert.That(text, Does.Contain("blocker_3=lsm_dir_page_record_stream"));
    Assert.That(text, Does.Contain("blocker_4=optional_aes_encryption_gates_leaf_bodies"));
    Assert.That(text, Does.Contain("blocker_5=commit_info_chain_not_decoded"));
    Assert.That(text, Does.Contain("blocker_6=content_defined_chunking_dedup_short_index"));
    Assert.That(text, Does.Contain("blocker_7=deduplicated_itemcommon_attributes"));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DistinguishesFromClassicTib() {
    var page = BuildHeader();
    using var ms = new MemoryStream(page);
    using var r = new AcronisTibxReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("classic_tib_magic=CE 24 B9 A2"));
    Assert.That(text, Does.Contain("tibx_magic=41 52 43 48"));
    Assert.That(text, Does.Contain("disjoint_first_4_bytes=true"));
  }

  // ─── Descriptor List() round-trip ────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_ReturnsThreeEntries() {
    var d = new AcronisTibxFormatDescriptor();
    using var ms = new MemoryStream(BuildHeader());
    var entries = d.List(ms, password: null);
    Assert.That(entries, Has.Count.EqualTo(4),
      "Stage-3 exposes metadata.ini + lsm-records.tsv + pages.tsv + acronis-tibx.bin.");
    Assert.That(entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "metadata.ini", "lsm-records.tsv", "pages.tsv", "acronis-tibx.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesAllEntriesToOutputDirectory() {
    var d = new AcronisTibxFormatDescriptor();
    using var ms = new MemoryStream(BuildHeader(trailingPad: 256));
    var outDir = Path.Combine(Path.GetTempPath(), $"tibx_extract_{Guid.NewGuid():N}");
    try {
      d.Extract(ms, outDir, password: null, files: null);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "acronis-tibx.bin")), Is.True);
      var bin = File.ReadAllBytes(Path.Combine(outDir, "acronis-tibx.bin"));
      Assert.That(bin.Length, Is.EqualTo(HeaderPageSize + 256),
        "acronis-tibx.bin must echo the verbatim container bytes.");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_FilterPicksSingleEntry() {
    var d = new AcronisTibxFormatDescriptor();
    using var ms = new MemoryStream(BuildHeader());
    var outDir = Path.Combine(Path.GetTempPath(), $"tibx_extract_filter_{Guid.NewGuid():N}");
    try {
      d.Extract(ms, outDir, password: null, files: ["metadata.ini"]);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "acronis-tibx.bin")), Is.False);
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  // ─── Sad paths ───────────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Reader_RejectsClassicTibMagic() {
    var img = new byte[64];
    // Classic .tib magic 0xA2B924CE (LE on disk: CE 24 B9 A2)
    img[0] = 0xCE; img[1] = 0x24; img[2] = 0xB9; img[3] = 0xA2;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new AcronisTibxReader(ms),
      "Classic .tib must NOT parse as .tibx — the two formats are disjoint and routed by magic.");
  }

  [Test, Category("Sad")]
  public void Reader_RejectsRandomMagic() {
    var img = new byte[64];
    img[0] = 0xDE; img[1] = 0xAD; img[2] = 0xBE; img[3] = 0xEF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new AcronisTibxReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsAlmostMagicWithLastByteWrong() {
    // Boundary case: A R C ? where last byte differs.
    var img = new byte[64];
    img[0] = (byte)'A'; img[1] = (byte)'R'; img[2] = (byte)'C'; img[3] = (byte)'I';
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new AcronisTibxReader(ms),
      "ARCI is the commit-info page magic — at file offset 0 it must NOT be accepted as a valid .tibx archive header.");
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooShort() {
    using var ms = new MemoryStream(new byte[3]);
    Assert.Throws<InvalidDataException>(() => _ = new AcronisTibxReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsNullStream() {
    Assert.Throws<ArgumentNullException>(() => _ = new AcronisTibxReader(null!));
  }

  // ─── Description-level invariants (forensic surfacing) ───────────

  [Test, Category("Stub")]
  public void Description_CitesReProvenanceAndDistinguishesFromClassicTib() {
    var d = new AcronisTibxFormatDescriptor();
    var desc = d.Description;
    Assert.That(desc, Does.Contain("archive3.dll"),
      "Description must cite the Windows binary the RE was driven from.");
    Assert.That(desc, Does.Contain("libarchive3.so"),
      "Description must cite the Linux ELF the RE was driven from.");
    Assert.That(desc, Does.Contain("'ARCH'").Or.Contain("\"ARCH\""),
      "Description must surface the 'ARCH' magic tag for registry consumers.");
    Assert.That(desc, Does.Contain("41 52 43 48"),
      "Description must cite the hex magic bytes.");
    Assert.That(desc, Does.Contain("classic .tib"),
      "Description must point at classic .tib as a separate format so consumers don't conflate them.");
    Assert.That(desc, Does.Contain("CE 24 B9 A2"),
      "Description must cite classic .tib's magic for unambiguous routing.");
  }

  [Test, Category("Stub")]
  public void Description_FlagsMetadataOnlyAndDocumentsBlockers() {
    var d = new AcronisTibxFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("metadata-only"),
      "Description must honestly state the surface is metadata-only.");
    Assert.That(desc, Does.Contain("lsm"),
      "Description must cite the LSM B+-tree as the unspecified data structure.");
    Assert.That(desc, Does.Contain("lsm_item.h"),
      "Description must name the Acronis-internal header that gates further promotion.");
  }
}
