using System.Security.Cryptography;
using System.Text;
using Hawkynt.Algorithms.Hashing;

namespace Compression.Tests.Hashing;

/// <summary>
/// SHA-384, SHA-512, SHA-512/224 and SHA-512/256 against the FIPS 180-4 / NIST CAVP example
/// messages (empty, "abc", the 896-bit two-block message, one million 'a'), HMAC-SHA-384/512
/// against RFC 4231, and differentially against System.Security.Cryptography across every
/// padding boundary.
/// </summary>
/// <remarks>
/// Expected digests are the published NIST / RFC values, re-checked with CPython's hashlib
/// and hmac (OpenSSL). A mistyped round constant (K[76]) once made every digest of this
/// family wrong while the library's own tests stayed green, because none compared against
/// an outside answer.
/// </remarks>
[TestFixture]
public class Sha512FamilyTests {
  private const string TwoBlocks =
    "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu";
  private const string Million = "<million a>";

  private static byte[] Message(string message)
    => message == Million ? Enumerable.Repeat((byte)'a', 1_000_000).ToArray() : Encoding.ASCII.GetBytes(message);

  [TestCase(512, "", "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e")]
  [TestCase(512, "abc", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
  [TestCase(512, TwoBlocks, "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909")]
  [TestCase(512, Million, "e718483d0ce769644e2e42c7bc15b4638e1f98b13b2044285632a803afa973ebde0ff244877ea60a4cb0432ce577c31beb009c5c2c49aa2e4eadb217ad8cc09b")]
  [TestCase(384, "", "38b060a751ac96384cd9327eb1b1e36a21fdb71114be07434c0cc7bf63f6e1da274edebfe76f65fbd51ad2f14898b95b")]
  [TestCase(384, "abc", "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7")]
  [TestCase(384, TwoBlocks, "09330c33f71147e83d192fc782cd1b4753111b173b3b05d22fa08086e3b0f712fcc7c71a557e2db966c3e9fa91746039")]
  [TestCase(384, Million, "9d0e1809716474cb086e834e310a4a1ced149e9c00f248527972cec5704c2a5b07b8b3dc38ecc4ebae97ddd87f3d8985")]
  [TestCase(224, "", "6ed0dd02806fa89e25de060c19d3ac86cabb87d6a0ddd05c333b84f4")]
  [TestCase(224, "abc", "4634270f707b6a54daae7530460842e20e37ed265ceee9a43e8924aa")]
  [TestCase(224, TwoBlocks, "23fec5bb94d60b23308192640b0c453335d664734fe40e7268674af9")]
  [TestCase(224, Million, "37ab331d76f0d36de422bd0edeb22a28accd487b7a8453ae965dd287")]
  [TestCase(256, "", "c672b8d1ef56ed28ab87c3622c5114069bdd3ad7b8f9737498d0c01ecef0967a")]
  [TestCase(256, "abc", "53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23")]
  [TestCase(256, TwoBlocks, "3928e184fb8690f840da3988121d31be65cb9d3ef83ee6146feac861e19b563a")]
  [TestCase(256, Million, "9a59a052930187a97038cae692f30708aa6491923ef5194394dc68d56c74fb21")]
  [Category("Spec")]
  public void KnownAnswer(int bits, string message, string expected)
    => Assert.That(Convert.ToHexStringLower(Sha512Family.Compute(Message(message), bits)), Is.EqualTo(expected));

  [Test, Category("Oracle"), Category("Boundary")]
  public void EveryLengthAroundThePaddingBoundaries_MatchesTheBcl() {
    // 111/112 bytes decide whether the length fits in the last block; 128 is one block.
    var data = new byte[700];
    new Random(512).NextBytes(data);
    var mismatches = new List<int>();
    for (var length = 0; length <= 300; ++length)
      if (!Matches(data.AsSpan(0, length))) mismatches.Add(length);
    foreach (var length in new[] { 383, 384, 385, 639, 640, 700 })
      if (!Matches(data.AsSpan(0, length))) mismatches.Add(length);
    Assert.That(mismatches, Is.Empty);

    static bool Matches(ReadOnlySpan<byte> m)
      => Sha512Family.Compute(m, 512).AsSpan().SequenceEqual(SHA512.HashData(m))
         && Sha512Family.Compute(m, 384).AsSpan().SequenceEqual(SHA384.HashData(m));
  }

  /// <summary>HMAC (RFC 2104) over the family, to exercise it the way keyed consumers do.</summary>
  private static byte[] Hmac(int bits, byte[] key, byte[] data) {
    const int block = 128;
    if (key.Length > block) key = Sha512Family.Compute(key, bits);
    var padded = new byte[block];
    key.CopyTo(padded, 0);
    var inner = padded.Select(b => (byte)(b ^ 0x36)).Concat(data).ToArray();
    var outer = padded.Select(b => (byte)(b ^ 0x5C)).Concat(Sha512Family.Compute(inner, bits)).ToArray();
    return Sha512Family.Compute(outer, bits);
  }

  // RFC 4231 test cases 1-7 (case 5's published value is the first 128 bits of these).
  [TestCase("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", "4869205468657265",
    "87aa7cdea5ef619d4ff0b4241a1d6cb02379f4e2ce4ec2787ad0b30545e17cdedaa833b7d6b8a702038b274eaea3f4e4be9d914eeb61f1702e696c203a126854",
    "afd03944d84895626b0825f4ab46907f15f9dadbe4101ec682aa034c7cebc59cfaea9ea9076ede7f4af152e8b2fa9cb6")]
  [TestCase("4a656665", "7768617420646f2079612077616e7420666f72206e6f7468696e673f",
    "164b7a7bfcf819e2e395fbe73b56e0a387bd64222e831fd610270cd7ea2505549758bf75c05a994a6d034f65f8f0e6fdcaeab1a34d4a6b4b636e070a38bce737",
    "af45d2e376484031617f78d2b58a6b1b9c7ef464f5a01b47e42ec3736322445e8e2240ca5e69e2c78b3239ecfab21649")]
  [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
    "fa73b0089d56a284efb0f0756c890be9b1b5dbdd8ee81a3655f83e33b2279d39bf3e848279a722c806b485a47e67c807b946a337bee8942674278859e13292fb",
    "88062608d3e6ad8a0aa2ace014c8a86f0aa635d947ac9febe83ef4e55966144b2a5ab39dc13814b94e3ab6e101a34f27")]
  [TestCase("0102030405060708090a0b0c0d0e0f10111213141516171819",
    "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
    "b0ba465637458c6990e5a8c5f61d4af7e576d97ff94b872de76f8050361ee3dba91ca5c11aa25eb4d679275cc5788063a5f19741120c4f2de2adebeb10a298dd",
    "3e8a69b7783c25851933ab6290af6ca77a9981480850009cc5577c6e1f573b4e6801dd23c4a7d679ccf8a386c674cffb")]
  [TestCase("0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c", "546573742057697468205472756e636174696f6e",
    "415fad6271580a531d4179bc891d87a650188707922a4fbb36663a1eb16da008711c5b50ddd0fc235084eb9d3364a1454fb2ef67cd1d29fe6773068ea266e96b",
    "3abf34c3503b2a23a46efc619baef897f4c8e42c934ce55ccbae9740fcbc1af4ca62269e2a37cd88ba926341efe4aeea")]
  [TestCase("aa*131", "54657374205573696e67204c6172676572205468616e20426c6f636b2d53697a65204b6579202d2048617368204b6579204669727374",
    "80b24263c7c1a3ebb71493c1dd7be8b49b46d1f41b4aeec1121b013783f8f3526b56d037e05f2598bd0fd2215d6a1e5295e64f73f63f0aec8b915a985d786598",
    "4ece084485813e9088d2c63a041bc5b44f9ef1012a2b588f3cd11f05033ac4c60c2ef6ab4030fe8296248df163f44952")]
  [TestCase("aa*131",
    "5468697320697320612074657374207573696e672061206c6172676572207468616e20626c6f636b2d73697a65206b657920616e642061206c6172676572207468616e20626c6f636b2d73697a6520646174612e20546865206b6579206e6565647320746f20626520686173686564206265666f7265206265696e6720757365642062792074686520484d414320616c676f726974686d2e",
    "e37b6a775dc87dbaa4dfa9f96e5e3ffddebd71f8867289865df5a32d20cdc944b6022cac3c4982b10d5eeb55c3e4de15134676fb6de0446065c97440fa8c6a58",
    "6617178e941f020d351e2f254e8fd32c602420feb0b8fb9adccebb82461e99c5a678cc31e799176d3860e6110c46523e")]
  [Category("Spec"), Category("Oracle")]
  public void HmacRfc4231(string keyHex, string dataHex, string sha512, string sha384) {
    var key = keyHex == "aa*131" ? Enumerable.Repeat((byte)0xAA, 131).ToArray() : Convert.FromHexString(keyHex);
    var data = Convert.FromHexString(dataHex);
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexStringLower(Hmac(512, key, data)), Is.EqualTo(sha512));
      Assert.That(Convert.ToHexStringLower(Hmac(384, key, data)), Is.EqualTo(sha384));
      Assert.That(Convert.ToHexStringLower(HMACSHA512.HashData(key, data)), Is.EqualTo(sha512), "BCL oracle");
      Assert.That(Convert.ToHexStringLower(HMACSHA384.HashData(key, data)), Is.EqualTo(sha384), "BCL oracle");
    });
  }

  [TestCase(0)]
  [TestCase(100)]
  [TestCase(1000)]
  [TestCase(64)]
  [TestCase(128)]
  [Category("ErrorHandling")]
  public void UnsupportedSize_Throws(int bits)
    => Assert.Throws<ArgumentOutOfRangeException>(() => Sha512Family.Compute("abc"u8, bits));
}
