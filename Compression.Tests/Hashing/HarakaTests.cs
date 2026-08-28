using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class HarakaTests {
  [Test]
  public void Haraka256MatchesOfficialVector() {
    var input = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(Haraka256.Compute(input)), Is.EqualTo("8027CCB87949774B78D0545FB72BF70C695C2A0923CBD47BBA1159EFBF2B2C1C"));
  }

  [Test]
  public void Haraka512MatchesOfficialVector() {
    var input = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(Haraka512.Compute(input)), Is.EqualTo("BE7F723B4E80A99813B292287F306F625A6D57331CAE5F34DD9277B0945BE2AA"));
  }
}
