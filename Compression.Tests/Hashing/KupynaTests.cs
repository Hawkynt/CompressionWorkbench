using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class KupynaTests {
  [TestCase(256, "", "CD5101D1CCDF0D1D1F4ADA56E888CD724CA1A0838A3521E7131D4FB78D0F5EB6")]
  [TestCase(256, "FF", "EA7677CA4526555680441C117982EA14059EA6D0D7124D6ECDB3DEEC49E890F4")]
  [TestCase(512, "", "656B2F4CD71462388B64A37043EA55DBE445D452AECD46C3298343314EF04019BCFA3F04265A9857F91BE91FCE197096187CEDA78C9C1C021C294A0689198538")]
  [TestCase(512, "FF", "871B18CF754B72740307A97B449ABEB32B64444CC0D5A4D65830AE5456837A72D8458F12C8F06C98C616ABE11897F86263B5CB77C420FB375374BEC52B6D0292")]
  public void MatchesBouncyCastleVectors(int bits, string inputHex, string expectedHex) {
    Assert.That(Convert.ToHexString(Kupyna.Compute(Convert.FromHexString(inputHex), bits)), Is.EqualTo(expectedHex));
  }

  [Test]
  public void MatchesKupyna256FullBlockVector() {
    var input = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(Kupyna.Compute(input, 256)), Is.EqualTo("08F4EE6F1BE6903B324C4E27990CB24EF69DD58DBE84813EE0A52F6631239875"));
  }

  [Test]
  public void MatchesKupyna384ReferenceVector() {
    var input = Enumerable.Range(0, 95).Select(value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(Kupyna.Compute(input, 384)), Is.EqualTo("D9021692D84E5175735654846BA751E6D0ED0FAC36DFBC0841287DCB0B5584C75016C3DECC2A6E47C50B2F3811E351B8"));
  }

  [Test]
  public void MatchesKupyna512FullBlockVector() {
    var input = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(Kupyna.Compute(input, 512)), Is.EqualTo("3813E2109118CDFB5A6D5E72F7208DCCC80A2DFB3AFDFB02F46992B5EDBE536B3560DD1D7E29C6F53978AF58B444E37BA685C0DD910533BA5D78EFFFC13DE62A"));
  }

  [Test]
  public void ExposesRecommendedDigestSizesAsOneRange() {
    Assert.That(Kupyna.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256, 384, 512 }));
    Assert.Throws<ArgumentOutOfRangeException>(() => Kupyna.Compute([], 320));
  }
}
