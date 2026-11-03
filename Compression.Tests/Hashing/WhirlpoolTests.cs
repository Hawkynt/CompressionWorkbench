using Hawkynt.Algorithms.Hashing;

namespace Compression.Tests.Hashing;

/// <summary>
/// Whirlpool against the ISO/NESSIE vectors (cross-checked with OpenSSL's legacy provider), at the
/// tail lengths around its padding boundary: 0x80 plus a 256-bit length needs a second final block
/// once 32 or more message bytes are left over.
/// </summary>
[TestFixture]
public class WhirlpoolTests {
  [TestCase("", "19fa61d75522a4669b44e39c1d2e1726c530232130d407f89afee0964997f7a73e83be698b288febcf88e3e03c4f0757ea8964e59b63d93708b138cc42a66eb3")]
  [TestCase("abc", "4e2448a4c6f486bb16b6562c73b4020bf3043e3a731bce721ae1b303d97e6d4c7181eebdb6c57e277d0e34957114cbd6c797fc9d95d8b582d225292076d4eef5")]
  [TestCase("The quick brown fox jumps over the lazy dog", "b97de512e91e3828b40d2b0fdce9ceb3c4a71f9bea8d88e75c4fa854df36725fd2b52eb6544edcacd6f8beddfea403cb55ae31f03ad62a5ef54e42ee82c3fb35")]
  [Category("HappyPath")]
  public void GivenReferenceInput_WhenHashed_ThenMatchesTheReferenceDigest(string input, string expected)
    => Assert.That(Convert.ToHexStringLower(Whirlpool.Compute(System.Text.Encoding.ASCII.GetBytes(input))), Is.EqualTo(expected));

  [Test, Category("Boundary")]
  public void GivenOneBlockPlus41Bytes_WhenHashed_ThenTheTailNeedsTwoPaddingBlocks()
    => Assert.That(Convert.ToHexStringLower(Whirlpool.Compute(Enumerable.Repeat((byte)'a', 105).ToArray())),
      Is.EqualTo("d352ca65c7d77ae0c27a8e1ce3b8e887f05bef753e28c7b7c7ff112f1ff4c1bcaf83f749ad1e6e397d210ea14da44c4c5615a77831ad80a132e3a67cef2b1878"));
}
