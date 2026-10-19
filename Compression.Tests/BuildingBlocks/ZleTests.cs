using Compression.Core.Transforms;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ZleTests {

  private static readonly ZleBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    Assert.That(Bb.Compress([]), Is.Empty);
    Assert.That(Bb.Decompress([]), Is.Empty);
  }

  [Test, Category("Spec")]
  public void ReferenceVector_DecodesLiteralAndZeroRuns() {
    // Given: control 2 = three literals, control 64 = one zero, control 255 = 192 zeros,
    //        control 0 = one literal.
    byte[] stream = [2, 0x41, 0x00, 0x42, 64, 255, 0, 0x43];
    // When
    var decoded = Bb.Decompress(stream);
    // Then
    var expected = new byte[3 + 1 + 192 + 1];
    expected[0] = 0x41;
    expected[2] = 0x42;
    expected[^1] = 0x43;
    Assert.That(decoded, Is.EqualTo(expected).AsCollection);
  }

  [Test, Category("Spec")]
  public void Encode_LoneZeroStaysLiteral_ZeroPairStartsARun() {
    Assert.That(Bb.Compress([1, 0, 2]), Is.EqualTo(new byte[] { 2, 1, 0, 2 }).AsCollection);
    Assert.That(Bb.Compress([1, 0, 0, 2]), Is.EqualTo(new byte[] { 0, 1, 65, 0, 2 }).AsCollection);
  }

  [TestCase(1, new byte[] { 64 })]
  [TestCase(192, new byte[] { 255 })]
  [TestCase(193, new byte[] { 255, 64 })]
  [Category("Boundary")]
  public void Encode_ZeroRunLengths(int zeros, byte[] expected)
    => Assert.That(Bb.Compress(new byte[zeros]), Is.EqualTo(expected).AsCollection);

  [TestCase(64, 65)]
  [TestCase(65, 67)]
  [Category("Boundary")]
  public void Encode_LiteralRunSplitsAt64(int length, int encodedLength) {
    var data = Enumerable.Range(1, length).Select(i => (byte)i).ToArray();
    var encoded = Bb.Compress(data);
    Assert.That(encoded, Has.Length.EqualTo(encodedLength));
    Assert.That(encoded[0], Is.EqualTo(63));
    Assert.That(Bb.Decompress(encoded), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SparseData_RoundTripsAndCompresses() {
    var data = new byte[20000];
    for (var i = 0; i < data.Length; i += 997) data[i] = (byte)(i | 1);
    var encoded = Bb.Compress(data);
    Assert.That(encoded.Length, Is.LessThan(data.Length / 20));
    Assert.That(Bb.Decompress(encoded), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Random_RoundTrips() {
    var data = new byte[4096];
    new Random(5).NextBytes(data);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Spec")]
  public void BoundedDecode_StopsAtDestinationAndIgnoresPadding() {
    // Given: 192 zeros then padding the way ZFS rounds a block up to its sector
    byte[] stream = [255, 0, 0, 0];
    var destination = new byte[100];
    Array.Fill(destination, (byte)0xEE);
    // When
    ZleEncoding.Decode(stream, destination);
    // Then
    Assert.That(destination, Is.All.EqualTo(0));
  }

  [Test, Category("ErrorHandling")]
  public void TruncatedLiteralRun_Throws() {
    Assert.Throws<InvalidDataException>(() => Bb.Decompress([5, 1, 2]));
    Assert.Throws<InvalidDataException>(() => ZleEncoding.Decode([5, 1, 2], new byte[6]));
  }

  [Test, Category("ErrorHandling")]
  public void BoundedDecode_StreamTooShort_Throws()
    => Assert.Throws<InvalidDataException>(() => ZleEncoding.Decode([64], new byte[2]));

  [Test, Category("EdgeCase")]
  public void BbRegistry_ExposesMetadata() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Zle"));
    Assert.That(Bb.DisplayName, Is.EqualTo("ZLE"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Transform));
  }
}
