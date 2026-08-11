using Compression.Registry;
using FileFormat.Ghost;

namespace Compression.Tests.Ghost;

/// <summary>
/// Stage 0 acceptance gate for <see cref="GhostFormatDescriptor"/>.
/// Norton / Symantec Ghost is treated as detection-only — the format is
/// closed proprietary across all generations (4-7 / 8-9 / 10-12), no
/// version-stable magic byte sequence is publicly documented, and no
/// open-source reader covers any single generation end-to-end. These
/// tests pin the deferral wording and the Stage-0 R/O surface; any future
/// promotion attempt must first remove the deferral assertions.
/// </summary>
[TestFixture]
public class GhostDetectionTests {

  /// <summary>
  /// Synthetic minimal Ghost image — just enough leading bytes for the
  /// reader's peek-and-classify path to run. No real Ghost compatibility
  /// implied; the Stage-0 reader only surfaces metadata + raw bytes.
  /// </summary>
  private static byte[] BuildMinimal(int payloadLen = 128, byte leadByte0 = 0xFE, byte leadByte1 = 0xEF) {
    var image = new byte[8 + payloadLen];
    image[0] = leadByte0;
    image[1] = leadByte1;
    for (var i = 2; i < 8; i++) image[i] = (byte)i;
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_HasExpectedSurface() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ghost"));
    Assert.That(d.DisplayName, Does.Contain("Ghost"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".gho"));
    Assert.That(d.Extensions, Is.EquivalentTo(new[] { ".gho", ".ghs" }));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  /// <summary>
  /// Given-When-Then: GIVEN the closed proprietary nature of Ghost,
  /// WHEN the descriptor is inspected, THEN it MUST NOT advertise any
  /// magic byte signature — no version-stable magic exists across the
  /// 4-7 / 8-9 / 10-12 generations, and a guess would cause false positives.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Detector_DoesNotClaimAnyMagic() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndRawImageEntries() {
    var d = new GhostFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "ghost-image.gho.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Reader_FlagsSpannedSegmentRoleWhenRequested() {
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 128));
    var r = new GhostReader(ms, isSpannedSegment: true);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("ghost-image.ghs.bin"));
    Assert.That(names, Does.Not.Contain("ghost-image.gho.bin"));
    Assert.That(r.LikelySpannedSegment, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ClassifiesLegacyLeadingBytesAsHint() {
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 64, leadByte0: 0xFE, leadByte1: 0xEF));
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PossiblyLegacy4To7));
  }

  [Test, Category("HappyPath")]
  public void Reader_ClassifiesSymcLeadingBytesAsModernHint() {
    var image = BuildMinimal(payloadLen: 64);
    image[0] = (byte)'S'; image[1] = (byte)'Y'; image[2] = (byte)'M'; image[3] = (byte)'C';
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PossiblyModern8Plus));
  }

  [Test, Category("HappyPath")]
  public void Reader_FallsBackToUnknownHintForArbitraryBytes() {
    var image = BuildMinimal(payloadLen: 32, leadByte0: 0x12, leadByte1: 0x34);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.Unknown));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new GhostFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("detection-only"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
  }

  /// <summary>
  /// Stage-0-confirmed gate: any future attempt to promote Norton Ghost past
  /// detection MUST first remove this assertion. The deferral is intentional
  /// and documented in <see cref="GhostFormatDescriptor.Description"/> and
  /// the surrounding source comment — closed proprietary Symantec format,
  /// multiple incompatible generations, no open-source reference reader,
  /// no validated corpus for safe LZ77-variant decoder synthesis.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_DocumentsPromotionDeferral() {
    var d = new GhostFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("stage-0"));
    Assert.That(desc, Does.Contain("proprietary"));
    Assert.That(desc, Does.Contain("deferred"));
    Assert.That(desc, Does.Contain("not publicly specified")
      .Or.Contain("no public spec"));
  }

  [Test, Category("Stub")]
  public void Metadata_DocumentsPromotionBlockedReason() {
    var d = new GhostFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GhostMetaTest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workDir);
    try {
      d.Extract(ms, workDir, password: null, files: ["metadata.ini"]);
      var metaPath = Path.Combine(workDir, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True, "metadata.ini must be written by Extract");
      var text = File.ReadAllText(metaPath);
      Assert.That(text, Does.Contain("stage=0"));
      Assert.That(text, Does.Contain("parse_status=detection-only"));
      Assert.That(text, Does.Contain("promotion_blocked_reason="));
      Assert.That(text, Does.Contain("format=Symantec / Norton Ghost"));
      Assert.That(text, Does.Contain("generation_hint="));
      Assert.That(text, Does.Contain("leading_bytes_hex="));
    }
    finally {
      if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsRawImageBytes() {
    var d = (IArchiveFormatOperations)new GhostFormatDescriptor();
    var image = BuildMinimal(payloadLen: 64);
    using var ms = new MemoryStream(image);
    using var stream = d.OpenEntry(ms, "ghost-image.gho.bin", password: null);
    Assert.That(stream, Is.Not.Null);
    Assert.That(stream.Length, Is.EqualTo(image.Length));
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    Assert.That(copy.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void OpenEntry_ThrowsForUnknownEntryName() {
    var d = (IArchiveFormatOperations)new GhostFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    Assert.That(() => d.OpenEntry(ms, "no-such-entry.bin", password: null),
      Throws.InstanceOf<FileNotFoundException>());
  }

  [Test, Category("BoundaryCase")]
  public void Reader_RejectsTruncatedHeader() {
    var image = new byte[] { 0xFE, 0xEF, 0x00 }; // < 8 bytes
    using var ms = new MemoryStream(image);
    var d = new GhostFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsEmptyStream() {
    using var ms = new MemoryStream([]);
    var d = new GhostFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_ThrowsOnNullStream() {
    Assert.That(() => new GhostReader(null!), Throws.InstanceOf<ArgumentNullException>());
  }
}
