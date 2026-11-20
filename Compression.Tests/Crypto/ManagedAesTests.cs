using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// <see cref="AesBlockCipher"/> and <see cref="ManagedAes"/> against the known-answer vectors of FIPS-197 and
/// SP 800-38A, the table engine against the AES-NI one, and <see cref="ManagedAes"/> against .NET's own AES
/// (the operating system's) as the oracle for every mode, padding and length the library meets — through the
/// one-shot methods, <see cref="CryptoStream"/> and the raw transform, failures included.
/// </summary>
[TestFixture]
public sealed class ManagedAesTests {
  private const string Plain = "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710";
  private const string Key128 = "2b7e151628aed2a6abf7158809cf4f3c";
  private const string Iv = "000102030405060708090a0b0c0d0e0f";

  private static IEnumerable<bool> Engines() => AesBlockCipher.IsHardwareAccelerated ? [false, true] : [false];

  // FIPS-197 Appendix C.1-C.3.
  [TestCase("000102030405060708090a0b0c0d0e0f", "69c4e0d86a7b0430d8cdb78070b4c55a")]
  [TestCase("000102030405060708090a0b0c0d0e0f1011121314151617", "dda97ca4864cdfe06eaf70a0ec0d7191")]
  [TestCase("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "8ea2b7ca516745bfeafc49904b496089")]
  public void GivenTheFips197Example_WhenEitherEngineRunsIt_ThenTheCiphertextMatchesAndDecryptsBack(string key, string cipher) {
    foreach (var hardware in Engines()) {
      var aes = new AesBlockCipher(Convert.FromHexString(key), hardware);
      var block = Convert.FromHexString("00112233445566778899aabbccddeeff");
      aes.Encrypt(block);
      Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher), $"hardware: {hardware}");
      aes.Decrypt(block);
      Assert.That(Convert.ToHexStringLower(block), Is.EqualTo("00112233445566778899aabbccddeeff"), $"hardware: {hardware}");
    }
  }

  // SP 800-38A F.1.1, F.2.1, F.3.13 (AES-128).
  [TestCase(CipherMode.ECB, 128, "3ad77bb40d7a3660a89ecaf32466ef97f5d3d58503b9699de785895a96fdbaaf43b1cd7f598ece23881b00e3ed0306887b0c785e27e8ad3f8223207104725dd4")]
  [TestCase(CipherMode.CBC, 128, "7649abac8119b246cee98e9b12e9197d5086cb9b507219ee95db113a917678b273bed6b8e3c1743b7116e69e222295163ff1caa1681fac09120eca307586e1a7")]
  [TestCase(CipherMode.CFB, 128, "3b3fd92eb72dad20333449f8e83cfb4ac8a64537a0b3a93fcde3cdad9f1ce58b26751f67a3cbb140b1808cf187a4f4dfc04b05357c5d1c0eeac4c66f9ff7f2e6")]
  public void GivenAnSp80038aVector_WhenManagedAesRunsTheMode_ThenTheCiphertextMatches(CipherMode mode, int feedback, string cipher) {
    using var aes = ManagedAes.Create();
    aes.Key = Convert.FromHexString(Key128);
    var plain = Convert.FromHexString(Plain);
    var iv = Convert.FromHexString(Iv);
    var encrypted = mode switch {
      CipherMode.ECB => aes.EncryptEcb(plain, PaddingMode.None),
      CipherMode.CBC => aes.EncryptCbc(plain, iv, PaddingMode.None),
      _ => aes.EncryptCfb(plain, iv, PaddingMode.None, feedback),
    };
    Assert.That(Convert.ToHexStringLower(encrypted), Is.EqualTo(cipher));
    var decrypted = mode switch {
      CipherMode.ECB => aes.DecryptEcb(encrypted, PaddingMode.None),
      CipherMode.CBC => aes.DecryptCbc(encrypted, iv, PaddingMode.None),
      _ => aes.DecryptCfb(encrypted, iv, PaddingMode.None, feedback),
    };
    Assert.That(Convert.ToHexStringLower(decrypted), Is.EqualTo(Plain));
  }

  // SP 800-38A F.3.7 (CFB8-AES128).
  [Test]
  public void GivenTheSp80038aCfb8Vector_WhenManagedAesEncrypts_ThenTheCiphertextMatches() {
    using var aes = ManagedAes.Create();
    aes.Key = Convert.FromHexString(Key128);
    var encrypted = aes.EncryptCfb(Convert.FromHexString(Plain[..36]), Convert.FromHexString(Iv), PaddingMode.None, 8);
    Assert.That(Convert.ToHexStringLower(encrypted), Is.EqualTo("3b79424c9c0dd436bace9e0ed4586a4f32b9"));
  }

