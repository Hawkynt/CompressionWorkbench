using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class EchoTests {
  [TestCase("", "17DA087595166F733FFF7CDB0BCA6438F303D0E00C48B5E7A3075905")]
  [TestCase("CC", "34D81C434B63C8FBCF023B6417AF87D906942EBD7B56C1D7B08BADDC")]
  public void Echo224MatchesSphlib(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Echo224.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "4496CD09D425999AEFA75189EE7FD3C97362AA9E4CA898328002D20A4B519788")]
  [TestCase("CC", "01C382B5B9D7D10EC36C98785C27EACCFB2F772A7E58B6B97BF62212B8584AE5")]
  public void Echo256MatchesSphlib(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Echo256.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "134040763F840559B84B7A1AE5D6D64FC3659821A789CC64A7F1444C09EE7F81A54D72BEEE8273BAE5EF18EC43AA5F34")]
  [TestCase("CC", "90875A2649CAB90018FF8AECD334482C92B15D76B378574EEAACD3B7598020DB11E2C7480614EEA8793DE3DAF2093F73")]
  public void Echo384MatchesSphlib(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Echo384.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));

  [TestCase("", "158F58CC79D300A9AA292515049275D051A28AB931726D0EC44BDD9FAEF4A702C36DB9E7922FFF077402236465833C5CC76AF4EFC352B4B44C7FA15AA0EF234E")]
  [TestCase("CC", "DFCE37CA6F32BA4C3A72E77BCA20E511A39B31A6075815F083DB2ECFD5C32CFD6A4E0DD9BD51921199758EDD2FE8ED0FA31E06AA821C7030653D15408E8728DD")]
  public void Echo512MatchesSphlib(string inputHex, string expected) =>
    Assert.That(Convert.ToHexString(Echo512.Compute(Convert.FromHexString(inputHex))), Is.EqualTo(expected));
}
