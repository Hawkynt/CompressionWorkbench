using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class BinaryTreeMatchFinderTests {
  [Category("HappyPath")]
  [Test]
  public void FindMatch_NoMatch_ReturnsDefault() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    // First position — nothing to match against
    var match = finder.FindMatch(data, 0, 1024, 258, 3);
    Assert.That(match.Length, Is.EqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_SimpleRepeat_FindsMatch() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = [1, 2, 3, 1, 2, 3, 4, 5];

    // Insert positions 0-2
    finder.FindMatch(data, 0, 1024, 258, 3);
    finder.FindMatch(data, 1, 1024, 258, 3);
    finder.FindMatch(data, 2, 1024, 258, 3);

    // At position 3, should find match at distance 3, length 3
    var match = finder.FindMatch(data, 3, 1024, 258, 3);
    Assert.That(match.Length, Is.GreaterThanOrEqualTo(3));
    Assert.That(match.Distance, Is.GreaterThan(0));
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_LongerMatch_Preferred() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = new byte[100];
    // Pattern: ABCABC... at start, then repeat
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    // Process first 20 positions
    for (int i = 0; i < 20; ++i)
      finder.FindMatch(data, i, 1024, 258, 3);

    // At position 20, should find a good match (length 10 pattern repeats)
    var match = finder.FindMatch(data, 20, 1024, 258, 3);
    Assert.That(match.Length, Is.GreaterThanOrEqualTo(3));
  }

  [Category("Boundary")]
  [Test]
  public void FindMatch_RespectsMaxLength() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = new byte[50];
    Array.Fill(data, (byte)0xAA);

    finder.FindMatch(data, 0, 1024, 258, 3);
    finder.FindMatch(data, 1, 1024, 258, 3);

    var match = finder.FindMatch(data, 2, 1024, 5, 3); // maxLength = 5
    Assert.That(match.Length, Is.LessThanOrEqualTo(5));
  }

  [Category("Boundary")]
  [Test]
  public void FindMatch_RespectsMinLength() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = [1, 2, 1, 2, 5, 6];

    finder.FindMatch(data, 0, 1024, 258, 3);
    finder.FindMatch(data, 1, 1024, 258, 3);

    // Match of length 2 at position 2, but minLength=3 should reject it
    var match = finder.FindMatch(data, 2, 1024, 258, 3);
    // Length 2 match exists but should be rejected
    Assert.That(match.Length == 0 || match.Length >= 3);
  }

  [Category("HappyPath")]
  [Test]
  public void InsertPosition_DoesNotCrash() {
    var finder = new BinaryTreeMatchFinder(1024);
    byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

    // InsertPosition should work without errors
    finder.InsertPosition(data, 0);
    finder.InsertPosition(data, 1);
    finder.InsertPosition(data, 2);

    Assert.Pass();
  }

  [Category("HappyPath")]
  [Test]
  public void FindMatch_ManyPositions_DoesNotCrash() {
    var finder = new BinaryTreeMatchFinder(4096);
    var rng = new Random(42);
    byte[] data = new byte[2000];
    rng.NextBytes(data);

    for (int i = 0; i < data.Length - 3; ++i)
      finder.FindMatch(data, i, 4096, 258, 3);

    Assert.Pass();
  }

  [Category("HappyPath")]
  [Test]
  public void ImplementsIMatchFinder() {
    IMatchFinder finder = new BinaryTreeMatchFinder(1024);
    byte[] data = [1, 2, 3, 4, 5, 1, 2, 3];

    var match = finder.FindMatch(data, 0, 1024, 258, 3);
    Assert.That(match.Length, Is.EqualTo(0)); // First position, no prior data
  }
}
