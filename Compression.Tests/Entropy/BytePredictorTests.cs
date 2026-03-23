using System.Text;
using Compression.Core.Entropy.ContextModeling;

namespace Compression.Tests.Entropy;

[TestFixture]
public class BytePredictorTests {
  [Category("EdgeCase")]
  [Test]
  public void Predict_NoContext_ReturnsUniform() {
    var pred = new BytePredictor(4);
    var probs = pred.Predict([]);
    Assert.That(probs.Length, Is.EqualTo(256));
    // All probabilities should be equal (uniform)
    Assert.That(probs[0], Is.EqualTo(probs[128]));
  }

  [Category("HappyPath")]
  [Test]
  public void PredictByte_AfterTraining_ReturnsMostLikely() {
    var pred = new BytePredictor(1);
    // After 'A', always 'B'
    for (var i = 0; i < 20; ++i)
      pred.Update([(byte)'A'], (byte)'B');

    var predicted = pred.PredictByte([(byte)'A']);
    Assert.That(predicted, Is.EqualTo((byte)'B'));
  }

  [Category("HappyPath")]
  [Test]
  public void Predict_HigherOrder_MoreSpecific() {
    var pred = new BytePredictor(2);
    // After "AB" → 'C', after "XB" → 'Y'
    for (var i = 0; i < 20; ++i) {
      pred.Update([(byte)'A', (byte)'B'], (byte)'C');
      pred.Update([(byte)'X', (byte)'B'], (byte)'Y');
    }

    var afterAB = pred.PredictByte([(byte)'A', (byte)'B']);
    var afterXB = pred.PredictByte([(byte)'X', (byte)'B']);
    Assert.That(afterAB, Is.EqualTo((byte)'C'));
    Assert.That(afterXB, Is.EqualTo((byte)'Y'));
  }

  [Category("Boundary")]
  [Test]
  public void Update_RescalesWhenCountsHigh() {
    var pred = new BytePredictor(1);
    // Pump in many updates to trigger rescaling
    for (var i = 0; i < 5000; ++i)
      pred.Update([0], 42);
    // Should still predict correctly
    Assert.That(pred.PredictByte([0]), Is.EqualTo(42));
  }

  [Category("Exception")]
  [Test]
  public void Constructor_InvalidOrder_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => new BytePredictor(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => new BytePredictor(9));
  }

  [Category("HappyPath")]
  [Test]
  public void Predict_FallsBackToLowerOrder() {
    var pred = new BytePredictor(3);
    // Only train order-1 context (byte 'A')
    for (var i = 0; i < 20; ++i)
      pred.Update([(byte)'A'], (byte)'Z');

    // Query with order-3 context that includes 'A' as the last byte
    // Should fall back to order-1 since order-3 has no data
    var predicted = pred.PredictByte([(byte)'X', (byte)'Y', (byte)'A']);
    Assert.That(predicted, Is.EqualTo((byte)'Z'));
  }

  [Category("HappyPath")]
  [Test]
  public void Predict_TextData_LearnsPatternsCorrectly() {
    var pred = new BytePredictor(4);
    var text = Encoding.ASCII.GetBytes("the the the the the the the the ");
    var context = new List<byte>();

    // Train on the text
    foreach (var b in text) {
      pred.Update(context.Count >= 4 ? context.ToArray().AsSpan(context.Count - 4, 4) : context.ToArray(), b);
      context.Add(b);
    }

    // After "the ", should predict 't'
    var predicted = pred.PredictByte([(byte)'h', (byte)'e', (byte)' ']);
    Assert.That(predicted, Is.EqualTo((byte)'t'));
  }

  [Category("HappyPath")]
  [Test]
  public void MaxOrder_ReturnsConfiguredValue() {
    var pred = new BytePredictor(6);
    Assert.That(pred.MaxOrder, Is.EqualTo(6));
  }
}
