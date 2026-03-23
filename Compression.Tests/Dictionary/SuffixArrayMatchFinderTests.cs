using System.Text;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class SuffixArrayMatchFinderTests {
  [Category("Exception")]
  [Test]
  public void Constructor_EmptyData_Throws() {
    Assert.Throws<ArgumentException>(() => new SuffixArrayMatchFinder([]));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_NoMatch_ReturnsDefault() {
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 3, maxDistance: 10, maxLength: 258, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_FindsExactMatch() {
    // "ABCABC" — at position 3, should find match of length 3 at distance 3
    var data = Encoding.ASCII.GetBytes("ABCABC");
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 3, maxDistance: 10, maxLength: 258, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(3));
    Assert.That(match.Distance, Is.EqualTo(3));
  }

  [Category("Boundary")]
  [Test]
  public void FindMatch_RespectsMaxDistance() {
    var data = Encoding.ASCII.GetBytes("ABCXYZABC");
    var finder = new SuffixArrayMatchFinder(data);
    // Distance would be 6, but max is 5
    var match = finder.FindMatch(data, 6, maxDistance: 5, maxLength: 258, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Category("Boundary")]
  [Test]
  public void FindMatch_RespectsMinLength() {
    var data = Encoding.ASCII.GetBytes("ABABX");
    var finder = new SuffixArrayMatchFinder(data);
    // Match of length 2 exists at position 2, but minLength is 3
    var match = finder.FindMatch(data, 2, maxDistance: 10, maxLength: 258, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Category("Boundary")]
  [Test]
  public void FindMatch_RespectsMaxLength() {
    var data = Encoding.ASCII.GetBytes("ABCDEFABCDEF");
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 6, maxDistance: 10, maxLength: 4, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(4));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_FindsLongestMatch() {
    // "ABCDABCXABCD" — at position 8, matches "ABCD" (len 4) at distance 8 and "ABC" (len 3) at distance 4
    var data = Encoding.ASCII.GetBytes("ABCDABCXABCD");
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 8, maxDistance: 20, maxLength: 258, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(4));
  }

  [Category("EdgeCase")]
  [Test]
  public void FindMatch_RepetitiveData() {
    // All same bytes — should find long matches
    var data = new byte[100];
    Array.Fill(data, (byte)0xAA);
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 10, maxDistance: 100, maxLength: 50, minLength: 3);
    Assert.That(match.Length, Is.EqualTo(50));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_LargeData_DoesNotThrow() {
    var data = new byte[10000];
    new Random(42).NextBytes(data);
    var finder = new SuffixArrayMatchFinder(data);
    // Just ensure it doesn't crash
    for (var i = 3; i < 100; ++i) {
      var match = finder.FindMatch(data, i, maxDistance: 10000, maxLength: 258, minLength: 3);
      // match may or may not exist — just checking no exceptions
      Assert.That(match.Length, Is.GreaterThanOrEqualTo(0));
    }
  }

  [Category("EdgeCase")]
  [Test]
  public void FindMatch_SingleByte_NoMatch() {
    var data = new byte[] { 42 };
    var finder = new SuffixArrayMatchFinder(data);
    var match = finder.FindMatch(data, 0, maxDistance: 10, maxLength: 258, minLength: 1);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Category("ThemVsUs")]
  [Test]
  public void FindMatch_AgreesWithHashChain_OnCompressibleData() {
    var data = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog.");

    var saFinder = new SuffixArrayMatchFinder(data);
    var hcFinder = new HashChainMatchFinder(data.Length, 128);

    // Insert positions for hash chain
    for (var i = 0; i < data.Length; ++i) {
      var saMatch = saFinder.FindMatch(data, i, data.Length, 258, 3);
      var hcMatch = hcFinder.FindMatch(data, i, data.Length, 258, 3);

      // SA should find matches at least as long as hash chain
      if (hcMatch.Length >= 3)
        Assert.That(saMatch.Length, Is.GreaterThanOrEqualTo(hcMatch.Length),
          $"At position {i}: SA found {saMatch.Length} but HC found {hcMatch.Length}");
    }
  }
}
