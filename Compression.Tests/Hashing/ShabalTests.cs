using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class ShabalTests {
  [TestCase("", "B09ADA740BA5C9B2B1CECA00D247782FF61635A541BCDA20")]
  [TestCase("abc", "3D332F26B12BE360CE530DA8446AE4E3236167148ACD5ABF")]
  public void Shabal192MatchesReference(string text,string expected) => Assert.That(Convert.ToHexString(Shabal192.Compute(Encoding.ASCII.GetBytes(text))),Is.EqualTo(expected));

  [TestCase("", "9BA48FF8698B52AF7FF8BF6907D1F583D25995584F6A6666ADECF77C")]
  [TestCase("abc", "1EF493C9A9B6F29ECD8325C4FF614A8E03FE9BADF66BC2270711D1D7")]
  public void Shabal224MatchesReference(string text,string expected) => Assert.That(Convert.ToHexString(Shabal224.Compute(Encoding.ASCII.GetBytes(text))),Is.EqualTo(expected));

  [TestCase("", "E423F8B7B92D7B56BC904BCD77FC2724D428D633775CE9CCC3E24672E3EA5900")]
  [TestCase("abc", "16FCE961D2912AABBB68666C4AD6CC33A10FCB5242BF202835B3F630135E7E1A")]
  public void Shabal256MatchesReference(string text,string expected) => Assert.That(Convert.ToHexString(Shabal256.Compute(Encoding.ASCII.GetBytes(text))),Is.EqualTo(expected));

  [Test]
  public void Shabal384MatchesReference() => Assert.That(Convert.ToHexString(Shabal384.Compute([])),Is.EqualTo("89A352FAC1AA5E3B352DD0583EC3150F39DA60A37D54BA5D3DDD462CE1C6C8FF44C8CE63597C7D4527F5D4FAE0A360E2"));

  [Test]
  public void Shabal512MatchesReference() => Assert.That(Convert.ToHexString(Shabal512.Compute([])),Is.EqualTo("5D96AFA391E772147EA97C86B7E62F699C559D0D5FBC8A3CBA11BF2E856398232C7033C163A058778F9FFC7576AD72BE95AB38475D5940F748CA99C8A3D5BA55"));
}
