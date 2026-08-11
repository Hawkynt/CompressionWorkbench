using Compression.Registry;
using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

/// <summary>
/// WORM-state contract tests for HAMMER2. The descriptor is pinned at R-only —
/// these tests fail any drive-by upgrade that adds CanCreate/CanModify before
/// the underlying copy-on-write blockref radix tree work lands.
/// </summary>
[TestFixture]
public class Hammer2WormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_StaysReadOnly_NoCanCreate_NoCanModify() {
    var d = new Hammer2FormatDescriptor();
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>(),
      "HAMMER2 must not advertise IArchiveCreatable — emitting a fresh image needs " +
      "four redundant 64 KB volume-data sectors with consistent generation numbers, " +
      "a COW blockref radix tree with per-block xxHash64 checksums, per-superroot PFS " +
      "clusters with their own sub-radix trees, and a real freemap that survives COW " +
      "promotion rules. See Description for the deferred scope.");
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsWriterGapExplicitly() {
    var d = new Hammer2FormatDescriptor();
    Assert.That(d.Description, Does.Contain("copy-on-write blockref"),
      "Description must name the COW blockref radix tree gap.");
    Assert.That(d.Description, Does.Contain("xxHash64"),
      "Description must call out the per-block checksum requirement.");
    Assert.That(d.Description, Does.Contain("Multi-week effort"),
      "Description must flag the effort honestly.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReadCapabilities_StillIntact() {
    var d = new Hammer2FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }
}