  [Test]
  public void GivenRandomKeysAndBlocks_WhenTheTableEngineAndAesNiRunThem_ThenTheyAgree() {
    if (!AesBlockCipher.IsHardwareAccelerated)
      Assert.Ignore("this processor has no AES instructions");
    var random = new Random(1197);
    foreach (var keyLength in new[] { 16, 24, 32 })
      for (var round = 0; round < 50; ++round) {
        var key = new byte[keyLength];
        random.NextBytes(key);
        var data = new byte[16 * random.Next(1, 40)];
        random.NextBytes(data);
        var table = new AesBlockCipher(key, false);
        var hardware = new AesBlockCipher(key, true);
        Assert.That(hardware.UsesHardware, Is.True);
        var a = data.ToArray();
        var b = data.ToArray();
        table.Encrypt(a);
        hardware.Encrypt(b);
        Assert.That(b, Is.EqualTo(a), $"encrypt, {keyLength}-byte key");
        table.Decrypt(a);
        hardware.Decrypt(b);
        Assert.That(a, Is.EqualTo(data), $"table round trip, {keyLength}-byte key");
        Assert.That(b, Is.EqualTo(data), $"AES-NI round trip, {keyLength}-byte key");

        // The chained modes, whose AES-NI paths keep the chaining value in a register and run the
        // parallel directions eight blocks deep, against the table engine's block-by-block chaining.
        var iv = new byte[16];
        random.NextBytes(iv);
        foreach (var (name, encrypt, decrypt) in new (string, Action<AesBlockCipher, byte[], byte[]>, Action<AesBlockCipher, byte[], byte[]>)[] {
          ("CBC", (x, d, v) => x.EncryptCbc(d, v), (x, d, v) => x.DecryptCbc(d, v)),
          ("CFB", (x, d, v) => x.EncryptCfb(d, v), (x, d, v) => x.DecryptCfb(d, v)),
        }) {
          var (ta, ha, tv, hv) = (data.ToArray(), data.ToArray(), iv.ToArray(), iv.ToArray());
          encrypt(table, ta, tv);
          encrypt(hardware, ha, hv);
          Assert.That(ha, Is.EqualTo(ta), $"{name} encrypt, {keyLength}-byte key, {data.Length} bytes");
          Assert.That(hv, Is.EqualTo(tv), $"{name} chaining value after encrypting");
          (tv, hv) = (iv.ToArray(), iv.ToArray());
          decrypt(table, ta, tv);
          decrypt(hardware, ha, hv);
          Assert.That(ta, Is.EqualTo(data), $"{name} table round trip");
          Assert.That(ha, Is.EqualTo(data), $"{name} AES-NI round trip");
          Assert.That(hv, Is.EqualTo(tv), $"{name} chaining value after decrypting");
        }
      }
  }

  private static readonly (CipherMode Mode, int Feedback)[] Modes = [(CipherMode.ECB, 128), (CipherMode.CBC, 128), (CipherMode.CFB, 8), (CipherMode.CFB, 128)];
  private static readonly PaddingMode[] Paddings = [PaddingMode.None, PaddingMode.PKCS7, PaddingMode.Zeros, PaddingMode.ANSIX923, PaddingMode.ISO10126];

  private static byte[] OneShot(SymmetricAlgorithm aes, bool encrypt, CipherMode mode, int feedback, byte[] data, byte[] iv, PaddingMode padding) => (mode, encrypt) switch {
    (CipherMode.ECB, true) => aes.EncryptEcb(data, padding),
    (CipherMode.ECB, false) => aes.DecryptEcb(data, padding),
    (CipherMode.CBC, true) => aes.EncryptCbc(data, iv, padding),
    (CipherMode.CBC, false) => aes.DecryptCbc(data, iv, padding),
    (_, true) => aes.EncryptCfb(data, iv, padding, feedback),
    _ => aes.DecryptCfb(data, iv, padding, feedback),
  };

  /// <summary>What a call answers: its bytes, or the type of what it threw.</summary>
  private static string Outcome(Func<byte[]> call) {
    try {
      return Convert.ToHexStringLower(call());
    } catch (Exception e) {
      return e.GetType().Name;
    }
  }

