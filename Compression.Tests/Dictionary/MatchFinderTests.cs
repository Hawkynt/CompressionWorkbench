using System.Text;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class MatchFinderTests {
  [Test]
  public void HashChain_FindsExactMatch() {
    byte[] data = Encoding.ASCII.GetBytes("ABCABC");
    var finder = new HashChainMatchFinder(32);

    // Insert first 3 bytes
    for (int i = 0; i < 3; ++i)
      finder.InsertPosition(data, i);

    // Find match at position 3
    var match = finder.FindMatch(data, 3, 32, 258);
    Assert.That(match.Length, Is.GreaterThanOrEqualTo(3));
    Assert.That(match.Distance, Is.EqualTo(3));
  }

  [Test]
  public void HashChain_NoMatch_ReturnsDefault() {
    byte[] data = Encoding.ASCII.GetBytes("ABCDEF");
    var finder = new HashChainMatchFinder(32);

    var match = finder.FindMatch(data, 0, 32, 258);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Test]
  public void HashChain_FindsLongestMatch() {
    byte[] data = Encoding.ASCII.GetBytes("ABCDABCDABCDE");
    var finder = new HashChainMatchFinder(32);

    // Insert positions 0-3
    for (int i = 0; i < 4; ++i)
      finder.InsertPosition(data, i);
    // Insert positions 4-7
    for (int i = 4; i < 8; ++i)
      finder.InsertPosition(data, i);

    // At position 8, "ABCDE" matches "ABCD" at position 4 (length 4)
    // but also at position 0 (also length 4)
    var match = finder.FindMatch(data, 8, 32, 258);
    Assert.That(match.Length, Is.GreaterThanOrEqualTo(4));
  }

  [Test]
  public void HashChain_RespectsMaxDistance() {
    byte[] data = Encoding.ASCII.GetBytes("ABC___ABC");
    var finder = new HashChainMatchFinder(32);

    for (int i = 0; i < 6; ++i)
      finder.InsertPosition(data, i);

    // With maxDistance=2, should not find the match at distance 6
    var match = finder.FindMatch(data, 6, 2, 258);
    Assert.That(match.Length, Is.LessThan(3));
  }
}
