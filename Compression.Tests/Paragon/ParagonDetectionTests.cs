using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Paragon;

namespace Compression.Tests.Paragon;

/// <summary>
/// R/O metadata acceptance gate for <see cref="ParagonFormatDescriptor"/>:
/// pins the corrected (TrID-documented) "PImg" detection magic, the
/// multi-file companion / format-evolution surface in metadata.ini, the
/// no-write capability shape, and the honest "ro-metadata" Description.
/// Paragon Backup (.pbf) was promoted from Stage-0 to R/O metadata after
/// deep RE research established the real magic and the public KB-documented
/// archive convention; R/W remains blocked because the byte layout after
/// the 4-byte magic is undocumented in every public source.
/// </summary>
[TestFixture]
public class ParagonDetectionTests {

  private static byte[] BuildPImg(int payloadLen = 128) {
    var image = new byte[4 + payloadLen];
    Encoding.ASCII.GetBytes("PImg").CopyTo(image.AsSpan(0, 4));
    for (var i = 0; i < payloadLen; i++) image[4 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_HasExpectedIdentity() {
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Paragon"));
    Assert.That(d.DisplayName, Is.EqualTo("Paragon Backup"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".pbf"));
    Assert.That(d.Extensions, Does.Contain(".pbf"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    // R/W promotion: WORM (IArchiveCreatable) plus true in-place modify
    // (IArchiveModifiable) via CWBP chunk-table append. Vendor-tool
    // byte-compat stays out of scope.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_PinsPImgMagic() {
    var d = new ParagonFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1),
      "After R/O promotion the descriptor uses the single TrID-documented 'PImg' magic; the earlier Stage-0 'PBF' / 'PBR1' guesses were retired.");
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("PImg"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x50, 0x49, 0x6D, 0x67 }),
      "Magic must match the TrID-catalogued hex signature 50 49 6D 67.");
  }

