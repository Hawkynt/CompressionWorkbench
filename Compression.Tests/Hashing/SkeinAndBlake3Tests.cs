using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class SkeinAndBlake3Tests {
  [Test]
  public void Skein512MatchesOfficialEmptyVector() =>
    Assert.That(Convert.ToHexString(Skein512.Compute([])), Is.EqualTo("BC5B4C50925519C290CC634277AE3D6257212395CBA733BBAD37A4AF0FA06AF41FCA7903D06564FEA7A2D3730DBDB80C1F85562DFCC070334EA4D1D9E72CBA7A"));

  [TestCase("", "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADC112B7CC9A93CAE41F3262")]
  [TestCase("000102", "E1BE4D7A8AB5560AA4199EEA339849BA8E293D55CA0A81006726D184519E647F")]
  [TestCase("00010203040506", "3F8770F387FAAD08FAA9D8414E9F449AC68E6FF0417F673F602A646A891419FE")]
  public void Blake3MatchesOfficialVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Blake3.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [Test]
  public void Blake3EnhancedSharesCanonicalCore() =>
    Assert.That(Blake3Enhanced.Compute([]), Is.EqualTo(Blake3.Compute([])));
}
