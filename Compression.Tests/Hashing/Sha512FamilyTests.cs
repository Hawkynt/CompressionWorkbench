using System.Text;
using Hawkynt.Algorithms.Hashing;

namespace Compression.Tests.Hashing;

/// <summary>
/// Known answers for SHA-384, SHA-512, SHA-512/224 and SHA-512/256: the FIPS 180-4 example
/// messages (empty, "abc", the 896-bit two-block message), with digests produced by
/// CPython's hashlib (OpenSSL) as oracle.
/// </summary>
[TestFixture]
public class Sha512FamilyTests {
  private const string TwoBlocks =
    "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu";

  [TestCase(512, "", "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e")]
  [TestCase(512, "abc", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
  [TestCase(512, TwoBlocks, "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909")]
  [TestCase(384, "abc", "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7")]
  [TestCase(384, TwoBlocks, "09330c33f71147e83d192fc782cd1b4753111b173b3b05d22fa08086e3b0f712fcc7c71a557e2db966c3e9fa91746039")]
  [TestCase(224, "abc", "4634270f707b6a54daae7530460842e20e37ed265ceee9a43e8924aa")]
  [TestCase(256, "", "c672b8d1ef56ed28ab87c3622c5114069bdd3ad7b8f9737498d0c01ecef0967a")]
  [TestCase(256, "abc", "53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23")]
  [TestCase(256, TwoBlocks, "3928e184fb8690f840da3988121d31be65cb9d3ef83ee6146feac861e19b563a")]
  [Category("Spec")]
  public void KnownAnswer(int bits, string message, string expected)
    => Assert.That(Convert.ToHexStringLower(Sha512Family.Compute(Encoding.ASCII.GetBytes(message), bits)), Is.EqualTo(expected));
}
