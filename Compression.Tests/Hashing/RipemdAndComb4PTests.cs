using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class RipemdAndComb4PTests {
  [TestCase("", "CDF26213A150DC3ECB610F18F6B38B46")]
  [TestCase("abc", "C14A12199C66E4BA84636B0F69144C77")]
  public void Ripemd128MatchesOfficialVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Ripemd128.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "9C1185A5C5E9FC54612808977EE8F548B2258D31")]
  [TestCase("abc", "8EB208F7E05D987A9B044A8E98C6B087F15A0BFC")]
  public void Ripemd160MatchesOfficialVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Ripemd160.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "02BA4C4E5F8ECD1877FC52D64D30E37A2D9774FB1E5D026380AE0168E3C5522D")]
  [TestCase("abc", "AFBD6E228B9D8CBBCEF5CA2D03E6DBA10AC0BC7DCBE4680E1E42D2E975459B65")]
  public void Ripemd256MatchesOfficialVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Ripemd256.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "22D65D5661536CDC75C1FDF5C6DE7B41B9F27325EBC61E8557177D705A0EC880151C3A32A00899B8")]
  [TestCase("abc", "DE4C01B3054F8930A79D09AE738E92301E5A17085BEFFDC1B8D116713E74F82FA942D64CDBC4682D")]
  public void Ripemd320MatchesOfficialVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Ripemd320.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void Comb4PMd4Md5MatchesBotanVector() =>
    Assert.That(Convert.ToHexString(Comb4PMd4Md5.Compute(Encoding.ASCII.GetBytes("comb4_input"))), Is.EqualTo("FD1A64F7BC61608FD054303AFA2E31608AA3F3788E3034821D63A0288A70B573"));

  [Test]
  public void Comb4PSha1Ripemd160MatchesBotanVector() =>
    Assert.That(Convert.ToHexString(Comb4PSha1Ripemd160.Compute(Encoding.ASCII.GetBytes("comb4_input"))), Is.EqualTo("2B5F61CB57F94E7C7E6D7439FFF260028665853988224E0AD8C08C2FAA61963C8F761654AC529325"));
}
