using System.Text;
using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

/// <summary>
/// Behaviour tests for <see cref="Hammer2Reader"/>. The kernel-interop test reads a
/// reference image that DragonFly BSD itself formatted and populated
/// (<c>newfs_hammer2</c> + mount + write), proving the reader parses the real kernel
/// blockref layout (freemap-allocated, possibly rolled volume-header slots). It is
/// skipped automatically when that reference image is absent.
/// </summary>
[TestFixture]
public class Hammer2ReaderTests {
  private const string ReferenceImage = "/tmp/hammer2_ref.img";

  [Test, Category("ErrorHandling")]
  public void Read_NonHammer2Image_YieldsNoFiles() {
    var files = new Hammer2Reader(new byte[65536]).ReadAllFiles();
    Assert.That(files, Is.Empty);
  }

  [Test, Category("Interop")]
  public void ReadAllFiles_KernelWrittenImage_RecoversFilesAndContent() {
    if (!File.Exists(ReferenceImage))
      Assert.Ignore($"Reference image {ReferenceImage} not present (run the DragonFly oracle first).");

    var files = new Hammer2Reader(File.ReadAllBytes(ReferenceImage)).ReadAllFiles();

    Assert.That(files.Keys, Does.Contain("frombsd.txt"));
    Assert.That(Encoding.UTF8.GetString(files["frombsd.txt"]), Is.EqualTo("kernel-written payload\n"));

    Assert.That(files.Keys, Does.Contain("inner.txt"));
    Assert.That(Encoding.UTF8.GetString(files["inner.txt"]), Is.EqualTo("nested\n"));
  }
}
