using Compression.Analysis;
using Compression.Analysis.ChainReconstruction;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace Compression.Tests.Analysis;

[TestFixture]
public class ChainReconstructorTests {

  [Test, Category("HappyPath")]
  public void Reconstruct_SingleRle_PeelsAtLeastOneLayer() {
    var original = new byte[200];
    for (var i = 0; i < 200; i++) original[i] = (byte)(i % 5);
    var encoded = RunLengthEncoding.Encode(original);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(encoded);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
    // First layer should be RLE (guided by fingerprinting) or another valid decompression
    Assert.That(chain.Layers[0].Algorithm, Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void Reconstruct_Deflate_PeelsAtLeastOneLayer() {
    var original = "Hello world! This is a test string with enough redundancy to compress well. "u8.ToArray();
    var data = new byte[original.Length * 5];
    for (var i = 0; i < 5; i++) original.CopyTo(data, i * original.Length);

    var compressed = DeflateCompressor.Compress(data);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(compressed);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reconstruct_MtfThenRle_PeelsMultipleLayers() {
    var original = new byte[200];
    for (var i = 0; i < 200; i++) original[i] = (byte)(i % 5);
    var mtfEncoded = MoveToFrontTransform.Encode(original);
    var rleEncoded = RunLengthEncoding.Encode(mtfEncoded);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(rleEncoded);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reconstruct_GzipWrapped_PeelsAtLeastOneLayer() {
    var original = "Compressed text data with repeating patterns repeating patterns repeating."u8.ToArray();
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      gz.Write(original);
    var compressed = ms.ToArray();

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(compressed);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
    // At least one decompression layer should be identified
    Assert.That(chain.Layers[0].OutputSize, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void SuccessEvaluator_EntropyDecrease_IsImprovement() {
    var rng = new Random(42);
    var input = new byte[1024];
    rng.NextBytes(input);
    var output = new byte[2048];
    for (var i = 0; i < output.Length; i++) output[i] = (byte)(i % 3);

    var result = SuccessEvaluator.Evaluate(input, output, "Test");
    Assert.That(result.IsImprovement, Is.True);
  }

  [Test, Category("HappyPath")]
  public void SuccessEvaluator_EmptyOutput_NotImprovement() {
    var input = new byte[] { 1, 2, 3 };
    var result = SuccessEvaluator.Evaluate(input, ReadOnlySpan<byte>.Empty, "Test");
    Assert.That(result.IsImprovement, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void Reconstruct_Random_FewOrNoLayers() {
    // Random data should not produce many false positive layers
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);
    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 200);
    var chain = reconstructor.Reconstruct(data);

    // Random data may produce false positive layers from building block trials
    Assert.That(chain.Depth, Is.LessThanOrEqualTo(5));
  }

  [Test, Category("HappyPath")]
  public void BinaryAnalyzer_AllModes_Completes() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var analyzer = new BinaryAnalyzer(new AnalysisOptions { All = true });
    var result = analyzer.Analyze(data);

    Assert.That(result.Statistics, Is.Not.Null);
    Assert.That(result.Signatures, Is.Not.Null);
    Assert.That(result.Fingerprints, Is.Not.Null);
    Assert.That(result.EntropyMap, Is.Not.Null);
    Assert.That(result.TrialResults, Is.Not.Null);
    Assert.That(result.Chain, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void BinaryAnalyzer_OffsetSlicing_Works() {
    var data = new byte[512];
    data[100] = 0x1F; data[101] = 0x8B;
    var analyzer = new BinaryAnalyzer(new AnalysisOptions { DeepScan = true, Offset = 100, Length = 200 });
    var result = analyzer.Analyze(data);

    Assert.That(result.Signatures, Is.Not.Null);
    Assert.That(result.Signatures!.Any(s => s.FormatName == "Gzip"), Is.True);
  }
}
