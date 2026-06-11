#pragma warning disable CS1591
namespace Compression.Tests.Ufs;

// Throwaway harness: materialises a UFS1 image for external fsck_ffs verification.
[TestFixture]
public class UfsImageDumpTests {
  [Test, Category("Manual"), Ignore("Manual fsck_ffs verification harness; writes /tmp/ufs_verify.img on demand.")]
  public void DumpForQemu() {
    var w = new FileSystem.Ufs.UfsWriter();
    w.AddFile("hello.txt", "hello world\n"u8.ToArray());
    w.AddFile("readme.md", "# readme\n"u8.ToArray());
    w.AddFile("docs/guide.txt", "guide contents\n"u8.ToArray());
    using var fs = File.Create("/tmp/ufs_verify.img");
    w.WriteTo(fs);
  }
}
