namespace Compression.Tests.Deflate;

using Compression.Core.Deflate;

[TestFixture]
public class DeflateConstantsTests {
  [Category("ThemVsUs")]
  [Test]
  public void LengthBase_Has29Entries() {
    Assert.That(DeflateConstants.LengthBase.Length, Is.EqualTo(29));
  }

  [Category("ThemVsUs")]
  [Test]
  public void LengthExtraBits_Has29Entries() {
    Assert.That(DeflateConstants.LengthExtraBits.Length, Is.EqualTo(29));
  }

  [Category("ThemVsUs")]
  [Test]
  public void DistanceBase_Has30Entries() {
    Assert.That(DeflateConstants.DistanceBase.Length, Is.EqualTo(30));
  }

  [Category("ThemVsUs")]
  [Test]
  public void DistanceExtraBits_Has30Entries() {
    Assert.That(DeflateConstants.DistanceExtraBits.Length, Is.EqualTo(30));
  }

  [Category("Boundary")]
  [Test]
  public void LengthTable_CoversAllLengths3To258() {
    // Every length 3–258 must be reachable via base + extra bits
    var reachable = new HashSet<int>();
    ReadOnlySpan<int> bases = DeflateConstants.LengthBase;
    ReadOnlySpan<int> extras = DeflateConstants.LengthExtraBits;

    for (int i = 0; i < bases.Length; ++i) {
      var range = 1 << extras[i];
      for (int j = 0; j < range; ++j)
        reachable.Add(bases[i] + j);
    }

    for (int len = 3; len <= 258; ++len)
      Assert.That(reachable.Contains(len), Is.True, $"Length {len} not reachable");
  }

  [Category("Boundary")]
  [Test]
  public void DistanceTable_CoversAllDistances1To32768() {
    var reachable = new HashSet<int>();
    ReadOnlySpan<int> bases = DeflateConstants.DistanceBase;
    ReadOnlySpan<int> extras = DeflateConstants.DistanceExtraBits;

    for (int i = 0; i < bases.Length; ++i) {
      var range = 1 << extras[i];
      for (int j = 0; j < range; ++j)
        reachable.Add(bases[i] + j);
    }

    for (int dist = 1; dist <= 32768; ++dist)
      Assert.That(reachable.Contains(dist), Is.True, $"Distance {dist} not reachable");
  }

  [Category("HappyPath")]
  [Test]
  public void LengthBase_IsMonotonicallyIncreasing() {
    ReadOnlySpan<int> bases = DeflateConstants.LengthBase;
    for (int i = 1; i < bases.Length; ++i)
      Assert.That(bases[i], Is.GreaterThan(bases[i - 1]), $"LengthBase[{i}] <= LengthBase[{i - 1}]");
  }

