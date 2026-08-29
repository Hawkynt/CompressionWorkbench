using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class RemainingSourceHashTests {
  [TestCase("", 256, "1EDC77386E20A37C721D6E77ADABB9C4830F199F5ED25284A13C1D84B9FC257A")]
  [TestCase("00", 256, "1BEC89506E75D725BF93BCCFDD6EC81DF05CA281CF5201E3EE0865A7063763EE")]
  [TestCase("0001", 256, "0FE4ED67EA1FF705E94E6D8AF07197728C1FC2D7D5ACCECB8D08CF39AE4D208D")]
  [TestCase("000102030405060708090A0B0C0D0E0F", 256, "572821D80D943E153CBB8C4556C3AD8CF20D77EDAD7998E8CD46F590D8D13EEB")]
  [TestCase("", 512, "6896590A319FDE1F3B18EBAE1DF1E5E8FB0756A878EE9E2165B085FF3AED6805F8F73D5714C75960A6A8095DAE5EF9C00D3F055490D4CF45D4A26B37FD7B5441")]
  [TestCase("00", 512, "DAB16B97C37160586B647B0DCA689794365480324E539CD63F87B119B0C46668DCDE5163A170E06DA9361B05F7CE7645EF68BDC99B3B813B8B1583C5C62D4E4A")]
  [TestCase("0001", 512, "D1982BC43D8C42DCD94C1C7E9611951374DC8BF5E6FC407E8A8DC423F4F0F45909A4AEAA1000B35A8081862E797508807E8763F611AEF1D3C06ECAEDB5229980")]
  [TestCase("000102030405060708090A0B0C0D0E0F", 512, "E743DE651072AE1A078D201373BC383FFAE607545308D268AC663B0B680FEE8BD0D053EA40A55C5DD2AEE281C1CBFFA79152ACC9BD5705F3FB4DAF415458CA12")]
  public void DryGasconMatchesOfficialVectors(string inputHex, int bits, string expected) =>
    Assert.That(Convert.ToHexString(DryGasconHash.Compute(Convert.FromHexString(inputHex), bits)), Is.EqualTo(expected));

  [TestCase("", SkinnyHashVariant.Tk2, "5DC460677EBA0DF3B48C60E949097A6C5D58E1C9ECF97C6FE89212B4B91F246F")]
  [TestCase("00", SkinnyHashVariant.Tk2, "49BC2538DEC23CD247989DE36F83BB730D307C758405EF15F7E97FCB7F7674D9")]
  [TestCase("00010203", SkinnyHashVariant.Tk2, "5557CAA3489858BBF119D7FCF55CDAA1E9817FD647CF68094432A2487D20D377")]
  [TestCase("000102030405060708090A0B0C0D0E0F", SkinnyHashVariant.Tk2, "8E110634307103B6AA92851B083058814F2A64DA807B0824EB8D2865CC6A1447")]
  [TestCase("", SkinnyHashVariant.Tk3, "15C81E6EB26ED692B51CF10A3FE186718C7AA6745CCEB7C82FF63F915F91E27B")]
  [TestCase("00", SkinnyHashVariant.Tk3, "1EFD40A650A042DBEFEF8FD5552F70F52F5224036BFC5483CF1828A62B4C5D59")]
  [TestCase("00010203", SkinnyHashVariant.Tk3, "92AD1CB242B43F9A00F65FEB037ACA2DC98958CA0083D132C944C1FA85C36D8F")]
  [TestCase("000102030405060708090A0B0C0D0E0F", SkinnyHashVariant.Tk3, "A09D8D868ADF68957378C500ADA9678A362897068D9AB00E9483196C318FD4FF")]
  public void SkinnyHashMatchesOfficialVectors(string inputHex, SkinnyHashVariant variant, string expected) =>
    Assert.That(Convert.ToHexString(SkinnyHash.Compute(Convert.FromHexString(inputHex), variant)), Is.EqualTo(expected));

  [TestCase("", "AE77CF13DD2A39B2C1EF44192F01705E9A8E5A67962645737C7661C6DCAE588775C1FD7AC9A3F249D3EADC8661408A4737931867A5E5D2D5E57E8BBBA04D477A")]
  [TestCase("616263", "4F15AC93DD2A39B2C1EF44192F01705E2BBF9A17BFF7BD5BCD47A1B6F57FA0AF0ECECD313534E7BFD3EADC8661408A47472229A78DCC032D95CFBA638864969A")]
  public void JhMatchesRegistryVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Jh.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase(0, "536EC222DE567A90")]
  [TestCase(1, "78DDCDC7AA43AB7E")]
  [TestCase(2, "623DB5B09A56D0B8")]
  public void HighwayHash64MatchesGoogleGoldenVectors(int length, string expected) {
    var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    var data = Enumerable.Range(0, length).Select(static value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(HighwayHash.Compute(data, key, 64)), Is.EqualTo(expected));
  }

  [Test]
  public void HighwayHash128MatchesGoogleEmptyVector() {
    var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(HighwayHash.Compute([], key, 128)),
      Is.EqualTo("C7FE8F9D8F26ED0F6F3E097F765E5633"));
  }

  [Test]
  public void HighwayHash256MatchesGoogleEmptyVector() {
    var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    Assert.That(Convert.ToHexString(HighwayHash.Compute([], key, 256)),
      Is.EqualTo("F574C8C22A4844DD1F35C713730146D9FF1487B9CCBEAEB3F41D75453123DA41"));
  }

  [Test]
  public void RemainingFamiliesExposeTheirFiniteSizes() {
    Assert.Multiple(() => {
      Assert.That(DryGasconHash.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256, 512 }));
      Assert.That(HighwayHash.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 64, 128, 256 }));
      Assert.That(Jh.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
      Assert.That(SkinnyHash.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256 }));
    });
  }
}
