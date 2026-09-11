#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Nwfs;

namespace Compression.Tests.Nwfs386;

[TestFixture]
public class Nwfs386DetectionTests {

  [Test, Category("ErrorHandling")]
  public void Detector_DoesNotTreatArbitraryNetWPrefixAsNwfs386() {
    var image = new byte[4096];
    "NetW"u8.CopyTo(image);

    var fmt = FormatDetector.DetectByMagic(image);

    Assert.That(fmt.ToString(), Is.Not.EqualTo("Nwfs386").IgnoreCase,
      "The old four-byte NetW prefix had no authoritative NWFS386 basis and must not claim the format.");
  }

  [Test, Category("HappyPath")]
  public void HotfixMagic_IsOwnedBySharedNwfsDetector_NotDuplicatedByAlias() {
    var writer = new NwfsWriter();
    writer.AddFile("HELLO.TXT", "hello"u8.ToArray());
    var image = writer.Build();

    var fmt = FormatDetector.DetectByMagic(image);

    Assert.That(fmt.ToString(), Is.EqualTo("Nwfs").IgnoreCase,
      "Nwfs386 is an extension-routed compatibility id; the shared NWFS descriptor owns HOTFIX00 magic routing.");
  }
}
