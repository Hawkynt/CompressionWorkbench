using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

/// <summary>
/// SA-IS against a naive suffix sort, and the suffix-form BWT against its definition: the
/// suffix rows of the input plus a sentinel, the sentinel's own cell dropped.
/// </summary>
[TestFixture]
public sealed class SuffixBurrowsWheelerTransformTests {

  private static int[] NaiveSuffixArray(byte[] data)
    => [.. Enumerable.Range(0, data.Length).OrderBy(i => i, Comparer<int>.Create((a, b) => data.AsSpan(a).SequenceCompareTo(data.AsSpan(b))))];

  public static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("GivenNoBytes");
    yield return new TestCaseData(new byte[] { 7 }).SetName("GivenOneByte");
    yield return new TestCaseData(new byte[] { 2, 1 }).SetName("GivenTwoDescendingBytes");
    yield return new TestCaseData(new byte[] { 1, 2 }).SetName("GivenTwoAscendingBytes");
    yield return new TestCaseData(new byte[64]).SetName("GivenOneRepeatedByte");
    yield return new TestCaseData("banana"u8.ToArray()).SetName("GivenBanana");
    yield return new TestCaseData("mississippi"u8.ToArray()).SetName("GivenMississippi");
    yield return new TestCaseData(Enumerable.Range(0, 300).Select(i => (byte)(i % 3)).ToArray()).SetName("GivenAPeriodicInput");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray()).SetName("GivenAStrictlyDescendingInput");
    yield return new TestCaseData(Enumerable.Range(0, 1000).Select(i => (byte)((i * i) ^ (i >> 3))).ToArray()).SetName("GivenAQuadraticPattern");
    var random = new Random(4321);
    var noise = new byte[5000];
    random.NextBytes(noise);
    yield return new TestCaseData(noise).SetName("GivenNoise");
    var lowAlphabet = new byte[5000];
    for (var i = 0; i < lowAlphabet.Length; ++i) lowAlphabet[i] = (byte)random.Next(2);
    yield return new TestCaseData(lowAlphabet).SetName("GivenATwoSymbolAlphabet");
  }

  [TestCaseSource(nameof(Inputs)), Category("HappyPath")]
  public void SuffixArray_MatchesANaiveSort(byte[] data)
    => Assert.That(SuffixArraySais.Build(data), Is.EqualTo(NaiveSuffixArray(data)));

  [TestCaseSource(nameof(Inputs)), Category("HappyPath")]
  public void Forward_FollowsTheDefinition_AndInverts(byte[] data) {
    var (transformed, primary) = SuffixBurrowsWheelerTransform.Forward(data);
    var sa = NaiveSuffixArray(data);
    var expected = new List<byte>();
    var expectedPrimary = 0;
    if (data.Length > 0) {
      expected.Add(data[^1]);
      for (var row = 0; row < sa.Length; ++row)
        if (sa[row] == 0) expectedPrimary = row + 1;
        else expected.Add(data[sa[row] - 1]);
    }

    Assert.Multiple(() => {
      Assert.That(transformed, Is.EqualTo(expected.ToArray()));
      Assert.That(primary, Is.EqualTo(expectedPrimary));
      Assert.That(SuffixBurrowsWheelerTransform.Inverse(transformed, primary), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenBanana_WhenTransformed_ThenItIsTheLibsaisResult() {
    // banana$ rows: $ a$ ana$ anana$ banana$ na$ nana$ -> last column a n n b $ a a, $ dropped.
    var (transformed, primary) = SuffixBurrowsWheelerTransform.Forward("banana"u8);
    Assert.Multiple(() => {
      Assert.That(transformed, Is.EqualTo("annbaa"u8.ToArray()));
      Assert.That(primary, Is.EqualTo(4));
    });
  }

  [Test, Category("HappyPath")]
  public void Samples_AreTheRowsOfEveryRatethSuffix() {
    var data = Enumerable.Range(0, 1000).Select(i => (byte)((i * 31) % 17)).ToArray();
    var (_, primary, samples) = SuffixBurrowsWheelerTransform.Forward(data, 64);
    var sa = NaiveSuffixArray(data);
    Assert.Multiple(() => {
      Assert.That(samples, Has.Length.EqualTo((data.Length - 1) / 64 + 1));
      Assert.That(samples[0], Is.EqualTo(primary));
      for (var t = 0; t < samples.Length; ++t)
        Assert.That(samples[t], Is.EqualTo(Array.IndexOf(sa, t * 64) + 1));
    });
  }

  [TestCase(0), TestCase(7)]
  [Category("ExceptionalCase")]
  public void GivenAPrimaryIndexOutsideTheRows_WhenInverted_ThenItIsRefused(int primary)
    => Assert.Throws<ArgumentOutOfRangeException>(() => SuffixBurrowsWheelerTransform.Inverse("annbaa"u8, primary));
}
