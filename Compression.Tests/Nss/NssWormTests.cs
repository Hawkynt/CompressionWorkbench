using Compression.Registry;
using FileSystem.Nss;

namespace Compression.Tests.Nss;

/// <summary>
/// WORM-state contract tests for NSS. The descriptor is pinned at R-only with
/// anchor-detection-only metadata — these tests fail any drive-by upgrade that
/// adds CanCreate/CanModify before Novell's on-disk format is publicly
/// documented (it never has been, as far as we know).
/// </summary>
[TestFixture]
public class NssWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_StaysReadOnly_NoCanCreate_NoCanModify() {
    var d = new NssFormatDescriptor();
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>(),
      "NSS must not advertise IArchiveCreatable — Novell's on-disk format was never " +
      "publicly documented, so we cannot emit a NetWare-mountable pool. The 'Beast' " +
      "object record layout, per-volume B-tree node format, and trustee ACL tree " +
      "encoding are all proprietary. See Description for the deferred scope.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsWriterGapExplicitly() {
    var d = new NssFormatDescriptor();
    Assert.That(d.Description, Does.Contain("never publicly documented"),
      "Description must call out the missing public spec.");
    Assert.That(d.Description, Does.Contain("Beast"),
      "Description must name the proprietary object record layout we can't reproduce.");
    Assert.That(d.Description, Does.Contain("Pinned at"),
      "Description must explicitly state the R-only pin.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReadCapabilities_StillIntact() {
    var d = new NssFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }
}
