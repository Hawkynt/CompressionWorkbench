using Compression.Analysis;
using Compression.Analysis.Statistics;
using Compression.Analysis.Structure;

namespace Compression.Tests.Analysis;

[TestFixture]
public class IntegrationTests {

  [Test, Category("HappyPath")]
  public void BoundaryDetector_MaxRegions_Respected() {
    // Create data with many transitions
    var data = new byte[16384];
    var rng = new Random(42);
    for (var i = 0; i < data.Length; i += 512) {
      var blockLen = Math.Min(512, data.Length - i);
      if ((i / 512) % 2 == 0)
        Array.Clear(data, i, blockLen);
      else
        rng.NextBytes(data.AsSpan(i, blockLen));
    }

    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 128, maxRegions: 5);
    Assert.That(regions.Count, Is.LessThanOrEqualTo(5));
    Assert.That(regions.Sum(r => r.Length), Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void BoundaryDetector_CancellationToken_Supported() {
    var data = new byte[8192];
    new Random(42).NextBytes(data);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    Assert.Throws<OperationCanceledException>(() =>
      BoundaryDetector.DetectBoundaries(data, cancellationToken: cts.Token));
  }

  [Test, Category("HappyPath")]
  public void EntropyMap_CancellationToken_Supported() {
    var data = new byte[8192];
    new Random(42).NextBytes(data);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    Assert.Throws<OperationCanceledException>(() =>
      EntropyMap.Profile(data, 256, useBoundaryDetection: true, cancellationToken: cts.Token));
  }

  [Test, Category("HappyPath")]
  public void BinaryAnalyzer_BoundaryDetection_Option() {
    var data = new byte[8192];
    new Random(42).NextBytes(data.AsSpan(4096));

    var options = new AnalysisOptions { EntropyMap = true, BoundaryDetection = true, WindowSize = 256 };
    var analyzer = new BinaryAnalyzer(options);
    var result = analyzer.Analyze(data);

    Assert.That(result.EntropyMap, Is.Not.Null);
    Assert.That(result.EntropyMap!.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(result.EntropyMap.Sum(r => r.Length), Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void BuiltInTemplates_AllParseAndInterpret() {
    var data = new byte[100];
    new Random(42).NextBytes(data);

    foreach (var (name, source) in BuiltInTemplates.All) {
      var template = TemplateParser.Parse(source, name);
      var fields = StructureInterpreter.Interpret(template, data);
      Assert.That(fields.Count, Is.GreaterThan(0), $"Template '{name}' produced no fields");
    }
  }
}
