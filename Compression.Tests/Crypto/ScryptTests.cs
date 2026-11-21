using System.Diagnostics;
using System.Text;
using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>scrypt against the RFC 7914 test vectors, its argument checks, and its speed against pea's.</summary>
[TestFixture]
public class ScryptTests {
  // RFC 7914, section 12
  [TestCase("", "", 16, 1, 1,
    "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906")]
  [TestCase("password", "NaCl", 1024, 8, 16,
    "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640")]
  [TestCase("pleaseletmein", "SodiumChloride", 16384, 8, 1,
    "7023bdcb3afd7348461c06cd81fd38ebfda8fbba904f8e3ea9b543f6545da1f2d5432955613f0fcf62d49705242a9af9e61e85dc0d651e40dfcf017b45575887")]
  [Category("HappyPath")]
  public void GivenTheRfc7914Vectors_WhenDerived_ThenTheKeysMatch(string password, string salt, int n, int r, int p, string expected) {
    var key = Scrypt.Derive(Encoding.ASCII.GetBytes(password), Encoding.ASCII.GetBytes(salt), n, r, p, 64);
    Assert.That(Convert.ToHexStringLower(key), Is.EqualTo(expected));
  }

  [TestCase(0, 8, 1)]
  [TestCase(1, 8, 1)]
  [TestCase(1000, 8, 1)] // not a power of two
  [TestCase(1024, 0, 1)]
  [TestCase(1024, 8, 0)]
  [Category("Exception")]
  public void GivenInvalidCosts_WhenDerived_ThenRefused(int n, int r, int p)
    => Assert.That(() => Scrypt.Derive([1], [2], n, r, p, 32), Throws.InstanceOf<ArgumentOutOfRangeException>());

  /// <summary>
  /// The three scrypt runs of a PEA SRI* archive at strength 0 (N = 64 Ki, r = 8, p = 1). PeaZip 11.4's
  /// pea writes such an archive, its start-up and all three included, in 1.6 s (vectors README);
  /// ours must derive in less.
  /// </summary>
  [Test, Category("Performance")]
  public void GivenPeaSriStrength0_WhenDerived_ThenFasterThanPeaWritesTheWholeArchive() {
    byte[] password = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    var watch = Stopwatch.StartNew();
    for (var i = 0; i < 3; ++i) Scrypt.Derive(password, salt, 64 << 10, 8, 1, 66);
    watch.Stop();
    TestContext.Out.WriteLine($"scrypt N=64Ki r=8 p=1, three times: {watch.ElapsedMilliseconds} ms (pea, whole archive: 1600 ms)");
    Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1600));
  }
}
