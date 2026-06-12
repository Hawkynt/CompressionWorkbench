using System.Text;
using FileSystem.Hammer;

namespace Compression.Tests.Hammer;

/// <summary>
/// Behaviour tests for <see cref="HammerReader"/>. The kernel-interop test reads a
/// reference image that DragonFly BSD itself formatted and populated (newfs_hammer +
/// mount + write), proving the reader parses the real kernel B-Tree layout. It is
/// skipped automatically when that reference image is absent.
/// </summary>
[TestFixture]
public class HammerReaderTests {
  private const string ReferenceImage = "/tmp/hammer_ref.img";

  [Test, Category("ErrorHandling")]
  public void Open_NonHammerImage_IsInvalidAndYieldsNoFiles() {
    var reader = HammerReader.Open(new byte[4096]);
    Assert.That(reader.Valid, Is.False);
    Assert.That(reader.ReadFiles(), Is.Empty);
  }

  [Test, Category("Interop")]
  public void ReadFiles_KernelWrittenImage_RecoversFilesAndContent() {
    if (!File.Exists(ReferenceImage))
      Assert.Ignore($"Reference image {ReferenceImage} not present (run the DragonFly oracle first).");

    var reader = HammerReader.Open(File.ReadAllBytes(ReferenceImage));
    Assert.That(reader.Valid, Is.True);

    var files = reader.ReadFiles().ToDictionary(f => f.Path, f => f.Content);

    Assert.That(files.Keys, Does.Contain("frombsd.txt"));
    Assert.That(Encoding.UTF8.GetString(files["frombsd.txt"]), Is.EqualTo("kernel-written payload\n"));

    Assert.That(files.Keys, Does.Contain("sub/inner.txt"));
    Assert.That(Encoding.UTF8.GetString(files["sub/inner.txt"]), Is.EqualTo("nested\n"));
  }
}
