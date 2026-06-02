using Compression.Registry;
using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

/// <summary>
/// Stage 0 acceptance gate for <see cref="GpfsFormatDescriptor"/>.
/// </summary>
[TestFixture]
public class GpfsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[8 + payloadLen];
    image[0] = 0x43; image[1] = 0x47; image[2] = 0x46; image[3] = 0x5C;
    for (var i = 0; i < payloadLen; i++) image[8 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new GpfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gpfs"));
    Assert.That(d.Extensions, Does.Contain(".gpfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x43, 0x47, 0x46, 0x5C }));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "gpfs-nsd.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new GpfsFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  /// <summary>
  /// Stage-0-confirmed gate: any future attempt to promote GPFS past detection
  /// MUST first remove this assertion. The deferral is intentional and
  /// documented in <see cref="GpfsFormatDescriptor.Description"/> and the
  /// surrounding source comment — proprietary IBM on-disk format, no public
  /// spec, no single-image content surface, no off-cluster fsck oracle.
  /// </summary>
  [Test, Category("Stub")]
  public void Description_DocumentsPromotionDeferral() {
    var d = new GpfsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("stage-0"));
    Assert.That(desc, Does.Contain("proprietary"));
    Assert.That(desc, Does.Contain("deferred"));
    // R/O promotion requires every one of these to be unblocked.
    Assert.That(desc, Does.Contain("not publicly specified")
      .Or.Contain("no public spec"));
  }

  [Test, Category("Stub")]
  public void Metadata_DocumentsPromotionBlockedReason() {
    var d = new GpfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    d.Extract(ms, TestContext.CurrentContext.WorkDirectory, password: null, files: ["metadata.ini"]);
    var metaPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "metadata.ini");
    Assert.That(File.Exists(metaPath), Is.True, "metadata.ini must be written by Extract");
    var text = File.ReadAllText(metaPath);
    Assert.That(text, Does.Contain("stage=0"));
    Assert.That(text, Does.Contain("parse_status=detection-only"));
    Assert.That(text, Does.Contain("promotion_blocked_reason="));
    File.Delete(metaPath);
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsBadMagic() {
    var image = new byte[64];
    image[0] = 0xDE; image[1] = 0xAD; image[2] = 0xBE; image[3] = 0xEF;
    using var ms = new MemoryStream(image);
    var d = new GpfsFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("BoundaryCase")]
  public void Reader_RejectsTruncatedHeader() {
    var image = new byte[] { 0x43, 0x47, 0x46 }; // 3 bytes, less than HeaderSize=8
    using var ms = new MemoryStream(image);
    var d = new GpfsFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }
}
