using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class AdditionalExoticHashTests {
  [TestCase("", "44A99882FEA033566856A27E7F0C94DC84FAC7E411B08B890A4A574E3DB75D4A")]
  [TestCase("00", "F165CCD18640B9703E96F1BD9A4A4EE32DD4031E4680A1B9890891DCC63468A7")]
  public void PhotonBeetleMatchesSourceVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(PhotonBeetleHash.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "7346BC14F036E87AE03D0997913088F5F68411434B3CF8B54FA796A80D251F91")]
  [TestCase("00", "8DD446ADA58A7740ECF56EB638EF775F7D5C0FD5F0C2BBBDFDEC29609D3C43A2")]
  public void IsapMatchesSourceVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(IsapHash.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  /// <summary>
  /// Values taken from the published CityHash 1.1 algorithm. One case per length
  /// branch: the empty constant, the 1-3 byte path, the 4-7 path, the 8-16 path,
  /// 17-32, 33-64, and the 64-byte chunk loop past that.
  /// </summary>
  [TestCase("", "9AE16A3B2F90404F")]
  [TestCase("a", "B3454265B6DF75E3")]
  [TestCase("abc", "24A5B3A074E7F369")]
  [TestCase("hello", "B48BE5A931380CE8")]
  [TestCase("0123456789abcdef", "54B961E5DC834067")]
  [TestCase("0123456789abcdefg", "A6DDFF87A449D24A")]
  public void CityHashMatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(CityHash.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase(33, "71AE2692BDCB0F71")]
  [TestCase(64, "F48BEB293F161D43")]
  [TestCase(65, "AC589C990483DD2E")]
  [TestCase(100, "A47FE83E60B34CC6")]
  public void CityHashCoversEveryLengthBranch(int length, string expected) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)i;
    Assert.That(Convert.ToHexString(CityHash.Compute(data)), Is.EqualTo(expected));
  }

  [Test]
  public void Knot256_256MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(KnotHash.Compute([], KnotHashVariant.KnotHash256_256)), Is.EqualTo("CF1AC5B7AA08D36D544E2D2049D0D0A5F1F6FF7B553D18035E69323D8E4118B1"));

  [Test]
  public void Knot256_384MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(KnotHash.Compute([], KnotHashVariant.KnotHash256_384)), Is.EqualTo("5025252949BF0EBF9D750D2E11AB5C75E4F7B8DCA426B58EA2AE52A857653E04"));

  [Test]
  public void Knot384_384MatchesSourceVector() =>
    Assert.That(Convert.ToHexString(KnotHash.Compute([], KnotHashVariant.KnotHash384_384)), Is.EqualTo("4F3D463251831D3689692AA1B4E02DDAD79ABFCBE075A2CD2805E95C099DB75BF11C3C5EC917B6C5B3B76F8BB8D6DB2C"));

  [Test]
  public void KnotFamilyAdvertisesDistinctDigestSizes() =>
    Assert.That(KnotHash.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256, 384, 512 }));
}
