using System.Text;
using Compression.Registry;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
/// Stage 0 acceptance gate for <see cref="AcronisFormatDescriptor"/>:
/// pins the detection magic, surface entry shape, and honest
/// "detection-only" Description for both <c>.tib</c> (classic) and
/// <c>.tibx</c> (Acronis Cyber Engine) variants.
/// </summary>
[TestFixture]
public class AcronisDetectionTests {

  private static byte[] BuildClassic(int payloadLen = 128, int tagOffset = 0) {
    var image = new byte[Math.Max(64, tagOffset + 10 + 4 + payloadLen)];
    Encoding.ASCII.GetBytes("AcronisFLM").CopyTo(image.AsSpan(tagOffset, 10));
    // Trailing word after the tag for the diagnostic dump.
    image[tagOffset + 10] = 0xDE;
    image[tagOffset + 11] = 0xAD;
    image[tagOffset + 12] = 0xBE;
    image[tagOffset + 13] = 0xEF;
    for (var i = 0; i < payloadLen; i++) image[tagOffset + 14 + i] = (byte)(i & 0xFF);
    return image;
  }

  private static byte[] BuildCyberEngine(int payloadLen = 128) {
    var image = new byte[Math.Max(64, 4 + 4 + payloadLen)];
    Encoding.ASCII.GetBytes("TIBX").CopyTo(image.AsSpan(0, 4));
    image[4] = 0xCA;
    image[5] = 0xFE;
    image[6] = 0xBA;
    image[7] = 0xBE;
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_RegistersBothMagicAndExtensions() {
    var d = new AcronisFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Acronis"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".tib"));
    Assert.That(d.Extensions, Does.Contain(".tib"));
    Assert.That(d.Extensions, Does.Contain(".tibx"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("AcronisFLM"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("TIBX"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_Classic_ReturnsTwoEntries() {
    var d = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(BuildClassic(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "tib-image.bin" }));
  }

  [Test, Category("HappyPath")]
  public void List_CyberEngine_ReturnsTwoEntries() {
    var d = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(BuildCyberEngine(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "tib-image.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new AcronisFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"),
      $"Acronis Description must flag Stage 0 honestly. Got: '{d.Description}'.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_NamesUnsupportedSurfaces() {
    // Honest Stage-0 doctrine: the Description must surface exactly WHAT is
    // unsupported so consumers don't file R/O-promotion tickets without
    // understanding the structural blockers. The task brief pins encryption,
    // sector reconstruction, and file index walk as the three required call-outs.
    var d = new AcronisFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("encryption"),
      "Acronis Description must call out the AES encryption blocker.");
    Assert.That(desc, Does.Contain("sector reconstruction").Or.Contain("block-level allocation bitmap"),
      "Acronis Description must call out sector reconstruction as unsupported.");
    Assert.That(desc, Does.Contain("file index"),
      "Acronis Description must call out file index walk as unsupported.");
    Assert.That(desc, Does.Contain("proprietary").Or.Contain("spec"),
      "Acronis Description must call out the proprietary / no-public-spec status.");
    Assert.That(desc, Does.Contain("acronis cyber engine"),
      "Acronis Description must explicitly name the Acronis Cyber Engine container (the 2018+ .tibx engine).");
  }

  [Test, Category("Stub")]
  public void Metadata_NamesStage0Rationale() {
    // Stage-0 metadata.ini must enumerate the structural blockers so end users
    // see them without reading source. Pin the doctrine fields (parse_status,
    // stage, treatment, vendor, ro_promotion) and the topic keywords
    // (encryption, sector, file index, spec) — not the surface phrasing.
    using var ms = new MemoryStream(BuildClassic(payloadLen: 64));
    var reader = new AcronisReader(ms);
    var meta = reader.Entries.First(e => e.Name == "metadata.ini");
    var ini = Encoding.UTF8.GetString(meta.Data).ToLowerInvariant();
    Assert.That(ini, Does.Contain("parse_status=detection-only"));
    Assert.That(ini, Does.Contain("stage=0"));
    Assert.That(ini, Does.Contain("treatment=stage 0 confirmed"));
    Assert.That(ini, Does.Contain("vendor=acronis"));
    Assert.That(ini, Does.Contain("ro_promotion=blocked"));
    Assert.That(ini, Does.Contain("encryption"));
    Assert.That(ini, Does.Contain("sector reconstruction"));
    Assert.That(ini, Does.Contain("file index"));
    Assert.That(ini, Does.Contain("no public on-disk specification"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Classic_DetectsTagAtOffsetZero() {
    using var ms = new MemoryStream(BuildClassic(payloadLen: 16, tagOffset: 0));
    using var r = new AcronisReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("tib"));
    Assert.That(r.MagicOffset, Is.EqualTo(0));
    Assert.That(r.TrailingWord, Is.EqualTo(0xEFBEADDEu)); // LE read of DE AD BE EF
  }

  [Test, Category("BoundaryCase")]
  public void Reader_Classic_DetectsTagWithinScanWindow() {
    // Real-world .tib files have the AcronisFLM tag shifted away from offset 0
    // depending on the engine generation. The reader must scan within
    // ScanWindow bytes — not refuse anything that isn't at offset 0.
    using var ms = new MemoryStream(BuildClassic(payloadLen: 16, tagOffset: 64));
    using var r = new AcronisReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("tib"));
    Assert.That(r.MagicOffset, Is.EqualTo(64));
  }

  [Test, Category("HappyPath")]
  public void Reader_CyberEngine_DetectsTibxTag() {
    using var ms = new MemoryStream(BuildCyberEngine(payloadLen: 16));
    using var r = new AcronisReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Variant, Is.EqualTo("tibx"));
    Assert.That(r.MagicOffset, Is.EqualTo(0));
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsImageWithoutAnyTag() {
    // Without either wrapper tag, the reader must refuse — a stray binary
    // with neither AcronisFLM nor TIBX in the first 512 bytes is not an
    // Acronis image and must not silently pass detection.
    var bogus = new byte[256];
    for (var i = 0; i < bogus.Length; i++) bogus[i] = (byte)(i ^ 0x5A);
    using var ms = new MemoryStream(bogus);
    Assert.That(() => _ = new AcronisReader(ms), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsImageTooSmallForAnyTag() {
    // File shorter than the classic tag (10 bytes) cannot possibly carry it.
    var tiny = new byte[4];
    using var ms = new MemoryStream(tiny);
    Assert.That(() => _ = new AcronisReader(ms), Throws.InstanceOf<InvalidDataException>());
  }
}
