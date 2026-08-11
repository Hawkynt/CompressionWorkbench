using Compression.Registry;
using FileSystem.Hammer;

namespace Compression.Tests.Hammer;

/// <summary>
/// WORM-state contract tests for HAMMER. The descriptor is pinned at R-only —
/// these tests fail any drive-by upgrade that adds CanCreate/CanModify before
/// the underlying cluster B-tree + zone blockmap + undo-fifo work lands.
/// </summary>
[TestFixture]
public class HammerWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_StaysReadOnly_NoCanCreate_NoCanModify() {
    var d = new HammerFormatDescriptor();
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>(),
      "HAMMER1 must not advertise IArchiveCreatable — emitting a fresh image needs " +
      "the cluster B-tree (zone blockmap → cluster → inode → records with hammer_crc_t " +
      "CRCs across every node) plus undo-fifo bootstrapping, which require a running " +
      "DragonFly BSD instance to validate. See Description for the deferred scope.");
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>(),
      "HAMMER1 must not advertise IArchiveModifiable until the same scaffold is built.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsWriterGapExplicitly() {
    var d = new HammerFormatDescriptor();
    Assert.That(d.Description, Does.Contain("cluster B-tree"),
      "Description must call out the cluster B-tree gap so a future agent can spot the deferred work.");
    Assert.That(d.Description, Does.Contain("Multi-week effort"),
      "Description must flag the effort honestly.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReadCapabilities_StillIntact() {
    var d = new HammerFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }
}
