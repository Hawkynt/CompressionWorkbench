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
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
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
  public void Capabilities_AreReadOnly() {
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
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
  public void Description_FlagsRoMetadata_NoCreateOrModify() {
    var d = new ParagonFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("r/o metadata"),
      $"Paragon Description must flag the R/O-metadata treatment honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  /// <summary>
  /// Locks in the R/O-metadata outcome. If anyone later flips the Description
  /// to advertise real entry walking, this test fails and forces them to
  /// update the investigation trail.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_PinsRoMetadataTreatment_AndCitesPImg() {
    var d = new ParagonFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("r/o metadata"),
      "R/O-metadata outcome must be explicitly pinned in the Description.");
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

    // Conclusion line must pin the "twelve vectors, all dead-ended" finding.
    Assert.That(text, Does.Contain("re_conclusion="),
      "Audit conclusion must be persisted.");
    Assert.That(text, Does.Contain("Twelve research vectors"),
      "Conclusion must cite the twelve vectors.");
    Assert.That(text.ToLowerInvariant(), Does.Contain("undocumented"),
      "Conclusion must restate that chunk framing remains undocumented.");
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
    Assert.That(desc, Does.Contain("twelve research vectors"),
      "Description must cite the twelve vectors as the scope of the investigation.");
    Assert.That(desc.ToLowerInvariant(), Does.Contain("dead-ended"),
      "Description must honestly state all vectors dead-ended.");

    // The retired AES-on-PBF claim must NOT reappear in the public surface.
    Assert.That(desc, Does.Not.Contain("optional AES payload encryption"),
      "The retired pre-audit AES-on-legacy-PBF claim must not reappear in the Description.");
    Assert.That(desc, Does.Contain("legacy PBF is unencrypted"),
      "Description must surface the audit's material correction.");
  }
}
