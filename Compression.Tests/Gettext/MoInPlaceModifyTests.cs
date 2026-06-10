using Compression.Registry;
using FileFormat.Gettext;

namespace Compression.Tests.Gettext;

/// <summary>
/// Locks the honest demotion of FileFormat.Gettext (MO catalog): the
/// descriptor must NOT advertise <see cref="FormatCapabilities.CanModify"/>
/// and the Description must name the specific spec-level reason that blocks
/// in-place mutation. The companion read tests live in <see cref="GettextTests"/>.
///
/// Why MO stays R-only: the 28-byte header records numStrings,
/// origTableOffset, transTableOffset. Both string descriptor tables (an
/// 8-byte (len, off) pair per message) sit BEFORE the key/value pools.
/// Adding a message extends both tables by 16 bytes total, which shifts the
/// pools downward and invalidates every (off) field already written. Removing
/// shrinks the tables and cascades the same way. That's a full rebuild, not
/// an in-place splice — so promoting to CanModify would mis-advertise the surface.
/// </summary>
[TestFixture]
public class MoInPlaceModifyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new MoFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Description_NamesTheBlockingMoTableLayout() {
    var desc = new MoFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("numStrings"));
    Assert.That(desc.Description, Does.Contain("descriptor tables"));
    Assert.That(desc.Description, Does.Contain("pools"));
    Assert.That(desc.Description, Does.Contain("rebuild"));
  }
}
