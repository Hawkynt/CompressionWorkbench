using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class PanamaTests {
  [TestCase("", "AA0CC954D757D7AC7779CA3342334CA471ABD47D5952AC91ED837ECD5B16922B")]
  [TestCase("The quick brown fox jumps over the lazy dog", "5F5CA355B90AC622B0AA7E654EF5F27E9E75111415B48B8AFE3ADD1C6B89CBA1")]
  public void PanamaLeMatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(PanamaLE.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [TestCase("", "E81AA04523532DD7267E5C5BC3BA0E289837A62BA032350351980E960A84B0AF")]
  [TestCase("The quick brown fox jumps over the lazy dog", "8FA7DADCE0110F979A0B795E76B2C25628D8BDA88747758149C42E3BC13F85BC")]
  public void PanamaBeMatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(PanamaBE.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));

  [Test]
  public void PanamaLeMacPrependsKey() =>
    Assert.That(
      Convert.ToHexString(PanamaLEMac.Compute(Encoding.ASCII.GetBytes("quick brown fox jumps over the lazy dog"), Encoding.ASCII.GetBytes("The "))),
      Is.EqualTo("5F5CA355B90AC622B0AA7E654EF5F27E9E75111415B48B8AFE3ADD1C6B89CBA1")
    );

  [Test]
  public void PanamaBeMacPrependsKey() =>
    Assert.That(
      Convert.ToHexString(PanamaBEMac.Compute(Encoding.ASCII.GetBytes("quick brown fox jumps over the lazy dog"), Encoding.ASCII.GetBytes("The "))),
      Is.EqualTo("8FA7DADCE0110F979A0B795E76B2C25628D8BDA88747758149C42E3BC13F85BC")
    );
}
