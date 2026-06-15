using Compression.Core.Entropy.ContextMixing;

namespace Compression.Tests.ContextMixing;

/// <summary>
/// Unit coverage for the individual context-mixing primitives:
/// the logistic transforms, the adaptive bit model, the logistic mixer and the APM.
/// </summary>
[TestFixture]
public class CmComponentTests {
  [Test]
  [Category("HappyPath")]
  public void Logistic_StretchSquash_AreInverseAroundHalf() {
    // squash(stretch(p)) should return p for a mid-range probability.
    const int p = 2048;
    var s = Logistic.Stretch(p);
    Assert.That(s, Is.EqualTo(0).Within(2)); // logit of 0.5 is 0
    Assert.That(Logistic.Squash(s), Is.EqualTo(p).Within(16));
  }

  [Test]
  [Category("Boundary")]
  public void Logistic_Stretch_IsMonotonic() {
    var prev = int.MinValue;
    for (var p = 0; p < Logistic.ProbabilityScale; ++p) {
      var s = Logistic.Stretch(p);
      Assert.That(s, Is.GreaterThanOrEqualTo(prev));
      prev = s;
    }
  }

  [Test]
  [Category("Boundary")]
  public void Logistic_Squash_ClampsExtremes() {
    Assert.That(Logistic.Squash(Logistic.MinStretch - 100), Is.EqualTo(1));
    Assert.That(Logistic.Squash(Logistic.MaxStretch + 100), Is.EqualTo(Logistic.ProbabilityScale - 1));
  }

  [Test]
  [Category("HappyPath")]
  public void ContextModel_InitiallyUniform() {
    var model = new ContextModel(8);
    Assert.That(model.Predict(0), Is.InRange(2000, 2100));
  }

  [Test]
  [Category("HappyPath")]
  public void ContextModel_AdaptsToZeros() {
    var model = new ContextModel(8);
    for (var i = 0; i < 100; ++i)
      model.Update(0, 0);
    Assert.That(model.Predict(0), Is.LessThan(200));
  }

  [Test]
  [Category("HappyPath")]
  public void ContextModel_AdaptsToOnes() {
    var model = new ContextModel(8);
    for (var i = 0; i < 100; ++i)
      model.Update(0, 1);
    Assert.That(model.Predict(0), Is.GreaterThan(3800));
  }

  [Test]
  [Category("HappyPath")]
  public void ContextModel_DifferentContextsIndependent() {
    var model = new ContextModel(8);
    for (var i = 0; i < 50; ++i) {
      model.Update(0, 0);
      model.Update(1, 1);
    }
    Assert.That(model.Predict(0), Is.LessThan(500));
    Assert.That(model.Predict(1), Is.GreaterThan(3500));
  }

  [Test]
  [Category("HappyPath")]
  public void Mixer_InitialPredictionNearHalf() {
    var mixer = new ContextMixer(new ContextModel(8), new ContextModel(8));
    int[] contexts = [0, 0];
    Assert.That(mixer.Predict(contexts), Is.InRange(30000, 35000));
  }

  [Test]
  [Category("HappyPath")]
  public void Mixer_LearnsToFollowAConfidentModel() {
    // One model always sees the true bit (1); after training the mixed
    // prediction should lean strongly towards 1.
    var good = new ContextModel(8);
    var noise = new ContextModel(8);
    var mixer = new ContextMixer(good, noise);
    int[] ctx = [0, 0];

    for (var i = 0; i < 400; ++i) {
      mixer.Predict(ctx);
      var bit = 1;
      good.Update(0, bit); // train the good model towards 1 directly
      mixer.Update(ctx, bit);
    }

    Assert.That(mixer.Predict(ctx), Is.GreaterThan(40000));
  }

  [Test]
  [Category("HappyPath")]
  public void Apm_RefinementConvergesTowardsObservedBit() {
    var apm = new Apm(4);
    // Feed a neutral 0.5 input but always observe bit 1 under context 0.
    int refined = 0;
    for (var i = 0; i < 2000; ++i) {
      refined = apm.Refine(2048, 0);
      apm.Update(1);
    }
    Assert.That(refined, Is.GreaterThan(2048));
  }

  [Test]
  [Category("Boundary")]
  public void Apm_RoundTripsExtremeProbabilities() {
    var apm = new Apm(4);
    Assert.That(apm.Refine(1, 0), Is.InRange(1, Logistic.ProbabilityScale - 1));
    Assert.That(apm.Refine(Logistic.ProbabilityScale - 1, 0), Is.InRange(1, Logistic.ProbabilityScale - 1));
  }
}
