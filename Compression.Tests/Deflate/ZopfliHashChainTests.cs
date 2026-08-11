using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class ZopfliHashChainTests {
  /// <summary>
  /// Expands the run-encoded answer of a whole-input search into one entry per achievable
  /// length at <paramref name="position"/>, which is the form the shortest-path parser
  /// consumes it in.
  /// </summary>
  private static List<(int Length, int Distance)> MatchesAt(byte[] data, int position) {
    var cache = ZopfliMatchCache.Build(data);
    var result = new List<(int, int)>();

    var length = ZopfliMatchCache.MinMatch;
    for (var run = cache.RunStart(position); run < cache.RunEnd(position); ++run)
      for (; length <= cache.MaxLengthOf(run); ++length)
        result.Add((length, cache.DistanceOf(run)));

    return result;
  }

  [Category("HappyPath")]
  [Test]
  public void Matches_CoverEveryAchievableLength() {
    // "ABCABCABCABC" — position 3 matches position 0 at every length from 3 upwards.
    var data = "ABCABCABCABC"u8.ToArray();

    var matches = MatchesAt(data, 3);

    Assert.That(matches, Has.Count.GreaterThan(1));
    Assert.That(matches.Select(m => m.Length), Is.EqualTo(Enumerable.Range(3, matches.Count)).AsCollection);
  }

  [Category("HappyPath")]
  [Test]
  public void Matches_AreInAscendingLengthOrder() {
    var data = "ABCABCABCABC"u8.ToArray();

    var matches = MatchesAt(data, 3);

    for (var i = 1; i < matches.Count; ++i)
      Assert.That(matches[i].Length, Is.GreaterThan(matches[i - 1].Length),
        "Matches must be sorted by ascending length");
  }

  [Category("HappyPath")]
  [Test]
  public void Matches_HoldOneDistancePerLength() {
    // Several candidates reach the same length; only one entry per length survives.
    var data = "ABABABABAB"u8.ToArray();

    var matches = MatchesAt(data, 6);

    var lengths = matches.Select(m => m.Length).ToList();
    Assert.That(lengths, Is.EqualTo(lengths.Distinct().ToList()).AsCollection);
  }

  [Category("HappyPath")]
  [Test]
  public void Matches_PreferTheShortestDistance() {
    // "XYZ" appears at 0, 3 and 6; searching from 9 must report the nearest of them.
    var data = "XYZXYZXYZXYZ"u8.ToArray();

    var matches = MatchesAt(data, 9);

    Assert.That(matches, Is.Not.Empty);
    Assert.That(matches[0].Distance, Is.EqualTo(3));
  }

  [Category("Boundary")]
  [Test]
  public void Matches_NeverReachBeforeTheStartOfInput() {
    var data = new byte[100];
    Array.Fill(data, (byte)'A');

    foreach (var (_, distance) in MatchesAt(data, 50))
      Assert.That(distance, Is.LessThanOrEqualTo(50));
  }

  [Category("EdgeCase")]
  [Test]
  public void Matches_AreEmptyWhenTooFewBytesRemain() {
    var data = "AB"u8.ToArray();

    Assert.That(MatchesAt(data, 0), Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void Matches_AreEmptyWhenNothingRepeats() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;

    Assert.That(MatchesAt(data, 200), Is.Empty);
  }

  [Category("Boundary")]
  [Test]
  public void Matches_ReachTheVeryFirstPosition() {
    // A match whose source is byte zero used to be dropped, because the walk stopped at
    // the window's lower bound instead of examining what sat on it.
    var data = "ABCDXXXXABCD"u8.ToArray();

    var matches = MatchesAt(data, 8);

    Assert.That(matches, Is.Not.Empty);
    Assert.That(matches[^1].Distance, Is.EqualTo(8));
    Assert.That(matches[^1].Length, Is.EqualTo(4));
  }

  [Category("Boundary")]
  [Test]
  public void Matches_StopAtTheLongestLengthRfc1951Allows() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0x5A);

    var matches = MatchesAt(data, 1);

    Assert.That(matches[^1].Length, Is.EqualTo(ZopfliMatchCache.MaxMatch));
    Assert.That(matches[^1].Distance, Is.EqualTo(1));
  }
}