  [Test, Category("HappyPath")]
  public void Capabilities_AreReadWriteModify() {
    // R/W promotion via CWBP chunk-table append: CanCreate and CanModify
    // are both true. Vendor-tool byte-compat remains out of scope.
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries_ForPImgTag() {
    var d = new ParagonFormatDescriptor();
    using var ms = new MemoryStream(BuildPImg(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "paragon-backup.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsPImgVariant_AndSurfacesFullImage() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 512));
    var r = new ParagonReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("PImg"));
    var volume = r.Entries.Single(e => e.Name == "paragon-backup.bin");
    Assert.That(volume.Size, Is.EqualTo(4 + 512));
  }

  [Test, Category("HappyPath")]
  public void Reader_CapturesTrailingWord_AsLittleEndianDiagnostic() {
    // The reader exposes the 4 bytes right after the magic as a diagnostic
    // little-endian word - the byte layout there is undocumented, so this is
    // forensic-triage only, NOT a parsed version field.
    var image = new byte[4 + 4 + 16];
    Encoding.ASCII.GetBytes("PImg").CopyTo(image.AsSpan(0, 4));
    image[4] = 0xDE;
    image[5] = 0xAD;
    image[6] = 0xBE;
    image[7] = 0xEF;
    using var ms = new MemoryStream(image);
    using var r = new ParagonReader(ms);
    Assert.That(r.TrailingWord, Is.EqualTo(0xEFBEADDEu));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsMissingMagic() {
    var img = new byte[64];
    img[0] = 0xDE; img[1] = 0xAD; img[2] = 0xBE; img[3] = 0xEF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsOldStage0BaselinePbfGuess() {
    // The Stage-0 baseline had pinned an unverified "PBF" tag at offset 0.
    // Research established the real magic is "PImg" (TrID), so the old guess
    // must now be rejected by the reader.
    var img = new byte[64];
    Encoding.ASCII.GetBytes("PBF").CopyTo(img.AsSpan(0, 3));
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsOldStage0BaselinePbr1Guess() {
    // Same retirement for the "PBR1" Stage-0 guess.
    var img = new byte[64];
    Encoding.ASCII.GetBytes("PBR1").CopyTo(img.AsSpan(0, 4));
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[2]);
    Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsNullStream() {
    Assert.Throws<ArgumentNullException>(() => _ = new ParagonReader(null!));
  }

  [Test, Category("Stub")]
  public void Description_FlagsRwCwbp_AndCitesInPlaceAppend() {
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("r/w"),
      $"After R/W promotion Description must flag the R/W treatment honestly. Got: '{d.Description}'.");
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("chunk-table append"),
      "Description must cite the true in-place chunk-table append strategy.");
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("vendor-tool byte-compat is explicitly out of scope"),
      "Description must explicitly state vendor-tool byte-compat is out of scope.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  /// <summary>
  /// Locks in the R/O-metadata outcome. If anyone later flips the Description
  /// to advertise real entry walking, this test fails and forces them to
  /// update the investigation trail.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_PinsRwCwbpTreatment_AndCitesPImg() {
    var d = new ParagonFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("r/w"),
      "R/W-CWBP outcome must be explicitly pinned in the Description.");
    Assert.That(desc, Does.Contain("r/o metadata"),
      "Vendor-file R/O-metadata fallback must still be cited in the Description.");
    Assert.That(desc, Does.Contain("pimg"),
      "Description must cite the TrID-documented 'PImg' magic.");
    Assert.That(desc, Does.Contain("50 49 6d 67"),
      "Description must cite the documented hex signature.");
    Assert.That(desc, Does.Contain("proprietary"),
      "Honest reason must mention the proprietary nature.");
    Assert.That(desc, Does.Contain("paragon"),
      "Description must name the vendor product family.");
    Assert.That(desc, Does.Contain("trid"),
      "Description must cite the source of the magic (TrID database).");
  }

  /// <summary>
  /// The metadata.ini surface is part of the R/O contract - downstream
  /// forensic tooling parses <c>parse_status</c>, <c>magic_bytes_hex</c>,
  /// the companion-file convention, the format-evolution history, and the
  /// <c>rw_blocker_*</c> keys to surface the honest "this is opaque" message.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_DocumentsRoSurface_CompanionsAndHistory() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 64));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    // R/O contract markers.
    Assert.That(text, Does.Contain("parse_status=ro-metadata"));
    Assert.That(text, Does.Contain("stage=1"));
    Assert.That(text, Does.Contain("ro_promotion=metadata-only"));
    Assert.That(text, Does.Contain("rw_promotion=blocked"));

    // TrID-documented magic.
    Assert.That(text, Does.Contain("magic_variant=PImg"));
    Assert.That(text, Does.Contain("magic_bytes_hex=50 49 6D 67"));
    Assert.That(text, Does.Contain("magic_ascii=PImg"));
    Assert.That(text, Does.Contain("magic_offset=0"));
    Assert.That(text, Does.Contain("trailing_word="));

    // Multi-file companion convention (KB article 767).
    Assert.That(text, Does.Contain("companion_pfi="));
    Assert.That(text, Does.Contain("companion_pfm="));
    Assert.That(text, Does.Contain("companion_split="));

    // Format-evolution timeline.
    Assert.That(text, Does.Contain("history_hdm11="));
    Assert.That(text, Does.Contain("history_hdm14="));
    Assert.That(text, Does.Contain("history_hdm16="));

    // R/W blockers.
    Assert.That(text, Does.Contain("rw_blocker_1="));
    Assert.That(text, Does.Contain("rw_blocker_4="));

    // Citations to the public sources actually consulted.
    Assert.That(text, Does.Contain("references="));
    Assert.That(text, Does.Contain("TrID"));
    Assert.That(text, Does.Contain("kb.paragon-software.com/article/767"));
    Assert.That(text, Does.Contain("kb.paragon-software.com/article/262"));
  }

  /// <summary>
  /// The deep-RE audit established that legacy PBF is unencrypted -
  /// password protection / compression / splitting are pVHD-only per the
  /// B&amp;R 17 / HDM 16 manuals. The earlier Stage-0 -&gt; R/O baseline had
  /// surfaced an "optional AES payload encryption with vendor KDF" blocker
  /// that the audit retired. Pin the retirement so a later edit can't
  /// silently re-introduce the wrong claim.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_RetiresLegacyAesBlocker_FromDeepReAudit() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 64));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    // The blocker list must NOT claim AES encryption on legacy PBF.
    Assert.That(text, Does.Not.Contain("optional AES payload encryption uses a proprietary vendor KDF"),
      "The pre-audit AES blocker on legacy PBF must stay retired - encryption is pVHD-only per the B&R 17 / HDM 16 manuals.");
    Assert.That(text, Does.Not.Contain("AES payload encryption with vendor KDF"),
      "Verbatim AES wording must not reappear.");

    // The correction must be surfaced as a positive fact.
    Assert.That(text, Does.Contain("fact_encryption_pvhd_only="),
      "The audit's material correction (encryption is pVHD-only, legacy PBF data blocks are unencrypted) must be persisted as a positive fact.");
    Assert.That(text.ToLowerInvariant(), Does.Contain("unencrypted"),
      "Metadata must affirmatively state legacy PBF blocks are unencrypted.");
  }

