using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class LuffaTests {
  [Test]
  public void Luffa224MatchesNistEmptyVector() =>
    Assert.That(Convert.ToHexString(Luffa224.Compute([])), Is.EqualTo("DBB8665871F4154D3E4396AEFBBA417CB7837DD683C332BA6BE87E02"));

  [Test]
  public void Luffa256MatchesNistEmptyVector() =>
    Assert.That(Convert.ToHexString(Luffa256.Compute([])), Is.EqualTo("DBB8665871F4154D3E4396AEFBBA417CB7837DD683C332BA6BE87E02A2712D6F"));

  [Test]
  public void Luffa384MatchesNistEmptyVector() =>
    Assert.That(Convert.ToHexString(Luffa384.Compute([])), Is.EqualTo("117D3AD49024DFE2994F4E335C9B330B48C537A13A9B7FA465938E1A02FF862BCDF33838BC0F371B045D26952D3EA0C5"));

  [Test]
  public void Luffa512MatchesNistEmptyVector() =>
    Assert.That(Convert.ToHexString(Luffa512.Compute([])), Is.EqualTo("6E7DE4501189B3CA58F3AC114916654BBCD4922024B4CC1CD764ACFE8AB4B7805DF133EAB345FFDB1C414564C924F48E0A301824E2AC4C34BD4EFDE2E43DA90E"));
}
