using System.Text;
using Compression.Registry;
using FileFormat.Veeam;

namespace Compression.Tests.Veeam;

/// <summary>
/// Stage 0 acceptance gate for <see cref="VeeamFormatDescriptor"/>:
/// pins the detection magic, surface entry shape, file-type bookkeeping,
/// and honest "detection-only" Description so consumers do not file
/// R/O-promotion tickets against a format whose spec is unpublished.
/// </summary>
[TestFixture]
public class VeeamDetectionTests {

  // Builds a synthetic Veeam-shaped image with the ASCII "VEEAM" tag at a
  // caller-chosen offset within the leading 4 KiB scan window. This mirrors
  // how real .vbk/.vib/.vrb files carry the tag at a writer-version-
  // dependent offset rather than at offset 0.
  private static byte[] BuildMinimal(int tagOffset = 0, int payloadLen = 256) {
    var totalLen = Math.Max(tagOffset + 16 + payloadLen, 32);
    var image = new byte[totalLen];
    Encoding.ASCII.GetBytes("VEEAM").CopyTo(image.AsSpan(tagOffset, 5));
    // Stamp the trailing-word area with a deterministic pattern.
    image[tagOffset + 5] = 0xDE;
    image[tagOffset + 6] = 0xAD;
    image[tagOffset + 7] = 0xBE;
    image[tagOffset + 8] = 0xEF;
    for (var i = 0; i < payloadLen; i++)
      image[tagOffset + 16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new VeeamFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Veeam"));
    Assert.That(d.DisplayName, Does.Contain("Veeam"));
    Assert.That(d.Extensions, Does.Contain(".vbk"));
    Assert.That(d.Extensions, Does.Contain(".vib"));
    Assert.That(d.Extensions, Does.Contain(".vrb"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".vbk"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("VEEAM"u8.ToArray()));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>(),
      "Stage-0 Veeam must NOT advertise CanCreate — there is no published spec to write against.");
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoSyntheticEntries() {
    var d = new VeeamFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(tagOffset: 0, payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Has.Member("metadata.ini"));
    Assert.That(names.Count(n => n.EndsWith(".bin", StringComparison.Ordinal)),
      Is.EqualTo(1),
      "List() must surface exactly one raw payload entry alongside metadata.ini.");
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new VeeamFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("detection-only"),
      $"Veeam Description must flag Stage 0 honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }

  [Test, Category("Stub")]
  public void Description_NamesUnsupportedScopes() {
    // Honest Stage-0 doctrine: Description must enumerate the structural
    // blockers (CBT chain replay, dedup pool, encryption, missing spec) so
    // consumers do not file R/O-promotion tickets against an unspec'd
    // proprietary backup container.
    var d = new VeeamFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("cbt"),
      "Veeam Description must call out CBT (Changed Block Tracking) chain replay as a blocker.");
    Assert.That(desc, Does.Contain("dedup"),
      "Veeam Description must call out deduplication as a blocker.");
    Assert.That(desc, Does.Contain("encryption").Or.Contain("aes"),
      "Veeam Description must call out encryption as a blocker.");
    Assert.That(desc, Does.Contain("spec").Or.Contain("proprietary"),
      "Veeam Description must call out the missing published spec.");
    Assert.That(desc, Does.Contain(".vbk"),
      "Veeam Description must name the .vbk full-backup file type.");
    Assert.That(desc, Does.Contain(".vib"),
      "Veeam Description must name the .vib incremental file type.");
    Assert.That(desc, Does.Contain(".vrb"),
      "Veeam Description must name the .vrb reverse-incremental file type.");
  }

  [Test, Category("HappyPath")]
  public void Reader_FindsMagicAtOffsetZero() {
    var image = BuildMinimal(tagOffset: 0, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicOffset, Is.EqualTo(0));
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("BoundaryCase")]
  public void Reader_FindsMagicAtNonZeroOffset_WithinScanWindow() {
    // Equivalence class: real Veeam files carry the VEEAM tag at a
    // writer-version-dependent offset within the leading 4 KiB. Pin the
    // scan with a representative non-zero offset (512 = common DOS-style
    // header alignment) so a refactor that hardcodes offset 0 fails fast.
    var image = BuildMinimal(tagOffset: 512, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicOffset, Is.EqualTo(512));
  }

  [Test, Category("BoundaryCase")]
  public void Reader_FindsMagicAtScanWindowBoundary() {
    // Boundary test: tag whose first byte is the last legal scan-window
    // index (ScanWindow - tag.Length) must still be found.
    const int tagLen = 5;
    var tagOffset = VeeamReader.ScanWindow - tagLen;
    var image = BuildMinimal(tagOffset: tagOffset, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MagicOffset, Is.EqualTo(tagOffset));
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsImageWithoutVeeamTag() {
    // Without the VEEAM tag anywhere in the scan window, the reader must
    // refuse — this is the boundary case where a stray non-Veeam blob is
    // mis-routed to the Veeam reader via extension-only detection.
    var bogus = new byte[2048];
    for (var i = 0; i < bogus.Length; i++) bogus[i] = (byte)(i & 0x7F);
    using var ms = new MemoryStream(bogus);
    Assert.That(() => _ = new VeeamReader(ms),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsImageBelowMinimumSize() {
    var tiny = new byte[] { 0x56, 0x45, 0x45 }; // partial "VEE", 3 bytes < 8
    using var ms = new MemoryStream(tiny);
    Assert.That(() => _ = new VeeamReader(ms),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Reader_BookkeepsFileTypeHint_Full() {
    var image = BuildMinimal(tagOffset: 0, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    Assert.That(r.FileType, Is.EqualTo(VeeamFileType.Full));
    var payload = r.Entries.First(e => e.Name != "metadata.ini");
    Assert.That(payload.Name, Does.Contain("vbk"),
      "Full-backup payload entry name must surface the .vbk role.");
  }

  [Test, Category("HappyPath")]
  public void Reader_BookkeepsFileTypeHint_Incremental() {
    var image = BuildMinimal(tagOffset: 0, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Incremental);
    Assert.That(r.FileType, Is.EqualTo(VeeamFileType.Incremental));
    var payload = r.Entries.First(e => e.Name != "metadata.ini");
    Assert.That(payload.Name, Does.Contain("vib"),
      "Incremental-backup payload entry name must surface the .vib role.");
  }

  [Test, Category("HappyPath")]
  public void Reader_BookkeepsFileTypeHint_ReverseIncremental() {
    var image = BuildMinimal(tagOffset: 0, payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.ReverseIncremental);
    Assert.That(r.FileType, Is.EqualTo(VeeamFileType.ReverseIncremental));
    var payload = r.Entries.First(e => e.Name != "metadata.ini");
    Assert.That(payload.Name, Does.Contain("vrb"),
      "Reverse-incremental-backup payload entry name must surface the .vrb role.");
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_DocumentsAllThreeFileTypes() {
    using var ms = new MemoryStream(BuildMinimal(tagOffset: 64, payloadLen: 64));
    using var r = new VeeamReader(ms);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    var lower = ini.ToLowerInvariant();
    Assert.That(lower, Does.Contain("stage=0"), "metadata.ini must pin stage=0.");
    Assert.That(lower, Does.Contain("parse_status=detection-only"));
    Assert.That(lower, Does.Contain(".vbk"));
    Assert.That(lower, Does.Contain(".vib"));
    Assert.That(lower, Does.Contain(".vrb"));
    Assert.That(lower, Does.Contain("magic_tag=veeam"));
    Assert.That(lower, Does.Contain("magic_offset=64"),
      "metadata.ini must surface the discovered VEEAM tag offset for diagnostics.");
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_NamesUnsupportedScopes() {
    // Stage-0 metadata.ini must enumerate the R/O-promotion blockers so
    // end-users see them without reading source. Pin the structural
    // reasons (CBT chain replay, dedup, encryption, missing spec).
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 64));
    using var r = new VeeamReader(ms);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data).ToLowerInvariant();
    Assert.That(ini, Does.Contain("ro_promotion=blocked"));
    Assert.That(ini, Does.Contain("cbt"), "metadata.ini must call out CBT chain replay.");
    Assert.That(ini, Does.Contain("dedup"), "metadata.ini must call out deduplication.");
    Assert.That(ini, Does.Contain("encryption").Or.Contain("aes"),
      "metadata.ini must call out encryption as a blocker.");
    Assert.That(ini, Does.Contain("spec"),
      "metadata.ini must call out the missing published spec.");
    Assert.That(ini, Does.Contain(".vbm"),
      "metadata.ini must call out the companion .vbm metadata index.");
    Assert.That(ini, Does.Contain("treatment=stage 0 confirmed"));
  }

  [Test, Category("HappyPath")]
  public void ScanWindow_ConstantIsFourKilobytes() {
    // Pin the scan window so a refactor that silently shrinks it (and
    // breaks detection on real-world Veeam files whose tag sits beyond
    // a few hundred bytes) fails loudly.
    Assert.That(VeeamReader.ScanWindow, Is.EqualTo(4096));
  }
}
