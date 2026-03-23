using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class BwtTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_EmptyData() {
    byte[] data = [];
    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_SingleByte() {
    byte[] data = [42];
    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_Banana() {
    var data = "banana"u8.ToArray();
    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_Repetitive() {
    var pattern = "abcabc"u8.ToArray();
    var data = new byte[pattern.Length * 50];
    for (var i = 0; i < 50; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_Random1KB() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Forward_Inverse_RoundTrip_AllZeros() {
    var data = new byte[4096];

    var (transformed, index) = BurrowsWheelerTransform.Forward(data);
    var result = BurrowsWheelerTransform.Inverse(transformed, index);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Forward_AllIdenticalBytes() {
    var data = new byte[100];
    Array.Fill(data, (byte)0x41);

    var (transformed, index) = BurrowsWheelerTransform.Forward(data);

    // All rotations are identical, so transformed should be all the same byte
    Assert.That(transformed, Is.All.EqualTo(0x41));
  }

  [Category("Exception")]
  [Test]
  public void Inverse_InvalidIndex_Throws() {
    byte[] data = [1, 2, 3];
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      BurrowsWheelerTransform.Inverse(data, -1));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      BurrowsWheelerTransform.Inverse(data, 3));
  }
}
