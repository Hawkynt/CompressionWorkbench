using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class GroestlTests {
  [Test]
  public void Groestl224MatchesEmptyVector() =>
    Assert.That(Convert.ToHexString(Groestl224.Compute([])), Is.EqualTo("F2E180FB5947BE964CD584E22E496242C6A329C577FC4CE8C36D34C3"));

  [TestCase("", "1A52D11D550039BE16107F9C58DB9EBCC417F16F736ADB2502567119F0083467")]
  [TestCase("The quick brown fox jumps over the lazy dog", "8C7AD62EB26A21297BC39C2D7293B4BD4D3399FA8AFAB29E970471739E28B301")]
  public void Groestl256MatchesReference(string text, string expected) =>
    Assert.That(Convert.ToHexString(Groestl256.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void Groestl384MatchesEmptyVector() =>
    Assert.That(Convert.ToHexString(Groestl384.Compute([])), Is.EqualTo("AC353C1095ACE21439251007862D6C62F829DDBE6DE4F78E68D310A9205A736D8B11D99BFFE448F57A1CFA2934F044A5"));

  [Test]
  public void Groestl512MatchesEmptyVector() =>
    Assert.That(Convert.ToHexString(Groestl512.Compute([])), Is.EqualTo("6D3AD29D279110EEF3ADBD66DE2A0345A77BAEDE1557F5D099FCE0C03D6DC2BA8E6D4A6633DFBD66053C20FAA87D1A11F39A7FBE4A6C2F009801370308FC4AD8"));
}
