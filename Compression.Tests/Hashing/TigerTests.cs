using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class TigerTests {
  [TestCase("", "3293AC630C13F0245F92BBB1766E16167A4E58492DDE73F3")]
  [TestCase("abc", "2AAB1484E8C158F2BFB8C5FF41B57A525129131C957B5F93")]
  [TestCase("Tiger", "DD00230799F5009FEC6DEBC838BB6A27DF2B9D6F110C7937")]
  [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-", "F71C8583902AFB879EDFE610F82C0D4786A3A534504486B5")]
  public void MatchesOfficialTigerVectors(string input, string expectedHex) {
    Assert.That(Convert.ToHexString(Tiger.Compute(Encoding.ASCII.GetBytes(input))), Is.EqualTo(expectedHex));
  }

  [Test]
  public void ExposesExactTigerDigestSize() {
    Assert.That(Tiger.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 192 }));
    Assert.Throws<ArgumentOutOfRangeException>(() => Tiger.Compute([], 160));
  }
}
