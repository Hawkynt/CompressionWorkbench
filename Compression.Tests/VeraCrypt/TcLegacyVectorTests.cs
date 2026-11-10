using FileFormat.VeraCrypt;

namespace Compression.Tests.VeraCrypt;

/// <summary>
/// TrueCrypt 4.3a's own sector encryption as the oracle for the legacy modes whose test images
/// carry no data (see ReferenceVectors/README.md): CBC, outer-CBC and inner-CBC for every cipher
/// and cascade, and LRW over GF(2^128) and GF(2^64).
/// </summary>
[TestFixture]
public sealed class TcLegacyVectorTests {
  private const int Sector = 77, HeaderSectors = 1;

  /// <summary>TrueCrypt 4.3a algorithm id → cryptsetup chain name (ciphers in encryption order).</summary>
  private static readonly Dictionary<int, string> Chains = new() {
    [1] = "aes", [2] = "blowfish_le", [3] = "cast5", [4] = "serpent", [5] = "des3_ede", [6] = "twofish",
    [7] = "twofish-aes", [8] = "serpent-twofish-aes", [9] = "aes-serpent", [10] = "aes-twofish-serpent",
    [11] = "serpent-twofish", [12] = "blowfish_le-aes", [13] = "serpent-blowfish_le-aes",
  };

  public static IEnumerable<TestCaseData> Vectors() {
    using var s = typeof(TcLegacyVectorTests).Assembly.GetManifestResourceStream("VeraCrypt.truecrypt43-legacy-sectors.txt")!;
    using var r = new StreamReader(s);
    while (r.ReadLine() is { Length: > 0 } line) {
      var parts = line.Split(' ');
      var ea = int.Parse(parts[0]);
      var mode = int.Parse(parts[1]);
      yield return new TestCaseData(ea, mode, parts[^1]).SetName($"TrueCrypt43_{parts[2..^1].Aggregate((a, b) => a + b).Replace('-', '_')}_{(mode switch { 1 => "LRW", 2 => "CBC", 3 => "OuterCBC", _ => "InnerCBC" })}");
    }
  }

  private static byte[] Keys() => Enumerable.Range(0, 256).Select(static i => (byte)(i * 7 + 1)).ToArray();
  private static byte[] Plain() => Enumerable.Range(0, 1024).Select(static i => (byte)(i * 13 + 5)).ToArray();

  [TestCaseSource(nameof(Vectors))]
  public void GivenTrueCrypt43Output_WhenWeEncryptAndDecrypt_ThenBothDirectionsMatch(int ea, int mode, string expectedHex) {
    var tcMode = mode switch { 1 => TcMode.Lrw, 2 or 3 => TcMode.CbcOuter, _ => TcMode.CbcInner };
    var chain = TcCipherChain.Find(tcMode, Chains[ea]);
    // TrueCrypt 4.x numbers sectors from the container start; LRW counts from the data area (one header sector in).
    var ivBase = tcMode == TcMode.Lrw ? 0 : HeaderSectors;
    using var cipher = new TcSectorCipher(chain, Keys(), ivBase);

    var data = Plain();
    cipher.Encrypt(data, Sector - HeaderSectors);
    Assert.That(Convert.ToHexStringLower(data), Is.EqualTo(expectedHex));

    cipher.Decrypt(data, Sector - HeaderSectors);
    Assert.That(data, Is.EqualTo(Plain()));
  }
}
