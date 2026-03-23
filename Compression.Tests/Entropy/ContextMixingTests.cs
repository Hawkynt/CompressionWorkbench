using System.Text;
using Compression.Core.Entropy.ContextMixing;

namespace Compression.Tests.Entropy;

[TestFixture]
public class ContextMixingTests {
  [Category("HappyPath")]
  [Test]
  public void ContextModel_Predict_InitiallyUniform() {
    var model = new ContextModel(8);
    var p = model.Predict(0);
    // Initial: count0=1, count1=1 → p(1) ≈ 2048 (of 4096)
    Assert.That(p, Is.InRange(2000, 2100));
  }

  [Category("HappyPath")]
  [Test]
  public void ContextModel_Predict_AdaptsToZeros() {
    var model = new ContextModel(8);
    for (var i = 0; i < 100; ++i)
      model.Update(0, 0);
    var p = model.Predict(0);
    // After 100 zeros, p(1) should be very low
    Assert.That(p, Is.LessThan(200));
  }

  [Category("HappyPath")]
  [Test]
  public void ContextModel_Predict_AdaptsToOnes() {
    var model = new ContextModel(8);
    for (var i = 0; i < 100; ++i)
      model.Update(0, 1);
    var p = model.Predict(0);
    // After 100 ones, p(1) should be very high
    Assert.That(p, Is.GreaterThan(3800));
  }

  [Category("HappyPath")]
  [Test]
  public void ContextModel_DifferentContexts_Independent() {
    var model = new ContextModel(8);
    for (var i = 0; i < 50; ++i) {
      model.Update(0, 0); // context 0 sees all zeros
      model.Update(1, 1); // context 1 sees all ones
    }
    Assert.That(model.Predict(0), Is.LessThan(500));
    Assert.That(model.Predict(1), Is.GreaterThan(3500));
  }

  [Category("HappyPath")]
  [Test]
  public void ContextMixer_Predict_CombinesModels() {
    var m1 = new ContextModel(8);
    var m2 = new ContextModel(8);
    var mixer = new ContextMixer(m1, m2);

    int[] contexts = [0, 0];
    var p = mixer.Predict(contexts);
    // Initial prediction should be roughly 32768 (50%)
    Assert.That(p, Is.InRange(30000, 35000));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_SingleByte() {
    var data = new byte[] { 42 };
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_ShortText() {
    var data = Encoding.UTF8.GetBytes("Hello, World!");
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_RepetitiveData() {
    var data = new byte[500];
    Array.Fill(data, (byte)'A');
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
    // Highly compressible
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_RandomData() {
    var data = new byte[500];
    new Random(42).NextBytes(data);
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void CmCompressor_RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var compressed = CmCompressor.Compress(data);
    var decompressed = CmCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void CmCompressor_CompressesTextWell() {
    var data = Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog. " +
      "Pack my box with five dozen liquor jugs.");
    var compressed = CmCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }
}