  /// <summary>
  /// What a decryption of arbitrary bytes answers, a refusal of any kind read as one: .NET's ANSI X.923
  /// check slices before it knows the pad fits, so a pad longer than the text comes out of it as
  /// ArgumentOutOfRangeException, where ManagedAes says CryptographicException, as for every other bad pad.
  /// </summary>
  private static string Refusal(Func<byte[]> call) {
    var outcome = Outcome(call);
    return outcome is nameof(CryptographicException) or nameof(ArgumentOutOfRangeException) ? "refused" : outcome;
  }

  private static (Aes Ours, Aes Theirs) Pair(byte[] key) {
    var ours = ManagedAes.Create();
    var theirs = Aes.Create();
    ours.Key = key;
    theirs.Key = key;
    return (ours, theirs);
  }

  [Test]
  public void GivenEveryModePaddingAndLength_WhenTheOneShotMethodsRun_ThenManagedAesAnswersAsTheFrameworkDoes() {
    var random = new Random(2001);
    foreach (var keyLength in new[] { 16, 24, 32 })
      foreach (var (mode, feedback) in Modes)
        foreach (var padding in Paddings)
          foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 64, 100, 255, 256, 1000 }) {
            var key = new byte[keyLength];
            var iv = new byte[16];
            var data = new byte[length];
            random.NextBytes(key);
            random.NextBytes(iv);
            random.NextBytes(data);
            var (ours, theirs) = Pair(key);
            using (ours)
            using (theirs) {
              var what = $"{mode}{(mode == CipherMode.CFB ? feedback : "")} {padding} {keyLength * 8}-bit, {length} bytes";
              // ISO 10126 pads with random bytes: the ciphertexts differ by design, each must decrypt with the other.
              if (padding != PaddingMode.ISO10126)
                Assert.That(Outcome(() => OneShot(ours, true, mode, feedback, data, iv, padding)), Is.EqualTo(Outcome(() => OneShot(theirs, true, mode, feedback, data, iv, padding))), "encrypt " + what);
              string Cross(Aes from, Aes to) => Outcome(() => OneShot(to, false, mode, feedback, OneShot(from, true, mode, feedback, data, iv, padding), iv, padding));
              Assert.That(Cross(ours, theirs), Is.EqualTo(Cross(theirs, theirs)), "ours encrypts, the framework decrypts " + what);
              Assert.That(Cross(theirs, ours), Is.EqualTo(Cross(theirs, theirs)), "the framework encrypts, ours decrypts " + what);
              // Arbitrary ciphertext: wrong paddings, odd lengths — refused or read the same way.
              Assert.That(Refusal(() => OneShot(ours, false, mode, feedback, data, iv, padding)), Is.EqualTo(Refusal(() => OneShot(theirs, false, mode, feedback, data, iv, padding))), "decrypt arbitrary " + what);
            }
          }
  }

  private static byte[] Streamed(ICryptoTransform transform, byte[] data, Random random) {
    using var sink = new MemoryStream();
    using (var stream = new CryptoStream(sink, transform, CryptoStreamMode.Write, leaveOpen: true))
      for (var at = 0; at < data.Length;) {
        var take = Math.Min(data.Length - at, random.Next(0, 70));
        stream.Write(data, at, take);
        at += take;
      }
    return sink.ToArray();
  }

  [Test]
  public void GivenEveryModeAndPadding_WhenACryptoStreamWritesInRandomPieces_ThenManagedAesAnswersAsTheFrameworkDoes() {
    var random = new Random(3011);
    foreach (var (mode, feedback) in Modes)
      foreach (var padding in Paddings)
        foreach (var length in new[] { 0, 1, 16, 17, 48, 333, 4096 }) {
          var key = new byte[32];
          var iv = new byte[16];
          var data = new byte[length];
          random.NextBytes(key);
          random.NextBytes(iv);
          random.NextBytes(data);
          var (ours, theirs) = Pair(key);
          using (ours)
          using (theirs) {
            foreach (var aes in new[] { ours, theirs }) {
              aes.Mode = mode;
              aes.Padding = padding;
              aes.FeedbackSize = feedback;
              aes.IV = iv;
            }
            var what = $"{mode}{(mode == CipherMode.CFB ? feedback : "")} {padding}, {length} bytes";
            var seed = random.Next();
            string Encrypted(Aes aes) => Outcome(() => Streamed(aes.CreateEncryptor(), data, new Random(seed)));
            if (padding != PaddingMode.ISO10126)
              Assert.That(Encrypted(ours), Is.EqualTo(Encrypted(theirs)), "encrypt " + what);
            string Cross(Aes from, Aes to) => Outcome(() => Streamed(to.CreateDecryptor(), Streamed(from.CreateEncryptor(), data, new Random(seed)), new Random(seed + 1)));
            Assert.That(Cross(ours, ours), Is.EqualTo(Cross(theirs, theirs)), "round trip " + what);
            Assert.That(Cross(theirs, ours), Is.EqualTo(Cross(theirs, theirs)), "the framework encrypts, ours decrypts " + what);
            Assert.That(Refusal(() => Streamed(ours.CreateDecryptor(), data, new Random(seed))), Is.EqualTo(Refusal(() => Streamed(theirs.CreateDecryptor(), data, new Random(seed)))), "decrypt arbitrary " + what);
          }
        }
  }

  [Test]
  public void GivenATransform_WhenItIsDrivenBlockByBlockAndReused_ThenItAnswersAsTheFrameworkDoes() {
    var random = new Random(4243);
    foreach (var (mode, feedback) in Modes)
      foreach (var padding in Paddings) {
        var key = new byte[16];
        var iv = new byte[16];
        var data = new byte[96];
        random.NextBytes(key);
        random.NextBytes(iv);
        random.NextBytes(data);
        var (ours, theirs) = Pair(key);
        using (ours)
        using (theirs) {
          foreach (var aes in new[] { ours, theirs }) {
            aes.Mode = mode;
            aes.Padding = padding;
            aes.FeedbackSize = feedback;
          }
          var what = $"{mode}{(mode == CipherMode.CFB ? feedback : "")} {padding}";
          // A second pass reuses the transform, which only a pass that ended well promises: after a refused
          // padding .NET keeps the block it held back, ManagedAes starts over clean.
          string Drive(ICryptoTransform t) {
            var parts = new List<string> { $"{t.InputBlockSize}/{t.OutputBlockSize}/{t.CanTransformMultipleBlocks}/{t.CanReuseTransform}" };
            for (var pass = 0; pass < 2; ++pass) {
              var output = new byte[data.Length + 32];
              var written = t.TransformBlock(data, 0, 64, output, 0);
              parts.Add(Convert.ToHexStringLower(output.AsSpan(0, written)));
              var final = Refusal(() => t.TransformFinalBlock(data, 64, 32));
              parts.Add(final);
              if (final == "refused")
                break;
            }
            return string.Join(" ", parts);
          }
          using (var a = ours.CreateDecryptor(key, iv))
          using (var b = theirs.CreateDecryptor(key, iv))
            Assert.That(Drive(a), Is.EqualTo(Drive(b)), "decryptor " + what);
          if (padding != PaddingMode.ISO10126)
            using (var a = ours.CreateEncryptor(key, iv))
            using (var b = theirs.CreateEncryptor(key, iv))
              Assert.That(Drive(a), Is.EqualTo(Drive(b)), "encryptor " + what);
        }
      }
  }

  [Test]
  public void GivenAKeyThatChanges_WhenManagedAesIsUsedAgain_ThenItUsesTheNewKey() {
    using var ours = ManagedAes.Create();
    using var theirs = Aes.Create();
    var block = new byte[16];
    foreach (var key in new[] { new byte[16], Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(7, 24).Select(i => (byte)i).ToArray() }) {
      ours.Key = key;
      theirs.Key = key;
      Assert.That(ours.EncryptEcb(block, PaddingMode.None), Is.EqualTo(theirs.EncryptEcb(block, PaddingMode.None)), $"{key.Length * 8}-bit key");
    }
  }

  [Test]
  public void GivenAModeAesDoesNotHave_WhenATransformIsAskedFor_ThenItIsRefusedAsTheFrameworkRefusesIt() {
    foreach (var mode in new[] { CipherMode.OFB, CipherMode.CTS }) {
      using var ours = ManagedAes.Create();
      using var theirs = Aes.Create();
      string Ask(Aes aes) {
        try {
          aes.Mode = mode;
          aes.CreateEncryptor().Dispose();
          return "made";
        } catch (Exception e) {
          return e.GetType().Name;
        }
      }
      Assert.That(Ask(ours), Is.EqualTo(Ask(theirs)), mode.ToString());
    }
  }
}
