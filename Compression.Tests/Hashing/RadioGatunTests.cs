using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class RadioGatunTests {
  [TestCase("", "F30028B54AFAB6B3E55355D277711109A19BEDA7091067E9A492FB5ED9F20117")]
  [TestCase("0", "AF0D3F51B98E90EEEBAE86DD0B304A4003AC5F755FA2CAC2B6866A0A91C5C752")]
  [TestCase("The quick brown fox jumps over the lazy dog", "191589005FEC1F2A248F96A16E9553BF38D0AEE1648FFA036655CE29C2E229AE")]
  public void RadioGatun32MatchesSourceVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(RadioGatun32.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));
}
