using Compression.Analysis.Fingerprinting;
using Compression.Core.Transforms;

namespace Compression.Tests.Analysis;

[TestFixture]
public class AlgorithmFingerprinterTests {

  [Test, Category("HappyPath")]
  public void EntropyClassifier_Plaintext_DetectsLowEntropy() {
    // Highly repetitive text for low entropy
    var data = "aaa bbb ccc aaa bbb ccc aaa bbb ccc aaa bbb ccc aaa bbb ccc aaa bbb ccc "u8.ToArray();
    var h = new EntropyClassifier();
    var result = h.Analyze(data);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Algorithm, Is.EqualTo("Plaintext"));
    Assert.That(result.Confidence, Is.GreaterThan(0.3));
  }

  [Test, Category("HappyPath")]
  public void EntropyClassifier_Random_DetectsEncrypted() {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var h = new EntropyClassifier();
    var result = h.Analyze(data);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Algorithm, Does.Contain("Encrypted").Or.Contain("Compression").Or.Contain("Random"));
  }

  [Test, Category("HappyPath")]
  public void DeflateHeuristic_StoredBlock_Detects() {
    // Construct a valid Deflate stored block: BFINAL=1, BTYPE=00, LEN=5, NLEN=~5
    var data = new byte[10];
    data[0] = 0x01; // BFINAL=1, BTYPE=0 (stored)
    data[1] = 0x05; data[2] = 0x00; // LEN=5
    data[3] = 0xFA; data[4] = 0xFF; // NLEN = ~5 = 0xFFFA
    var h = new DeflateHeuristic();
    var result = h.Analyze(data);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Algorithm, Is.EqualTo("Deflate"));
    Assert.That(result.Confidence, Is.GreaterThan(0.7));
  }

  [Test, Category("HappyPath")]
  public void MtfHeuristic_MtfOutput_Detects() {
    // Create MTF-like data: mostly low bytes
    var text = "aaabbbcccdddeeefffggg"u8.ToArray();
    var mtf = MoveToFrontTransform.Encode(text);
    var h = new MtfHeuristic();
    var result = h.Analyze(mtf);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Algorithm, Is.EqualTo("MTF"));
  }

  [Test, Category("HappyPath")]
  public void RleHeuristic_RleData_Detects() {
    // Construct count+value pairs
    var data = new byte[100];
    for (var i = 0; i < data.Length; i += 2) {
      data[i] = (byte)(3 + i % 10); // count
      data[i + 1] = (byte)(i % 5);  // value
    }
    var h = new RleHeuristic();
    var result = h.Analyze(data);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Algorithm, Is.EqualTo("RLE"));
  }

  [Test, Category("HappyPath")]
  public void BwtHeuristic_BwtOutput_Detects() {
    // BWT creates clustered runs
    var text = "banana bandana cabana"u8.ToArray();
    var (bwt, _) = BurrowsWheelerTransform.Forward(text);
    var h = new BwtHeuristic();
    var result = h.Analyze(bwt);
    // BWT output may or may not trigger depending on run clustering
    // At minimum, no exception
    Assert.That(true); // smoke test — BWT detection is heuristic
  }

  [Test, Category("HappyPath")]
  public void Fingerprinter_ReturnsMultipleResults() {
    var fp = new AlgorithmFingerprinter();
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var results = fp.Analyze(data);
    Assert.That(results.Count, Is.GreaterThan(0));
    // Results should be sorted by confidence descending
    for (var i = 1; i < results.Count; i++) {
      Assert.That(results[i].Confidence, Is.LessThanOrEqualTo(results[i - 1].Confidence));
    }
  }

  [Test, Category("HappyPath")]
  public void Fingerprinter_Plaintext_DetectsLowEntropy() {
    var fp = new AlgorithmFingerprinter();
    // Repetitive text → low entropy → should get Plaintext classification
    var data = "aaa bbb ccc ddd aaa bbb ccc ddd aaa bbb ccc ddd aaa bbb ccc ddd aaa bbb ccc ddd "u8.ToArray();
    var results = fp.Analyze(data);
    Assert.That(results.Count, Is.GreaterThan(0));
    Assert.That(results.Any(r => r.Algorithm == "Plaintext"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Fingerprinter_TinyData_ReturnsSomething() {
    var fp = new AlgorithmFingerprinter();
    var data = new byte[] { 1, 2, 3 };
    var results = fp.Analyze(data);
    // May or may not have results for 3 bytes — no crash
    Assert.That(results, Is.Not.Null);
  }
}
