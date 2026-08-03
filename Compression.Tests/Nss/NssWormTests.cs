using Compression.Registry;
using FileSystem.Nss;

namespace Compression.Tests.Nss;

/// <summary>
/// What NSS may and may not claim. Writing a container of our own is one thing;
/// claiming to write, or to edit, a pool NetWare would mount is another, and
/// these tests fail any drive-by upgrade that starts claiming the second.
/// </summary>
[TestFixture]
public class NssWormTests {

  /// <summary>
  /// The container this writes is its own, documented as such, and carries the
  /// anchors so detection is unchanged. What must stay unclaimed is editing a
  /// pool in place: that would need the object tree Novell never published.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Descriptor_WritesItsOwnContainerButNeverEditsAPool() {
    var d = new NssFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "NSS writes a container of its own, so it says so.");
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>(),
      "NSS must not advertise IArchiveModifiable: editing a pool in place would need the " +
      "object record layout, the per-volume B-tree and the trustee ACL tree, none of which " +
      "Novell ever published.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
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
