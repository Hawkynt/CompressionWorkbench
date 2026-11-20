using System.Diagnostics;
using System.Security.Cryptography;
using Compression.Core.Crypto;
using Hawkynt.Algorithms.Hashing;

namespace Compression.Tests.Hashing;

/// <summary>
/// The incremental Whirlpool and SHA-3 hashers, HMAC with precomputed pads and PBKDF2 on top: equal
/// to the one-shot hashes, to the platform's HMAC/PBKDF2 where it has the hash, and to the
/// RFC-6070-pinned generic PBKDF2; and faster than that generic one.
/// </summary>
[TestFixture]
public class HmacPbkdf2Tests {
  private static byte[] Bytes(int n, int seed) {
    var b = new byte[n];
    new Random(seed).NextBytes(b);
    return b;
  }

  private static IEnumerable<int> Lengths() => [0, 1, 31, 32, 33, 63, 64, 65, 71, 72, 73, 127, 128, 129, 300, 1000];

  [TestCaseSource(nameof(Lengths)), Category("HappyPath")]
  public void GivenDataInPieces_WhenHashedIncrementally_ThenItEqualsTheOneShotHash(int length) {
    var data = Bytes(length, length);
    var rng = new Random(length + 1);
    (IBlockHasher Hasher, Func<byte[], byte[]> OneShot)[] cases = [
      (new WhirlpoolHasher(), static d => Whirlpool.Compute(d)),
      (KeccakHasher.Sha3_512(), static d => Sha3.Compute512(d)),
      (KeccakHasher.Sha3_384(), static d => Sha3.Compute384(d)),
      (KeccakHasher.Sha3_256(), static d => Sha3.Compute256(d)),
    ];
    foreach (var (hasher, oneShot) in cases) {
      for (var at = 0; at < length;) {
        var n = Math.Min(rng.Next(0, 80), length - at);
        hasher.Append(data.AsSpan(at, n));
        at += n;
      }
      var digest = new byte[hasher.HashSize];
      hasher.Finish(digest);
      Assert.That(digest, Is.EqualTo(oneShot(data)), hasher.GetType().Name);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenAHasherCopiedMidway_WhenBothContinue_ThenTheyAgree() {
    var a = new WhirlpoolHasher();
    a.Append(Bytes(100, 1));
    var b = a.CreateEmpty();
    b.CopyFrom(a);
    var tail = Bytes(50, 2);
    a.Append(tail);
    b.Append(tail);
    byte[] da = new byte[64], db = new byte[64];
    a.Finish(da);
    b.Finish(db);
    Assert.That(db, Is.EqualTo(da));
  }

  [TestCase(0)]
  [TestCase(20)]
  [TestCase(72)]
  [TestCase(200)] // longer than the block: hashed first
  [Category("HappyPath")]
  public void GivenKeyAndMessage_WhenHmacSha3_512IsComputed_ThenItMatchesThePlatform(int keyLength) {
    if (!HMACSHA3_512.IsSupported) Assert.Ignore("The platform has no SHA3-512.");
    var key = Bytes(keyLength, keyLength);
    var message = Bytes(333, 9);
    var mac = new byte[64];
    new Hmac(KeccakHasher.Sha3_512(), key).Compute(message, mac);
    Assert.That(mac, Is.EqualTo(HMACSHA3_512.HashData(key, message)));
  }

  [TestCase(1, 66)]
  [TestCase(7, 130)]
  [TestCase(1000, 64)]
  [Category("HappyPath")]
  public void GivenPassword_WhenDerivedWithTheIncrementalHashers_ThenItMatchesTheGenericPbkdf2(int iterations, int length) {
    var password = Bytes(90, 3); // longer than Whirlpool's block
    var salt = Bytes(12, 4);
    Assert.That(Pbkdf2.Derive(new WhirlpoolHasher(), password, salt, iterations, length),
      Is.EqualTo(KeyDerivation.Pbkdf2(static d => Whirlpool.Compute(d), 64, password, salt, iterations, length)));
    Assert.That(Pbkdf2.Derive(KeccakHasher.Sha3_512(), password, salt, iterations, length),
      Is.EqualTo(KeyDerivation.Pbkdf2(static d => Sha3.Compute512(d), 72, password, salt, iterations, length)));
  }

  [Test, Category("Exception")]
  public void GivenZeroIterations_WhenDerived_ThenRefused()
    => Assert.That(() => Pbkdf2.Derive(new WhirlpoolHasher(), [1], [2], 0, 8), Throws.InstanceOf<ArgumentOutOfRangeException>());

  /// <summary>
  /// The key derivations of PEA's TRI* cascades at strength 0 (25,000 Whirlpool and 75,000 SHA3-512
  /// iterations for 66 bytes), timed through the generic hash-function PBKDF2 and through the
  /// precomputed-pad one. Prints both; the precomputed one must be the faster.
  /// </summary>
  [Test, Category("Performance")]
  public void GivenPeaCascadeSettings_WhenDerived_ThenPrecomputedPadsBeatTheGenericPbkdf2() {
    var password = Bytes(30, 5);
    var salt = Bytes(12, 6);
    var generic = Stopwatch.StartNew();
    KeyDerivation.Pbkdf2(static d => Whirlpool.Compute(d), 64, password, salt, 25_000, 66);
    KeyDerivation.Pbkdf2(static d => Sha3.Compute512(d), 72, password, salt, 75_000, 66);
    generic.Stop();
    var precomputed = Stopwatch.StartNew();
    Pbkdf2.Derive(new WhirlpoolHasher(), password, salt, 25_000, 66);
    Pbkdf2.Derive(KeccakHasher.Sha3_512(), password, salt, 75_000, 66);
    precomputed.Stop();
    TestContext.Out.WriteLine($"generic {generic.ElapsedMilliseconds} ms, precomputed pads {precomputed.ElapsedMilliseconds} ms");
    Assert.That(precomputed.Elapsed, Is.LessThan(generic.Elapsed));
  }
}
