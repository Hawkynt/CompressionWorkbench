#pragma warning disable CS1591
using System.Text;
using Compression.Core.Crypto.OpenPgp;

namespace Compression.Tests.Crypto;

/// <summary>
/// GnuPG as the oracle for the OpenPGP message layer, both ways: gpg decrypts what we encrypt to
/// its keys (cv25519 and RSA), and we decrypt what gpg encrypts to them under its AEAD (OCB) and
/// <c>--rfc4880</c> (CFB + MDC) packets, each compression and several ciphers. The keys are the
/// test keys of <c>OpenPgpVectors</c>, imported into a scratch <c>GNUPGHOME</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class OpenPgpGnuPgOracleTests {
  private const string Passphrase = "keypass";
  private string _tmp = null!;
  private string? _home;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("gpg"))
      Assert.Ignore("gpg is not installed (sudo apt-get install -y gnupg).");
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_openpgp_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
    foreach (var name in new[] { "ecc-secret.asc", "rsa-secret.asc", "p256-secret.asc" })
      File.WriteAllBytes(Path.Combine(this._tmp, name), OpenPgpTests.Vector(name));
    var made = FsInteropToolbox.RunWsl("mktemp -d /tmp/cwb-gnupg-XXXXXX");
    Assert.That(made.ExitCode, Is.Zero, made.StdErr);
    this._home = made.StdOut.Trim();
    var import = this.Gpg($"--import {FsInteropToolbox.WinToWsl(Path.Combine(this._tmp, "ecc-secret.asc"))} {FsInteropToolbox.WinToWsl(Path.Combine(this._tmp, "rsa-secret.asc"))} {FsInteropToolbox.WinToWsl(Path.Combine(this._tmp, "p256-secret.asc"))}");
    Assert.That(import.ExitCode, Is.Zero, import.StdErr);
  }

  [TearDown]
  public void TearDown() {
    if (this._home is { Length: > 0 } home) {
      FsInteropToolbox.RunWsl($"GNUPGHOME='{home}' gpgconf --kill gpg-agent");
      FsInteropToolbox.RunWsl(FsInteropToolbox.RemoveScratch($"'{home}'"));
    }
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private (string StdOut, string StdErr, int ExitCode) Gpg(string arguments) {
    var r = FsInteropToolbox.RunWsl($"GNUPGHOME='{this._home}' gpg --batch --yes --pinentry-mode loopback --passphrase {Passphrase} --trust-model always {arguments}");
    TestContext.Out.WriteLine($"gpg {arguments} -> {r.ExitCode}\n{r.StdErr}");
    return r;
  }

  private static byte[] Data() => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i} of the plaintext\n")));

  [TestCase("ecc-secret.asc", 9)]
  [TestCase("ecc-secret.asc", 7)]
  [TestCase("rsa-secret.asc", 9)]
  [TestCase("rsa-secret.asc", 13)]
  public void GivenOurMessage_WhenGpgDecryptsIt_ThenThePlaintextComesBack(string key, int algorithm) {
    var recipient = OpenPgpKey.Load(OpenPgpTests.Vector(key)).Last(k => k.CanEncrypt);
    var data = Data();
    var message = Path.Combine(this._tmp, "ours.pgp");
    File.WriteAllBytes(message, OpenPgpMessage.Encrypt(data, [recipient], algorithm));
    var output = Path.Combine(this._tmp, "ours.out");
    var r = this.Gpg($"-o {FsInteropToolbox.WinToWsl(output)} -d {FsInteropToolbox.WinToWsl(message)}");
    Assert.That(r.ExitCode, Is.Zero, $"gpg refused our message:\n{r.StdErr}");
    Assert.That(File.ReadAllBytes(output), Is.EqualTo(data));
  }

  [TestCase("ecc-secret.asc", 22)]
  [TestCase("rsa-secret.asc", 1)]
  [TestCase("p256-secret.asc", 19)]
  public void GivenOurSignedMessage_WhenGpgDecryptsIt_ThenItReportsAGoodSignature(string signerFile, int algorithm) {
    var signer = OpenPgpKey.Load(OpenPgpTests.Vector(signerFile)).First(k => k.HasSecret && k.Algorithm == algorithm);
    signer.Unlock(Passphrase);
    var recipient = OpenPgpKey.Load(OpenPgpTests.Vector("rsa-secret.asc")).Last(k => k.CanEncrypt);
    var data = Data();
    var message = Path.Combine(this._tmp, "signed.pgp");
    File.WriteAllBytes(message, OpenPgpMessage.Encrypt(data, [recipient], signers: [signer]));
    var output = Path.Combine(this._tmp, "signed.out");
    var r = this.Gpg($"--status-fd 2 -o {FsInteropToolbox.WinToWsl(output)} -d {FsInteropToolbox.WinToWsl(message)}");
    Assert.That(r.ExitCode, Is.Zero, $"gpg refused our signed message:\n{r.StdErr}");
    Assert.That(r.StdErr, Does.Contain("[GNUPG:] GOODSIG").And.Contain("[GNUPG:] VALIDSIG " + Convert.ToHexString(signer.Fingerprint)));
    Assert.That(File.ReadAllBytes(output), Is.EqualTo(data));
  }

  [TestCase("cwb-ecc@test.invalid", "", "ecc-secret.asc")]
  [TestCase("cwb-ecc@test.invalid", "--rfc4880 --compress-algo zip", "ecc-secret.asc")]
  [TestCase("cwb-ecc@test.invalid", "--cipher-algo TWOFISH --compress-algo none", "ecc-secret.asc")]
  [TestCase("cwb-rsa@test.invalid", "--cipher-algo AES128 --compress-algo bzip2", "rsa-secret.asc")]
  [TestCase("cwb-rsa@test.invalid", "--rfc4880 --cipher-algo CAMELLIA256 -z 9", "rsa-secret.asc")]
  [TestCase("cwb-rsa@test.invalid", "--rfc4880 --cipher-algo CAST5", "rsa-secret.asc")]
  public void GivenGpgMessage_WhenWeDecryptIt_ThenThePlaintextComesBack(string recipient, string options, string key) {
    var data = Data();
    var input = Path.Combine(this._tmp, "plain.txt");
    File.WriteAllBytes(input, data);
    var message = Path.Combine(this._tmp, "theirs.pgp");
    var r = this.Gpg($"-r {recipient} {options} -o {FsInteropToolbox.WinToWsl(message)} -e {FsInteropToolbox.WinToWsl(input)}");
    Assert.That(r.ExitCode, Is.Zero, $"gpg -e failed:\n{r.StdErr}");
    Assert.That(OpenPgpMessage.Decrypt(File.ReadAllBytes(message), OpenPgpKey.Load(OpenPgpTests.Vector(key)), Passphrase), Is.EqualTo(data));
  }
}
