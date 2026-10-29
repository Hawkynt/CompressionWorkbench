using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.Msu;

namespace Compression.Tests.Msu;

[TestFixture]
public class MsuTests {

  [Test]
  public void GivenMsuPath_WhenDetected_ThenMsuIsChosen()
    => Assert.That(FormatDetector.DetectByExtension("update.msu"), Is.EqualTo(FormatDetector.Format.Msu));

  [Test]
  public void GivenBytesThatAreNeitherCabNorWim_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var entries = new MsuFormatDescriptor().List(new MemoryStream(new byte[64]), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenEmptyStream_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var entries = new MsuFormatDescriptor().List(new MemoryStream(), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  /// <summary>
  /// The real package from the Microsoft Update Catalog (see ReferenceVectors/README.md; set
  /// CWB_MSU_FIXTURES): every part must match what 7-Zip extracts (oracle.tsv).
  /// </summary>
  [Test]
  public void GivenRealMsu_WhenExtracted_ThenEveryPartMatches7Zip() {
    var dir = Environment.GetEnvironmentVariable("CWB_MSU_FIXTURES");
    var path = dir == null ? null : Path.Combine(dir, "windows6.0-kb2999226-x86.msu");
    if (path == null || !File.Exists(path))
      Assert.Ignore("CWB_MSU_FIXTURES not set.");

    var oracle = File.ReadAllLines(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Msu", "ReferenceVectors", "oracle.tsv"))
      .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToDictionary(f => f[1], f => f[3]);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb-msu-" + Guid.NewGuid().ToString("N"));
    try {
      using (var fs = File.OpenRead(path!))
        new MsuFormatDescriptor().Extract(fs, outDir, null, null);
      var got = Directory.GetFiles(outDir).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
      Assert.That(got, Is.EquivalentTo(oracle));
    } finally {
      if (Directory.Exists(outDir))
        Directory.Delete(outDir, recursive: true);
    }
  }
}
