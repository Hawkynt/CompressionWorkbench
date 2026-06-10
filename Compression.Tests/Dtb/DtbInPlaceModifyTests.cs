using Compression.Registry;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

/// <summary>
/// Locks the honest demotion of FileFormat.Dtb: the descriptor must NOT
/// advertise <see cref="FormatCapabilities.CanModify"/> and the Description
/// must name the specific spec-level reason that blocks in-place mutation.
/// The companion read tests live in <see cref="DtbTests"/>.
///
/// Why DTB stays R-only: the 40-byte FDT header at offset 0 records
/// <c>totalsize</c>, <c>off_dt_struct</c>, <c>off_dt_strings</c>,
/// <c>size_dt_struct</c>, <c>size_dt_strings</c>. Adding/removing any
/// property mutates the struct or strings block, which cascades through every
/// downstream offset and the four header size/offset fields. Mutating those
/// fields in place is a rebuild, not an in-place splice — so promoting to
/// CanModify would mis-advertise the surface.
/// </summary>
[TestFixture]
public class DtbInPlaceModifyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new DtbFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Description_NamesTheBlockingFdtHeaderFields() {
    var desc = new DtbFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("totalsize"));
    Assert.That(desc.Description, Does.Contain("off_dt_strings"));
    Assert.That(desc.Description, Does.Contain("off_dt_struct"));
    Assert.That(desc.Description, Does.Contain("rebuild"));
  }
}
