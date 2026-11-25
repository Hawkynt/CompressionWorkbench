#pragma warning disable CS1591
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;
using Compression.Core.Crypto.OpenPgp;

namespace Compression.Tests.Crypto;

/// <summary>
/// The primitives of public-key OpenPGP against their specifications' test vectors — X25519
/// (RFC 7748), AES key wrap (RFC 3394), OCB (RFC 7253), S2K and the version 1 AEAD packet
/// (draft-ietf-openpgp-rfc4880bis-10, appendix A) — and the message layer against messages and
/// keys GnuPG 2.4.4 made (<c>OpenPgpVectors/README.md</c>).
/// </summary>
[TestFixture]
public class OpenPgpTests {
  private const string Passphrase = "keypass";
  private static readonly byte[] Plain = "hello openpgp\n"u8.ToArray();

  internal static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OpenPgpVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded OpenPGP vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // ── X25519 (RFC 7748 sections 5.2 and 6.1) ────────────────────────────────

  [TestCase("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4",
    "e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c",
    "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")]
  [TestCase("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d",
    "e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493",
    "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957")]
  [Category("HappyPath")]
  public void GivenRfc7748Vector_WhenX25519IsComputed_ThenTheOutputMatches(string scalar, string u, string expected)
    => Assert.That(Convert.ToHexStringLower(X25519.ScalarMultiply(Convert.FromHexString(scalar), Convert.FromHexString(u))), Is.EqualTo(expected));

