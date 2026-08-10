using System.Text;
using Compression.Core.Entropy;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class GolombFixedMBuildingBlockTests {

  private static readonly GolombFixedMBuildingBlock Bb = new();
  private static readonly GolombBuildingBlock Adaptive = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    Assert.That(compressed, Is.Empty);
    Assert.That(Bb.Decompress(compressed), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x41];
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EveryByteValue_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)i;
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 4)));
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// M=2 is Rice k=1, so a stream of 0s and 1s costs two bits per value — the case the
  /// fixed parameter exists for.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ZeroHeavyResidual_Compresses() {
    var data = new byte[3000];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i % 11 == 0 ? 1 : 0);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x60);
    var data = new byte[2048];
    rng.NextBytes(data);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  /// <summary>Exercises the two- and three-byte forms of the LEB128 element count.</summary>
  [TestCase(127)]
  [TestCase(128)]
  [TestCase(16383)]
  [TestCase(16384)]
  [Category("BoundaryValue"), Category("RoundTrip")]
  public void VarIntCountBoundaries_RoundTrip(int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i & 1);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Pins the wire format: M byte, LEB128 count, then unary quotient plus one remainder bit
  /// per value, MSB-first and zero-padded to a byte boundary.
  /// </summary>
  [Test, Category("HappyPath")]
  public void SingleZero_MatchesKnownEncoding() {
    byte[] expected = [0x02, 0x01, 0x00];
    Assert.That(Bb.Compress([0]), Is.EqualTo(expected).AsCollection);
    Assert.That(Bb.Decompress(expected), Is.EqualTo(new byte[] { 0 }).AsCollection);
  }

  /// <summary>The adaptive profile stays on its four-byte little-endian count header.</summary>
  [Test, Category("EdgeCase")]
  public void AdaptiveProfile_HeaderIsUnchanged() {
    var compressed = Adaptive.Compress([0]);
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(5));
    Assert.That(compressed[..5], Is.EqualTo(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00 }).AsCollection);
    Assert.That(Adaptive.Compress([]), Is.EqualTo(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 }).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void ParameterOutOfRange_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => GolombBuildingBlock.Compress([1, 2, 3], GolombProfile.FixedParameter, 0));
    Assert.Throws<ArgumentOutOfRangeException>(
      () => GolombBuildingBlock.Compress([1, 2, 3], GolombProfile.FixedParameter, 256));
  }

  [Test, Category("EdgeCase")]
  public void TruncatedVarInt_Throws() {
    byte[] truncated = [0x02, 0x80];
    Assert.Throws<InvalidDataException>(() => Bb.Decompress(truncated));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_GolombFixedM"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Golomb/Rice (fixed M=2)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
      Assert.That(Adaptive.Id, Is.EqualTo("BB_Golomb"));
    });
  }
}