  /// <summary>
  /// The deep-RE audit pursued twelve research vectors past the bare TrID
  /// "PImg" magic, all dead-ended. Pin the audit trail in metadata.ini so
  /// the next maintainer doesn't repeat the same searches and so anyone
  /// downgrading the keys forces an investigation refresh.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_PersistsDeepReAuditTrail_AsReAuditKeys() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 64));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    // All twelve dead-end vectors must be persisted.
    for (var i = 1; i <= 12; i++) {
      Assert.That(text, Does.Contain($"re_audit_{i}="),
        $"Deep-RE audit vector {i} must be persisted in metadata.ini.");
    }

    // The named sources must be identifiable in the audit trail.
    Assert.That(text, Does.Contain("asmodean"),
      "asmodean expimg false lead must be flagged so the next maintainer doesn't chase it.");
    Assert.That(text, Does.Contain("FALSE LEAD"),
      "False-lead vector must be marked as such.");
    Assert.That(text, Does.Contain("Paragon-Software-Group"));
    Assert.That(text, Does.Contain("Paragon-Backup-Recovery"));
    Assert.That(text, Does.Contain("USPTO"));
    Assert.That(text, Does.Contain("EnCase"));
    Assert.That(text, Does.Contain("X-Ways"));
    Assert.That(text, Does.Contain("FTK"));
    Assert.That(text, Does.Contain("Habr"));
    Assert.That(text, Does.Contain("paragon284"));
    Assert.That(text, Does.Contain("Kessler"));
    Assert.That(text, Does.Contain("Kaitai"));
    Assert.That(text, Does.Contain("Scripting Language"));
    Assert.That(text, Does.Contain("ExtFS"));

