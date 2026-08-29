using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class HavalTests {
  [Test]
  public void Haval128_3MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(Haval.Compute128(Encoding.ASCII.GetBytes("abc"), 3)), Is.EqualTo("9E40ED883FB63E985D299B40CDA2B8F2"));

  [Test]
  public void Haval256_3MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(Haval.Compute(Encoding.ASCII.GetBytes("abc"), 3, 256)), Is.EqualTo("8699F1E3384D05B2A84B032693E2B6F46DF85A13A50D93808D6874BB8FB9E86C"));

  [Test]
  public void Haval256_5MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(Haval.Compute256([], 5)), Is.EqualTo("BE417BB4DD5CFB76C7126F4F8EEB1553A449039307B1A3CD451DBFDC0FBBE330"));
}
