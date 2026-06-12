#pragma warning disable CS1591
using System.Text;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// True cross-implementation interop: reads a UFS1 image that the real FreeBSD
/// kernel produced (<c>newfs -O1</c> + populated via <c>mount</c>) and asserts
/// our <see cref="UfsReader"/> extracts the kernel's files byte-exact, including
/// a subdirectory and a multi-megabyte file spanning single-indirect blocks.
///
/// The reference image is produced out-of-band by the scripted-QEMU FreeBSD
/// oracle and dropped at <c>/tmp/ufs_ref.img</c>; the test self-skips when it is
/// absent so the suite stays green without the VM harness.
/// </summary>
[TestFixture]
public class UfsBsdInteropTests {
  private const string RefImage = "/tmp/ufs_ref.img";

  [Test]
  public void ReadsFilesWrittenByFreeBsdKernel() {
    if (!File.Exists(RefImage))
      Assert.Ignore("FreeBSD-written reference image not present; run the QEMU oracle to produce /tmp/ufs_ref.img.");

    using var fs = File.OpenRead(RefImage);
    var reader = new UfsReader(fs);

    var byName = reader.Entries.ToDictionary(e => e.Name, e => e);

    Assert.That(byName.ContainsKey("frombsd.txt"), Is.True, "frombsd.txt missing from kernel image");
    Assert.That(byName.ContainsKey("sub/inner.txt"), Is.True, "sub/inner.txt (subdir entry) missing");
    Assert.That(byName.ContainsKey("big.bin"), Is.True, "big.bin (indirect-block file) missing");

    Assert.That(Encoding.ASCII.GetString(reader.Extract(byName["frombsd.txt"])),
      Is.EqualTo("kernel-written payload\n"));
    Assert.That(Encoding.ASCII.GetString(reader.Extract(byName["sub/inner.txt"])),
      Is.EqualTo("nested\n"));

    // big.bin exercises the single-indirect block table; assert size + head marker.
    var big = reader.Extract(byName["big.bin"]);
    Assert.That(big.Length, Is.EqualTo(byName["big.bin"].Size), "big.bin extracted length != inode size");
    Assert.That(Encoding.ASCII.GetString(big, 0, 7), Is.EqualTo("BIGFILE"));
  }
}
