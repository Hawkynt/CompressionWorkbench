using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class ExoticHashTests {
  [TestCase("", "8596F8DF2E856EC888823DA8CCC914139F31BAEE6AA5C37DBE30BDDBFD75C63CDC205F15F30FAA348E27B5F90495B339A606E3C84BFCDCD55E88B0E178B56FEB")]
  [TestCase("abc", "4A2E21878D2785DFFB751BB0C635E1F5780152922FFE7EF5342F7442D877754A3F866CD5B2D9F2711B02B24F64E437E4484A8D24B7878D288E9C550729FF954E")]
  public void DarkCryptKeccakMatchesSourceVectors(string text, string expected) => Assert.That(Convert.ToHexString(DarkCryptKeccak.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "E3BDE7F708D2006335B09D95A0E8648A87F782E7A1EF17D676D84CC91FE006331749FCF14BF2A4C80AE1AEB52ED0799C8FC9420C59344D4731690E18F7A2CEF3")]
  [TestCase("abc", "1C6233A806832E2C711A5595CDC355B04B81A3F547FFF89E40391399BB925BC845A0CCE9ECC3D1B0439450E079DF51A23D9FDAFE99A85E72D1562BBAE6A1EB46")]
  public void DarkCryptMd6MatchesSourceVectors(string text, string expected) => Assert.That(Convert.ToHexString(DarkCryptMd6.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "D3F7263A09837F4CE5C8EF70A5DDFFAC7B92D6C2ACE5A12265BD5B593260A3FF20D8B4B4C5494E945448B37ABB1FC526F6B46089208FDE938D7F23724C4BDFB7")]
  [TestCase("abc", "C52438C670F3D580DC4CB8D085141A19643668F82A6AD5F4ECB9292F04B8F38F1B9DCC8DC4108F72E6EC81FC6CBCD6EDF1867FC4F0BEAFA692957A4ADC1183E3")]
  public void DarkCryptSkeinMatchesSourceVectors(string text, string expected) => Assert.That(Convert.ToHexString(DarkCryptSkein.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void Gimli24MatchesSourceVector() => Assert.That(Convert.ToHexString(Gimli24Hash.Compute(Convert.FromHexString("00010203"))), Is.EqualTo("AC9BC82B68FE1FC51DB80C67F6751A09F432D0C7E78239C0697468F54AE3F5AA"));

  [Test]
  public void ChcMatchesSourceVector() => Assert.That(Convert.ToHexString(ChcHash.Compute(Encoding.ASCII.GetBytes("hello world"))), Is.EqualTo("CF579DC30A0EEA610D5447C43C06F54E"));

  [TestCase(1, "42E50CD224BACEBA760BDD2BD409281A")]
  [TestCase(2, "2E4679B5ADD9CA7535D87AFEAB33BEE2")]
  public void Mdc2MatchesOpenSslSourceVectors(int paddingType, string expected) => Assert.That(Convert.ToHexString(Mdc2.Compute(Encoding.ASCII.GetBytes("Now is the time for all "), paddingType)), Is.EqualTo(expected));

  [TestCase("", "7346BC14F036E87AE03D0997913088F5F68411434B3CF8B54FA796A80D251F91")]
  public void AsconHashMatchesSourceVectors(string inputHex, string expected) => Assert.That(Convert.ToHexString(AsconHash.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "5D4CBDE6350EA4C174BD65B5B332F8408F99740B81AA02735EAEFBCF0BA0339E")]
  public void AsconXofMatchesSourceVectors(string inputHex, string expected) => Assert.That(Convert.ToHexString(AsconXof.Compute(Convert.FromHexString(inputHex), 32)), Is.EqualTo(expected));

  [TestCase("", "C0E815D78B875DC768C6C8B3AFA51987CD69E5C087D387368628A511CFAD5730")]
  [TestCase("00", "D515FD9C2852D9D6F00C9CF01D858AF467EEDF21FF68CC14C005B3EFF7A6ECD3")]
  [TestCase("00010203", "649D3E5258E504EF842A7176108D36A823E751D5E0EE31E3FAF111415BB9BBC2")]
  public void Esch256MatchesSourceVectors(string inputHex, string expected) => Assert.That(Convert.ToHexString(Esch256.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [Test]
  public void Esch384MatchesSourceVector() => Assert.That(Convert.ToHexString(Esch384.Compute([])), Is.EqualTo("2981715E2263EBD0CB6E5C2C99D0776D5E691EE737FDE05247895E75D02E7447FD6AB707E2EC8385A539777965E472EE"));

  [TestCase("", 32, "1AC2D450FC3B4205D19DA7BFCA1B37513C0803577AC7167F06FE2CE1F0EF39E5")]
  [TestCase("", 64, "1AC2D450FC3B4205D19DA7BFCA1B37513C0803577AC7167F06FE2CE1F0EF39E54269C056B8C82E48276038B6D292966CC07A3D4645272E31FF38508139EB0A71")]
  public void KangarooTwelveMatchesRfcVectors(string inputHex, int outputBytes, string expected) => Assert.That(Convert.ToHexString(KangarooTwelve.Compute(Convert.FromHexString(inputHex), outputBytes)), Is.EqualTo(expected));

  [TestCase("", "4DE2B673C183D1031BBBA5FB63CC15270DAAFBBE1F77FA7FBEAF1D17CF694FEB")]
  [TestCase("00", "91E6735EB598B7FAD5EA99EEA59DC9524C1BDD1FF864108CB5011C28E6572AFB")]
  [TestCase("00010203", "B6F84FCC1C4CF0AF391136BAA0B9ECA326840E8602773354F3D4D63ECC711A48")]
  public void SubterraneanMatchesSourceVectors(string inputHex, string expected) => Assert.That(Convert.ToHexString(SubterraneanHash.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "EA152F2B47BCE24EFB66C479D4ADF17BD324D806E85FF75EE369EE50DC8F8BD1")]
  [TestCase("00", "27921F8DDF392894460B70B3ED6C091E6421B7D2147DCD6031D7EFEBAD3030CC")]
  public void XoodyakMatchesSourceVectors(string inputHex, string expected) => Assert.That(Convert.ToHexString(XoodyakHash.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [Test]
  public void CubeHash256MatchesSourceVector() => Assert.That(Convert.ToHexString(CubeHash256.Compute([])), Is.EqualTo("44C6DE3AC6C73C391BF0906CB7482600EC06B216C7C54A2A8688A6A42676577D"));

  [Test]
  public void CubeHash512MatchesSourceVector() => Assert.That(Convert.ToHexString(CubeHash512.Compute([])), Is.EqualTo("4A1D00BBCFCB5A9562FB981E7F7DB3350FE2658639D948B9D57452C22328BB32F468B072208450BAD5EE178271408BE0B16E5633AC8A1E3CF9864CFBFC8E043A"));

  [TestCase("", "69217A3079908094E11121D042354A7C1F55B6482CA1A51E1B250DFD1ED0EEF9")]
  [TestCase("abc", "508C5E8C327C14E2E1A72BA34EEB452F37458B209ED63A294D999B4C86675982")]
  public void Blake2sMatchesRfcVectors(string text, string expected) => Assert.That(Convert.ToHexString(Blake2s.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void Blake2xsMatchesSourceVector() {
    var input = Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray();
    Assert.That(Convert.ToHexString(Blake2xs.Compute(input, 32)), Is.EqualTo("91CAB802B466092897C7639A02ACF529CA61864E5E8C8E422B3A9381A95154D1"));
  }
}