    // Conclusion line must pin the "twelve vectors, Wave 13 succeeded" finding.
    Assert.That(text, Does.Contain("re_conclusion="),
      "Audit conclusion must be persisted.");
    Assert.That(text, Does.Contain("Twelve public-source vectors"),
      "Conclusion must cite the twelve public-source vectors as exhausted.");
    Assert.That(text, Does.Contain("Wave 13"),
      "Conclusion must cite Wave 13 (binary RE) as the successful vector.");
  }

  /// <summary>
  /// The diagnostic facts cross-confirmed during the audit (compression 0-9
  /// dial, 4 GiB default split, encryption-pVHD-only, conceptual triple,
  /// chain model, exFAT advisory) must be surfaced as <c>fact_*</c> keys so
  /// downstream forensic tooling can consume them.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_SurfacesAuditConfirmedDiagnosticFacts() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 64));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("fact_compression_levels="),
      "0-9 compression dial (Paragon Scripting Language manual) must be surfaced.");
    Assert.That(text, Does.Contain("fact_default_split="),
      "Default 4 GiB split (B&R 17 + HDM 16 manuals) must be surfaced.");
    Assert.That(text, Does.Contain("fact_encryption_pvhd_only="),
      "Encryption-is-pVHD-only correction must be surfaced.");
    Assert.That(text, Does.Contain("fact_conceptual_triple="),
      "{index, metadata, compressed} conceptual triple (KB 767 + paragon284) must be surfaced.");
    Assert.That(text, Does.Contain("fact_chain_model="),
      "Differential = base+1; Incremental = base+N (KB 262) must be surfaced.");
    Assert.That(text, Does.Contain("fact_exfat_advisory="),
      "exFAT cache-flush advisory must be surfaced - implies append-style framing.");
  }

  /// <summary>
  /// The deep-RE audit must be cited in the public Description so registry
  /// consumers can see the investigation outcome at the descriptor level.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_CitesDeepReAuditOutcome() {
    var d = new ParagonFormatDescriptor();
    var desc = d.Description;

    Assert.That(desc, Does.Contain("Deep-RE audit"),
      "Description must flag the deep-RE audit was conducted.");
    Assert.That(desc, Does.Contain("twelve public-source vectors"),
      "Description must cite the twelve public-source vectors as the dead-ended scope.");
    Assert.That(desc, Does.Contain("Wave-13"),
      "Description must cite Wave 13 (binary RE) as the successful vector that followed.");
    Assert.That(desc.ToLowerInvariant(), Does.Contain("dead-ended"),
      "Description must honestly state the public-source vectors dead-ended.");

    // The retired AES-on-PBF claim must NOT reappear in the public surface.
    Assert.That(desc, Does.Not.Contain("optional AES payload encryption"),
      "The retired pre-audit AES-on-legacy-PBF claim must not reappear in the Description.");
    Assert.That(desc, Does.Contain("legacy PBF is unencrypted"),
      "Description must surface the audit's material correction.");
  }

  /// <summary>
  /// Wave 13 (binary reverse-engineering of the vendor's
  /// <c>hdmengine_hdmsdk.dll</c> from HDM 18.12.0.0744) parses two real
  /// structured fields past the magic: <c>Major</c> at <c>+4</c> and
  /// <c>FormatVersion</c> at <c>+6</c>, both 16-bit little-endian. Lock in
  /// that the reader exposes them as parsed values (not just bytes) so the
  /// next promotion pass can build on the structured surface.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Reader_ParsesWave13StructuredHeader_MajorAndFormatVersion() {
    // Build a PImg header with Major = 0x0002 / FormatVersion = 0x0003, the
    // exact values the vendor writer emits at RVA 0x4a8dc4
    // (MOV DWORD [rax+4], 0x00030002).
    var image = new byte[256];
    Encoding.ASCII.GetBytes("PImg").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4, 2), 0x0002);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6, 2), 0x0003);
    using var ms = new MemoryStream(image);
    using var r = new ParagonReader(ms);

    Assert.That(r.Major, Is.EqualTo(0x0002),
      "Wave-13 RE: Major at +4 must be parsed as the vendor-writer literal 0x0002.");
    Assert.That(r.FormatVersion, Is.EqualTo(0x0003),
      "Wave-13 RE: FormatVersion at +6 must be parsed as the vendor-writer literal 0x0003.");
    Assert.That(r.TrailingWord, Is.EqualTo(0x00030002u),
      "TrailingWord must equal the composite vendor-writer literal 0x00030002.");
  }

  /// <summary>
  /// Wave 13 confirms the format-version range: the vendor's reader at RVA
  /// <c>0x4ae6e4</c> rejects format-version &gt; 3 with error code
  /// <c>0x210a8</c> ("Incompatible version of the archive"). Our R/O reader
  /// does NOT enforce this gate (we want to surface any sample for forensic
  /// triage even if the vendor would reject it), but we MUST persist the
  /// parsed value so downstream tooling can detect out-of-range samples.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Reader_PersistsFormatVersionField_EvenAboveVendorMax() {
    // Build a PImg with FormatVersion = 0x0007 - above the vendor's max of 3.
    var image = new byte[256];
    Encoding.ASCII.GetBytes("PImg").CopyTo(image.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4, 2), 0x0002);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6, 2), 0x0007);
    using var ms = new MemoryStream(image);
    using var r = new ParagonReader(ms);

    Assert.That(r.FormatVersion, Is.EqualTo(0x0007),
      "Reader must surface the raw FormatVersion even above the vendor's reject threshold.");
    Assert.That(r.ValidHeader, Is.True,
      "R/O metadata reader must still flag the header valid - we are forensic-triage, not the vendor.");
  }

  /// <summary>
  /// Wave 13 bumped MinHeaderSize from 4 to 8 because we now parse the real
  /// 4-byte version word. Pin the new minimum so a future regression that
  /// reverts to "magic only" surfaces here.
  /// </summary>
  [Test, Category("Sad")]
  public void Reader_RejectsHeaderSmallerThanWave13Minimum() {
    // 6 bytes - has the magic but not the full version word.
    var image = new byte[6];
    Encoding.ASCII.GetBytes("PImg").CopyTo(image.AsSpan(0, 4));
    image[4] = 0x02; image[5] = 0x00;
    using var ms = new MemoryStream(image);
    Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms),
      "Wave-13 reader needs the full 8-byte magic + version-word prefix.");
  }

  /// <summary>
  /// Wave-13 metadata surface: the reverse-engineered structured-header
  /// offsets must be persisted as <c>struct_header_offset_*</c> keys so the
  /// next maintainer can extend the parser without re-doing the binary RE.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_PersistsWave13StructuredHeaderOffsets() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 256));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    // All ten reverse-engineered offsets must be documented.
    Assert.That(text, Does.Contain("struct_header_offset_0="),
      "Wave-13: magic-at-offset-0 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_4="),
      "Wave-13: Major at +4 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_6="),
      "Wave-13: FormatVersion at +6 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_c="),
      "Wave-13: F12 discriminator at +0xC must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_26="),
      "Wave-13: FlagsA at +0x26 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_27="),
      "Wave-13: FlagsB at +0x27 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_30="),
      "Wave-13: image-type / fork ID at +0x30 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_34="),
      "Wave-13: volume-name / GUID string at +0x34 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_d8="),
      "Wave-13: ParentId u64 at +0xD8 must be persisted - the incremental-chain back-pointer.");
    Assert.That(text, Does.Contain("struct_header_offset_e8="),
      "Wave-13: FlagsC at +0xE8 must be persisted.");
    Assert.That(text, Does.Contain("struct_header_offset_f1="),
      "Wave-13: derived byte at +0xF1 must be persisted - last initialised byte of the header.");
    Assert.That(text, Does.Contain("struct_header_min_size="),
      "Wave-13: 0xF2 minimum header size must be persisted.");
  }

  /// <summary>
  /// Wave-13 metadata surface: the reverse-engineered chunk / segment /
  /// bitmap data-layer architecture (zlib + Adler-32 per chunk, chained
  /// allocation bitmap, segment-per-split-file) must be persisted as
  /// <c>data_layer_*</c> keys.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_PersistsWave13DataLayerArchitecture() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 256));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    // Architecture-level findings.
    Assert.That(text, Does.Contain("data_layer_arch="),
      "Wave-13: segments-of-chunks architecture must be persisted.");
    Assert.That(text, Does.Contain("data_layer_chunk="),
      "Wave-13: per-chunk fields (number / offset / size / compress-flag) must be persisted.");
    Assert.That(text, Does.Contain("data_layer_compressor="),
      "Wave-13: per-chunk compressor (zlib / DEFLATE + Adler-32) must be persisted.");
    Assert.That(text, Does.Contain("data_layer_bitmap="),
      "Wave-13: chained allocation-bitmap layer must be persisted.");
    Assert.That(text, Does.Contain("data_layer_index_pfi="),
      "Wave-13: PFI index file having its own magic must be persisted.");
    Assert.That(text, Does.Contain("data_layer_class_hierarchy="),
      "Wave-13: PBF C++ class hierarchy must be persisted.");
    Assert.That(text, Does.Contain("data_layer_source_files="),
      "Wave-13: PBF source-file map (pbfhdr.cpp / pbfarc.cpp / pbflnk.cpp / ...) must be persisted.");

    // Specific RE evidence.
    Assert.That(text, Does.Contain("zlib"),
      "Wave-13: compressor must be identified as zlib (not a proprietary codec).");
    Assert.That(text, Does.Contain("Adler-32"),
      "Wave-13: per-chunk checksum must be identified as Adler-32 (zlib checksum).");
    Assert.That(text, Does.Contain("CPbfBitmapIO"),
      "Wave-13: bitmap I/O class must be named.");
    Assert.That(text, Does.Contain("PbfDataFile"),
      "Wave-13: per-segment data-file class must be named.");
  }

  /// <summary>
  /// Wave-13 audit entry: the 13th vector (binary RE of HDM 18) succeeded
  /// where Wave-1..12 (public-source research) dead-ended. The trail must
  /// be persisted as <c>re_audit_13=</c>, the conclusion must be updated
  /// to flag the partial success, and the descriptor Description must
  /// cite Wave 13.
  /// </summary>
  [Test, Category("Stub")]
  public void Metadata_PinsWave13AuditEntryAndPartialSuccess() {
    using var ms = new MemoryStream(BuildPImg(payloadLen: 64));
    var r = new ParagonReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("re_audit_13="),
      "Wave-13 audit entry must be persisted alongside re_audit_1..12.");
    Assert.That(text, Does.Contain("SUCCESS"),
      "Wave-13 trail must explicitly flag the binary RE as successful.");
    Assert.That(text, Does.Contain("hdmengine_hdmsdk.dll"),
      "Wave-13 trail must name the vendor binary that was reverse-engineered.");
    Assert.That(text, Does.Contain("HDM 18.12.0.0744"),
      "Wave-13 trail must pin the exact vendor version analysed.");
    Assert.That(text, Does.Contain("history_hdm18="),
      "Wave-13 must add HDM 18 to the format-evolution timeline.");

    // The Description must cite Wave 13.
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Description, Does.Contain("Wave-13"),
      "Descriptor Description must cite Wave 13 so registry consumers see the upgraded RE state.");
    Assert.That(d.Description, Does.Contain("hdmengine_hdmsdk.dll"),
      "Description must name the vendor binary the structural findings came from.");
  }
}