  [Test, Category("HappyPath")]
  public void GivenRfc7748DiffieHellmanVector_WhenBothSidesAgree_ThenTheSharedSecretMatches() {
    var a = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
    var b = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
    Assert.That(Convert.ToHexStringLower(X25519.PublicKey(a)), Is.EqualTo("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
    Assert.That(Convert.ToHexStringLower(X25519.PublicKey(b)), Is.EqualTo("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"));
    const string shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
    Assert.That(Convert.ToHexStringLower(X25519.ScalarMultiply(a, X25519.PublicKey(b))), Is.EqualTo(shared));
    Assert.That(Convert.ToHexStringLower(X25519.ScalarMultiply(b, X25519.PublicKey(a))), Is.EqualTo(shared));
  }

  [TestCase(31, 32)]
  [TestCase(32, 33)]
  [Category("Exception")]
  public void GivenWrongLength_WhenX25519IsComputed_ThenRefused(int scalar, int u)
    => Assert.That(() => X25519.ScalarMultiply(new byte[scalar], new byte[u]), Throws.ArgumentException);

  // ── AES key wrap (RFC 3394 section 4) ─────────────────────────────────────

  [TestCase("000102030405060708090A0B0C0D0E0F", "00112233445566778899AABBCCDDEEFF", "1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5")]
  [TestCase("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
    "00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
    "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21")]
  [Category("HappyPath")]
  public void GivenRfc3394Vector_WhenWrappedAndUnwrapped_ThenBothMatch(string kek, string data, string wrapped) {
    Assert.That(Convert.ToHexString(AesKeyWrap.Wrap(Convert.FromHexString(kek), Convert.FromHexString(data))), Is.EqualTo(wrapped));
    Assert.That(Convert.ToHexString(AesKeyWrap.Unwrap(Convert.FromHexString(kek), Convert.FromHexString(wrapped))), Is.EqualTo(data));
  }

  [Test, Category("Exception")]
  public void GivenWrappedKeyWithAFlippedBit_WhenUnwrapped_ThenTheIntegrityCheckFails() {
    var wrapped = Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5");
    wrapped[9] ^= 1;
    Assert.That(() => AesKeyWrap.Unwrap(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), wrapped), Throws.InstanceOf<CryptographicException>());
  }

  [TestCase(8)]
  [TestCase(20)]
  [Category("Exception")]
  public void GivenKeyDataNotWholeBlocks_WhenWrapped_ThenRefused(int length)
    => Assert.That(() => AesKeyWrap.Wrap(new byte[16], new byte[length]), Throws.ArgumentException);

  // ── OCB (RFC 7253 appendix A) ─────────────────────────────────────────────

  private static IEnumerable<TestCaseData> OcbVectors() {
    var seq = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
    (int A, int P, string C)[] cases = [
      (0, 0, "785407BFFFC8AD9EDCC5520AC9111EE6"),
      (8, 8, "6820B3657B6F615A5725BDA0D3B4EB3A257C9AF1F8F03009"),
      (8, 0, "81017F8203F081277152FADE694A0A00"),
      (0, 8, "45DD69F8F5AAE72414054CD1F35D82760B2CD00D2F99BFA9"),
      (16, 16, "571D535B60B277188BE5147170A9A22C3AD7A4FF3835B8C5701C1CCEC8FC3358"),
      (16, 0, "8CF761B6902EF764462AD86498CA6B97"),
      (0, 16, "5CE88EC2E0692706A915C00AEB8B2396F40E1C743F52436BDF06D8FA1ECA343D"),
      (24, 24, "1CA2207308C87C010756104D8840CE1952F09673A448A122C92C62241051F57356D7F3C90BB0E07F"),
      (24, 0, "6DC225A071FC1B9F7C69F93B0F1E10DE"),
      (0, 24, "221BD0DE7FA6FE993ECCD769460A0AF2D6CDED0C395B1C3CE725F32494B9F914D85C0B1EB38357FF"),
      (32, 32, "BD6F6C496201C69296C11EFD138A467ABD3C707924B964DEAFFC40319AF5A48540FBBA186C5553C68AD9F592A79A4240"),
      (32, 0, "FE80690BEE8A485D11F32965BC9D2A32"),
      (0, 32, "2942BFC773BDA23CABC6ACFD9BFD5835BD300F0973792EF46040C53F1432BCDFB5E1DDE3BC18A5F840B52E653444D5DF"),
      (40, 40, "D5CA91748410C1751FF8A2F618255B68A0A12E093FF454606E59F9C1D0DDC54B65E8628E568BAD7AED07BA06A4A69483A7035490C5769E60"),
      (40, 0, "C5CD9D1850C141E358649994EE701B68"),
      (0, 40, "4412923493C57D5DE0D700F753CCE0D1D2D95060122E9F15A5DDBFC5787E50B5CC55EE507BCB084E479AD363AC366B95A98CA5F3000B1479"),
    ];
    for (var i = 0; i < cases.Length; ++i)
      yield return new TestCaseData($"BBAA998877665544332211{i:X2}", seq[..cases[i].A], seq[..cases[i].P], cases[i].C)
        .SetName($"GivenRfc7253Vector{i}_WhenOcbEncryptsAndDecrypts_ThenCiphertextAndTagMatch");
  }

  [TestCaseSource(nameof(OcbVectors)), Category("HappyPath")]
  public void OcbVector(string nonce, byte[] associated, byte[] plaintext, string expected) {
    var ocb = Ocb.WithAes(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"));
    var n = Convert.FromHexString(nonce);
    var c = new byte[plaintext.Length];
    var tag = new byte[16];
    ocb.Encrypt(n, associated, plaintext, c, tag);
    Assert.That(Convert.ToHexString([.. c, .. tag]), Is.EqualTo(expected));
    var back = new byte[c.Length];
    ocb.Decrypt(n, associated, c, tag, back);
    Assert.That(back, Is.EqualTo(plaintext));
  }

  [Test, Category("Exception")]
  public void GivenOcbCiphertextWithAFlippedBit_WhenDecrypted_ThenTheTagFails() {
    var ocb = Ocb.WithAes(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"));
    var c = Convert.FromHexString("45DD69F8F5AAE724");
    var tag = Convert.FromHexString("14054CD1F35D82760B2CD00D2F99BFA9");
    c[3] ^= 0x40;
    Assert.That(() => ocb.Decrypt(Convert.FromHexString("BBAA99887766554433221103"), [], c, tag, new byte[8]), Throws.InstanceOf<CryptographicException>());
  }

  [TestCase(0)]
  [TestCase(16)]
  [Category("Exception")]
  public void GivenNonceOutsideOneToFifteenBytes_WhenOcbEncrypts_ThenRefused(int length)
    => Assert.That(() => Ocb.WithAes(new byte[16]).Encrypt(new byte[length], [], [], [], new byte[16]), Throws.ArgumentException);

  // ── S2K and the AEAD packet (draft-ietf-openpgp-rfc4880bis-10 appendices A.3 and A.4) ───

  [TestCase("cd5a9f70fbe0bc65", "b25569b954324566 4527c4976e7a5d6e")]
  [TestCase("9f0b7da3e5ea6477", "eb9da78a9d5df80e c7020596399b6508")]
  [Category("HappyPath")]
  public void GivenRfc4880bisSample_WhenTheIteratedSaltedS2kRuns_ThenTheKeyMatches(string salt, string key)
    => Assert.That(Convert.ToHexStringLower(OpenPgpSymmetric.StringToKey(3, 8, Convert.FromHexString(salt), 144, "password"u8, 16)),
      Is.EqualTo(key.Replace(" ", "")));

  [TestCase("86f1efb86952329f24acd3bfd0e5346d",
    "01 07 01 0e b7 32 37 9f 73 c4 92 8d e2 5f ac fe 65 17 ec 10 5d c1 1a 81 dc 0c b8 a2 f6 f3 d9 00 16 38 4a 56 fc 82 1a e1 1a e8 db cb 49 86 26 55 de a8 8d 06 a8 14 86 80 1b 0f f3 87 bd 2e ab 01 3d e1 25 95 86 90 6e ab 24 76",
    TestName = "GivenRfc4880bisEaxSample_WhenTheAeadPacketIsDecrypted_ThenTheLiteralComesBack")]
  [TestCase("d1f01ba30e130aa7d2582c16e050ae44",
    "01 07 02 0e 5e d2 bc 1e 47 0a be 8f 1d 64 4c 7a 6c 8a 56 7b 0f 77 01 19 66 11 a1 54 ba 9c 25 74 cd 05 62 84 a8 ef 68 03 5c 62 3d 93 cc 70 8a 43 21 1b b6 ea f2 b2 7f 7c 18 d5 71 bc d8 3b 20 ad d3 a0 8b 73 af 15 b9 a0 98",
    TestName = "GivenRfc4880bisOcbSample_WhenTheAeadPacketIsDecrypted_ThenTheLiteralComesBack")]
  public void AeadSample(string cek, string body) {
    var clear = OpenPgpMessage.DecryptAead(Convert.FromHexString(body.Replace(" ", "")), Convert.FromHexString(cek));
    Assert.That(Convert.ToHexStringLower(clear), Is.EqualTo("cb1462000000000048656c6c6f2c20776f726c64210a"));
    Assert.That(OpenPgpMessage.LiteralData(clear, 0), Is.EqualTo("Hello, world!\n"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void GivenAeadSampleWithItsFinalTagCut_WhenDecrypted_ThenRefused() {
    var body = Convert.FromHexString("0107020e5ed2bc1e470abe8f1d644c7a6c8a567b0f7701196611a154ba9c2574cd056284a8ef68035c623d93cc708a43211bb6eaf2b27f7c18");
    Assert.That(() => OpenPgpMessage.DecryptAead(body, Convert.FromHexString("d1f01ba30e130aa7d2582c16e050ae44")), Throws.Exception);
  }

  // ── keys and messages GnuPG made ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenGnuPgSecretKeyExport_WhenLoaded_ThenFingerprintsAndKeyIdsMatchGpg() {
    var keys = OpenPgpKey.Load(Vector("ecc-secret.asc"));
    Assert.That(keys.Select(k => Convert.ToHexString(k.Fingerprint)),
      Is.EqualTo(new[] { "A076F346C8844D63F4065625D6F494848082CEB6", "C65FD747328645B2CC7A49DA902E4E2B038E11A3" }));
    Assert.That(keys[1].KeyId, Is.EqualTo(0x902E4E2B038E11A3UL));
    Assert.That(keys.Select(k => (k.Algorithm, k.CanEncrypt, k.HasSecret, k.IsProtected)),
      Is.EqualTo(new[] { (22, false, true, true), (18, true, true, true) }));
    Assert.That(OpenPgpKey.Load(Vector("ecc-public.asc")).Select(k => k.KeyId), Is.EqualTo(keys.Select(k => k.KeyId)));
  }

  [TestCase("ecc-secret.asc", "ecc-seipd.pgp", TestName = "GivenGpgMessageToCv25519_WithSeipd_WhenDecrypted_ThenThePlaintextComesBack")]
  [TestCase("rsa-secret.asc", "rsa-ocb.pgp", TestName = "GivenGpgMessageToRsa_WithOcb_WhenDecrypted_ThenThePlaintextComesBack")]
  [TestCase("rsa-secret.asc", "rsa-seipd-bz2.pgp", TestName = "GivenGpgMessageToRsa_WithBzip2_WhenDecrypted_ThenThePlaintextComesBack")]
  public void GpgMessage(string key, string message)
    => Assert.That(OpenPgpMessage.Decrypt(Vector(message), OpenPgpKey.Load(Vector(key)), Passphrase), Is.EqualTo(Plain));

  [Test, Category("Exception")]
  public void GivenGpgMessage_WhenTheKeysPassphraseIsWrong_ThenRefused()
    => Assert.That(() => OpenPgpMessage.Decrypt(Vector("rsa-ocb.pgp"), OpenPgpKey.Load(Vector("rsa-secret.asc")), "not it"),
      Throws.InstanceOf<CryptographicException>().With.Message.Contains("passphrase"));

  [Test, Category("Exception")]
  public void GivenGpgMessage_WhenOnlyAnotherKeyIsGiven_ThenRefused()
    => Assert.That(() => OpenPgpMessage.Decrypt(Vector("rsa-ocb.pgp"), OpenPgpKey.Load(Vector("ecc-secret.asc")), Passphrase),
      Throws.InstanceOf<CryptographicException>());

  [Test, Category("Exception")]
  public void GivenGpgMessage_WhenOnlyThePublicKeyIsGiven_ThenRefused()
    => Assert.That(() => OpenPgpMessage.Decrypt(Vector("ecc-seipd.pgp"), OpenPgpKey.Load(Vector("ecc-public.asc")), Passphrase),
      Throws.InstanceOf<CryptographicException>());

  [Test, Category("Exception")]
  public void GivenSeipdMessageWithAFlippedByte_WhenDecrypted_ThenTheIntegrityCheckFails() {
    var message = Vector("ecc-seipd.pgp");
    message[^5] ^= 0x01;
    Assert.That(() => OpenPgpMessage.Decrypt(message, OpenPgpKey.Load(Vector("ecc-secret.asc")), Passphrase), Throws.InstanceOf<CryptographicException>());
  }

  [TestCase("ecc-secret.asc", 9)]
  [TestCase("rsa-secret.asc", 7)]
  [TestCase("rsa-secret.asc", 10)]
  [Category("HappyPath")]
  public void GivenRecipientKey_WhenEncryptedAndDecrypted_ThenTheDataComesBack(string key, int algorithm) {
    var keys = OpenPgpKey.Load(Vector(key));
    var data = Encoding.UTF8.GetBytes(new string('x', 9000));
    var message = OpenPgpMessage.Encrypt(data, [keys.Last(k => k.CanEncrypt)], algorithm);
    Assert.That(OpenPgpMessage.Decrypt(message, OpenPgpKey.Load(Vector(key)), Passphrase), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenTwoRecipients_WhenEncrypted_ThenEitherKeyDecrypts() {
    var ecc = OpenPgpKey.Load(Vector("ecc-public.asc")).Last(k => k.CanEncrypt);
    var rsa = OpenPgpKey.Load(Vector("rsa-secret.asc")).Last(k => k.CanEncrypt);
    var message = OpenPgpMessage.Encrypt(Plain, [ecc, rsa]);
    Assert.That(OpenPgpMessage.Decrypt(message, OpenPgpKey.Load(Vector("ecc-secret.asc")), Passphrase), Is.EqualTo(Plain));
    Assert.That(OpenPgpMessage.Decrypt(message, OpenPgpKey.Load(Vector("rsa-secret.asc")), Passphrase), Is.EqualTo(Plain));
  }

  [Test, Category("Exception")]
  public void GivenSigningOnlyKey_WhenUsedAsRecipient_ThenRefused()
    => Assert.That(() => OpenPgpMessage.Encrypt(Plain, [OpenPgpKey.Load(Vector("ecc-public.asc"))[0]]), Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenArmorWithAWrongChecksum_WhenLoaded_ThenRefused() {
    var text = Encoding.ASCII.GetString(Vector("ecc-public.asc"));
    var at = text.LastIndexOf("\n=", StringComparison.Ordinal) + 2;
    var broken = text[..at] + (text[at] == 'A' ? 'B' : 'A') + text[(at + 1)..];
    Assert.That(() => OpenPgpKey.Load(Encoding.ASCII.GetBytes(broken)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenTruncatedMessage_WhenDecrypted_ThenRefused()
    => Assert.That(() => OpenPgpMessage.Decrypt(Vector("rsa-ocb.pgp")[..100], OpenPgpKey.Load(Vector("rsa-secret.asc")), Passphrase),
      Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<CryptographicException>());
}
