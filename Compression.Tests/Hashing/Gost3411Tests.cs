using System.Text;
using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class Gost3411Tests {
  [TestCase("", "981E5F3CA30C841487830F84FB433E13AC1101569B9C13584AC483234CD656C0")]
  [TestCase("This is message, length=32 bytes", "2CEFC2F7B7BDC514E18EA57FA74FF357E7FA17D652C75F69CB1BE7893EDE48EB")]
  [TestCase("Suppose the original message has length = 50 bytes", "C3730C5CBCCACF915AC292676F21E8BD4EF75331D9405E5F1A61DC3130A65011")]
  [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "73B70A39497DE53A6E08C67B6D4DB853540F03E9389299D9B0156EF7E85D0F61")]
  public void Gost3411MatchesBouncyCastleVectors(string text, string expected) =>
    Assert.That(Convert.ToHexString(Gost3411_94.Compute(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected));
}
