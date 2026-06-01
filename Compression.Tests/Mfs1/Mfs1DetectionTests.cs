#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Mfs1;

[TestFixture]
public class Mfs1DetectionTests {

  /// <summary>
  /// MFS-1 magic is intentionally weak (two bytes plus heuristic) so it cannot
  /// reliably win <see cref="FormatDetector.DetectByMagic"/> against
  /// stronger-signature formats. The single-extension <c>.mfs</c> is also claimed
  /// by FileSystem.Mfs (Macintosh File System), which was registered first.
  /// We verify detection through the unique-to-Mfs1 <c>.mfsd</c> extension.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Detector_IdentifiesMfs1_ByExtension() {
    var tmp = Path.Combine(Path.GetTempPath(), "mfs1_" + Guid.NewGuid().ToString("N") + ".mfsd");
    var img = new byte[4096];
    img[0] = 0x00;
    img[1] = 0x80;
    File.WriteAllBytes(tmp, img);
    try {
      var fmt = FormatDetector.Detect(tmp);
      Assert.That(fmt.ToString(), Is.EqualTo("Mfs1").IgnoreCase,
        $"FormatDetector must recognise MFS-1 by .mfsd extension. Got: {fmt}");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Mfs1.Mfs1FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Mfs1"));
    Assert.That(d.Extensions, Does.Contain(".mfs"));
    Assert.That(d.Extensions, Does.Contain(".mfsd"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }
}