  [Category("HappyPath")]
  [Test]
  public void DistanceBase_IsMonotonicallyIncreasing() {
    ReadOnlySpan<int> bases = DeflateConstants.DistanceBase;
    for (int i = 1; i < bases.Length; ++i)
      Assert.That(bases[i], Is.GreaterThan(bases[i - 1]), $"DistanceBase[{i}] <= DistanceBase[{i - 1}]");
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticLiteralLengths_MatchRfc1951() {
    var lengths = DeflateConstants.GetStaticLiteralLengths();
    Assert.That(lengths.Length, Is.EqualTo(288));

    for (int i = 0; i <= 143; ++i)
      Assert.That(lengths[i], Is.EqualTo(8), $"Symbol {i}");
    for (int i = 144; i <= 255; ++i)
      Assert.That(lengths[i], Is.EqualTo(9), $"Symbol {i}");
    for (int i = 256; i <= 279; ++i)
      Assert.That(lengths[i], Is.EqualTo(7), $"Symbol {i}");
    for (int i = 280; i <= 287; ++i)
      Assert.That(lengths[i], Is.EqualTo(8), $"Symbol {i}");
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticDistanceLengths_AllFive() {
    var lengths = DeflateConstants.GetStaticDistanceLengths();
    Assert.That(lengths.Length, Is.EqualTo(30));
    Assert.That(lengths, Is.All.EqualTo(5));
  }

  [Category("ThemVsUs")]
  [Test]
  public void CodeLengthOrder_Has19Entries() {
    Assert.That(DeflateConstants.CodeLengthOrder.Length, Is.EqualTo(19));
  }

  [Category("ThemVsUs")]
  [Test]
  public void CodeLengthOrder_ContainsAllValues0To18() {
    var values = new HashSet<int>();
    ReadOnlySpan<int> order = DeflateConstants.CodeLengthOrder;
    for (int i = 0; i < order.Length; ++i)
      values.Add(order[i]);

    for (int i = 0; i <= 18; ++i)
      Assert.That(values.Contains(i), Is.True, $"Value {i} missing from CodeLengthOrder");
  }

  [Category("HappyPath")]
  [TestCase(3, 257)]
  [TestCase(4, 258)]
  [TestCase(10, 264)]
  [TestCase(11, 265)]
  [TestCase(18, 268)]
  [TestCase(19, 269)]
  [TestCase(34, 272)]
  [TestCase(35, 273)]
  [TestCase(66, 276)]
  [TestCase(67, 277)]
  [TestCase(130, 280)]
  [TestCase(131, 281)]
  [TestCase(257, 284)]
  [TestCase(258, 285)]
  public void GetLengthCode_ReturnsCorrectCode(int length, int expectedCode) {
    Assert.That(DeflateConstants.GetLengthCode(length), Is.EqualTo(expectedCode));
  }

  [Category("HappyPath")]
  [Test]
  public void GetLengthCode_RoundTripsWithBaseAndExtraBits() {
    ReadOnlySpan<int> bases = DeflateConstants.LengthBase;
    ReadOnlySpan<int> extras = DeflateConstants.LengthExtraBits;

    for (int length = 3; length <= 258; ++length) {
      int code = DeflateConstants.GetLengthCode(length);
      int idx = code - 257;
      int extra = length - bases[idx];
      Assert.That(extra, Is.GreaterThanOrEqualTo(0), $"Length {length}: negative extra");
      Assert.That(extra, Is.LessThan(1 << extras[idx]), $"Length {length}: extra out of range");
    }
  }

  [Category("HappyPath")]
  [TestCase(1, 0)]
  [TestCase(2, 1)]
  [TestCase(4, 3)]
  [TestCase(5, 4)]
  [TestCase(7, 5)]
  [TestCase(9, 6)]
  [TestCase(13, 7)]
  [TestCase(32768, 29)]
  public void GetDistanceCode_ReturnsCorrectCode(int distance, int expectedCode) {
    Assert.That(DeflateConstants.GetDistanceCode(distance), Is.EqualTo(expectedCode));
  }

  [Category("HappyPath")]
  [Test]
  public void GetDistanceCode_RoundTripsWithBaseAndExtraBits() {
    ReadOnlySpan<int> bases = DeflateConstants.DistanceBase;
    ReadOnlySpan<int> extras = DeflateConstants.DistanceExtraBits;

    for (int distance = 1; distance <= 32768; ++distance) {
      int code = DeflateConstants.GetDistanceCode(distance);
      int extra = distance - bases[code];
      Assert.That(extra, Is.GreaterThanOrEqualTo(0), $"Distance {distance}: negative extra");
      Assert.That(extra, Is.LessThan(1 << extras[code]), $"Distance {distance}: extra out of range");
    }
  }

  [Category("Exception")]
  [Test]
  public void GetLengthCode_ThrowsForInvalidLength() {
    Assert.Throws<ArgumentOutOfRangeException>(() => DeflateConstants.GetLengthCode(2));
    Assert.Throws<ArgumentOutOfRangeException>(() => DeflateConstants.GetLengthCode(259));
  }

  [Category("Exception")]
  [Test]
  public void GetDistanceCode_ThrowsForInvalidDistance() {
    Assert.Throws<ArgumentOutOfRangeException>(() => DeflateConstants.GetDistanceCode(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => DeflateConstants.GetDistanceCode(32769));
  }
}
