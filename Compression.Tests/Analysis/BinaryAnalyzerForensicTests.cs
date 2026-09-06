using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public sealed class BinaryAnalyzerForensicTests {

  [Test, Category("Integration")]
  public void Analyze_DeepScan_UsesPackageFormatDetection() {
    const int imageStart = 137;
    var data = new byte[512];
    "farbfeld"u8.CopyTo(data.AsSpan(imageStart));

    var analyzer = new BinaryAnalyzer(new AnalysisOptions {
      DeepScan = true,
      MaxScanResults = 500,
      HeaderProbeAlignment = 1,
    });

    var result = analyzer.Analyze(data);

    Assert.That(result.Signatures, Is.Not.Null);
    Assert.That(result.Signatures!.Any(hit => hit.FormatName == "Farbfeld" && hit.Offset == imageStart), Is.True);
  }
}
