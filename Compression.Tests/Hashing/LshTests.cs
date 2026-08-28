using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class LshTests {
  [TestCase("", "48A0D55B2B3D91F26E06F7110FE9CE8EA0E2656BBE344CB1C5930653")]
  [TestCase("CA", "4253E6E91B3C37F75C231D53CA6DC8464885250D2058C41D495BD08F")]
  [TestCase("40EA", "11302CC1282F57A8B107CBF1E495E0E81CAE7561803C039D60E48720")]
  public void Lsh224MatchesCryptoPpVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Lsh224.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "F3CD416A03818217726CB47F4E4D2881C9C29FD445C18B66FB19DEA1A81007C1")]
  [TestCase("CE", "862F86DB654094840D86DF7881732FD69B7227EE4F7943868162FEB733A9CA5B")]
  [TestCase("8B6C", "DA96B21314CFD129FDBAA620DC3D0E2B5B3E087E90E6C147CC6B9950FDE4B40E")]
  public void Lsh256MatchesCryptoPpVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Lsh256.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "DBB259CF22459368AB2C52B3E1C977288B38670ADCB91CAE6B8B6A2D646E76F8BD53E5CAB0E47C856F55249B895C1730")]
  [TestCase("76", "52FF6386AFCE2189733AB9F206DD87774C22C1475B22F4E72CB7F603C1AC54402C63CABE2CF10CF01697A0DA717DE9EC")]
  public void Lsh384MatchesCryptoPpVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Lsh384.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "118A2FF2A99E3B2134125E2BAF20EBE3BDD034D5A69B29C22FC4995063340B46697801D7F7FB0070568F78E8ED514215FC70AF27D6F27B01AA8A1DA72B14CE7C")]
  [TestCase("41", "32E896B21BEC19C15254F7A1F089F748E05918A68E6D829FB1A62B7D5822AD98B7DE274F7DC6C73E6F52C5F0B7633666DBE6048661351D811105EE015B9DCAC9")]
  public void Lsh512MatchesCryptoPpVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Lsh512.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "706DF4EBF100F06D5CC9F6C79BE5297C3F6F515801DD10FBC1B665A2D7BDB653")]
  [TestCase("1A", "415382363239665872545914061D19DF20E803C7446ED603DF0B16142FBCC731")]
  public void Lsh512_256MatchesCryptoPpVectors(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Lsh512_256.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));
}
