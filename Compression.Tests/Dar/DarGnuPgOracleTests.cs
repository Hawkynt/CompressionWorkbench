#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Crypto;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar and GnuPG as the oracle for archives encrypted for GnuPG recipients (<c>dar -K
/// gnupg:cipher:recipient</c>), both ways: we read, extract and edit what dar wrote to the test
/// keys of <c>Crypto/OpenPgpVectors</c> (cv25519 and RSA), and dar, with those keys in a scratch
/// <c>GNUPGHOME</c>, tests and restores what we wrote and edited.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarGnuPgOracleTests {
  private const string Passphrase = "keypass";
  private const string EccRecipient = "cwb-ecc@test.invalid";
  private const string RsaRecipient = "cwb-rsa@test.invalid";
  private const string P256Signer = "cwb-p256@test.invalid";
  private static readonly string DarBinary = Environment.GetEnvironmentVariable("CWB_DAR") is { Length: > 0 } custom ? custom : "dar";
  private string _tmp = null!;
  private string? _home;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(DarBinary) || !FsInteropToolbox.WslHasTool("gpg"))
      Assert.Ignore("dar and gpg are needed (sudo apt-get install -y dar gnupg).");
    this._tmp = DarFixtures.TempDirectory("gnupg");
    foreach (var name in new[] { "ecc-secret.asc", "rsa-secret.asc", "ecc-public.asc", "rsa-public.asc", "p256-secret.asc", "p256-public.asc" })
      File.WriteAllBytes(Path.Combine(this._tmp, name), OpenPgpTests.Vector(name));
    var made = FsInteropToolbox.RunWsl("mktemp -d /tmp/cwb-dar-gnupg-XXXXXX");
    Assert.That(made.ExitCode, Is.Zero, made.StdErr);
    this._home = made.StdOut.Trim();
    // the test keys, trusted, with their passphrase cached by one loopback decryption so that
    // dar's own (non-interactive) use of gpg finds it
    var probe = Path.Combine(this._tmp, "probe.txt");
    File.WriteAllText(probe, "probe");
    var setup = this.Shell(
      $"gpg --batch --import {Wsl("ecc-secret.asc")} {Wsl("rsa-secret.asc")} {Wsl("p256-secret.asc")} && "
      + "printf 'A076F346C8844D63F4065625D6F494848082CEB6:6:\\n02AAE52A50B9261967A9DDD09CB234AB9157E743:6:\\nC64374F0BD9DDFA05DF9DA58C583D4E6AD2EF20B:6:\\n' | gpg --batch --import-ownertrust && "
      + $"for r in {EccRecipient} {RsaRecipient}; do gpg --batch --yes -r $r -o - -e {FsInteropToolbox.WinToWsl(probe)} | gpg --batch --pinentry-mode loopback --passphrase {Passphrase} -d > /dev/null || exit 1; done && "
      + $"for u in {EccRecipient} {RsaRecipient} {P256Signer}; do gpg --batch --yes --pinentry-mode loopback --passphrase {Passphrase} -u $u -o /dev/null -s {FsInteropToolbox.WinToWsl(probe)} || exit 1; done");
    Assert.That(setup.ExitCode, Is.Zero, $"gpg setup failed:\n{setup.StdErr}");
  }

  [TearDown]
  public void TearDown() {
    if (this._home is { Length: > 0 } home) {
      FsInteropToolbox.RunWsl($"GNUPGHOME='{home}' gpgconf --kill gpg-agent");
      FsInteropToolbox.RunWsl(FsInteropToolbox.RemoveScratch($"'{home}'"));
    }
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private string Wsl(string name) => FsInteropToolbox.WinToWsl(Path.Combine(this._tmp, name));

  private (string StdOut, string StdErr, int ExitCode) Shell(string command) {
    var r = FsInteropToolbox.RunWsl($"export GNUPGHOME='{this._home}'; {command}");
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  private string SecretKey(string file) => Path.Combine(this._tmp, file) + "|" + Passphrase;

  private string Source() {
    var src = Path.Combine(this._tmp, "src");
    foreach (var item in DarEncryptionTests.Items()) {
      var full = Path.Combine(src, item.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      using var input = item.Open!();
      using var output = File.Create(full);
      input.CopyTo(output);
    }
    return src;
  }

  private static void AssertSameTree(string expectedRoot, string actualRoot) {
    var expected = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expectedRoot, f)).Order(StringComparer.Ordinal).ToArray();
    var actual = Directory.EnumerateFiles(actualRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(actualRoot, f)).Order(StringComparer.Ordinal).ToArray();
    Assert.That(actual, Is.EqualTo(expected));
    foreach (var rel in expected)
      Assert.That(File.ReadAllBytes(Path.Combine(actualRoot, rel)), Is.EqualTo(File.ReadAllBytes(Path.Combine(expectedRoot, rel))), rel);
  }

  /// <summary>dar tests the archive and restores a tree equal to <paramref name="src"/>.</summary>
  private void AssertDarRestores(string basePath, string src, bool sequential) {
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var test = this.Shell($"{DarBinary} -Q -t {darBase} -K gnupg:");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t refused the archive:\n{test.StdOut}\n{test.StdErr}");
    if (sequential) {
      var seq = this.Shell($"{DarBinary} -Q -t {darBase} -K gnupg: --sequential-read");
      Assert.That(seq.ExitCode, Is.Zero, $"dar -t --sequential-read refused the archive:\n{seq.StdOut}\n{seq.StdErr}");
    }
    var outDir = Path.Combine(this._tmp, $"out-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    var extract = this.Shell($"{DarBinary} -Q -O -x {darBase} -K gnupg: -R {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed:\n{extract.StdOut}\n{extract.StdErr}");
    AssertSameTree(src, outDir);
  }

  [TestCase("aes256", EccRecipient, "ecc-secret.asc", "")]
  [TestCase("blowfish", EccRecipient, "ecc-secret.asc", "-at -zgzip")]
  [TestCase("twofish256", RsaRecipient, "rsa-secret.asc", "")]
  [TestCase("camellia256", RsaRecipient, "rsa-secret.asc", "-s 20k -zbzip2")]
  public void GivenDarArchiveForAGnuPgRecipient_WhenWeExtractAndEditIt_ThenItIsByteIdenticalAndDarRestoresTheEdit(string cipher, string recipient, string secretKey, string options) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "theirs");
    var create = this.Shell($"{DarBinary} -Q -c {FsInteropToolbox.WinToWsl(basePath)} -K gnupg:{cipher}:{recipient} {options} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c -K gnupg failed:\n{create.StdOut}\n{create.StdErr}");
    var first = basePath + ".1.dar";

    using (var fs = File.OpenRead(first)) {
      Assert.That(() => new DarFormatDescriptor().List(fs, null),
        Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).False);
      fs.Position = 0;
      Assert.That(() => new DarFormatDescriptor().List(fs, Path.Combine(this._tmp, secretKey) + "|wrong"), Throws.InstanceOf<ArchivePasswordException>());
      fs.Position = 0;
      var other = secretKey == "ecc-secret.asc" ? "rsa-secret.asc" : "ecc-secret.asc";
      Assert.That(() => new DarFormatDescriptor().List(fs, this.SecretKey(other)), Throws.InstanceOf<ArchivePasswordException>());
    }
    var outDir = Path.Combine(this._tmp, "ours-out");
    using (var fs = File.OpenRead(first))
      new DarFormatDescriptor().Extract(fs, outDir, this.SecretKey(secretKey), null);
    AssertSameTree(src, outDir);

    var added = "added to a GnuPG archive\n"u8.ToArray();
    using (var fs = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("added.txt", added)], new ArchiveMutationOptions { Password = this.SecretKey(secretKey) });
    File.WriteAllBytes(Path.Combine(src, "added.txt"), added);
    this.AssertDarRestores(basePath, src, sequential: !options.Contains("-at") && !options.Contains("-s "));
  }

  [TestCase("aes256", "ecc-public.asc", true)]
  [TestCase("blowfish", "rsa-secret.asc", false)]
  [TestCase("serpent256", "ecc-public.asc;rsa-secret.asc", true)]
  public void GivenOurArchiveForGnuPgRecipients_WhenDarReadsIt_ThenItIsByteIdentical(string cipher, string recipients, bool tapeMarks) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "ours");
    var inputs = Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(src, p).Replace('\\', '/'), Directory.Exists(p))).ToList();
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
        MethodName = "gzip", EncryptionMethod = cipher,
        FormatSpecific = {
          [DarFormatDescriptor.GnuPgRecipientsOption] = string.Join(';', recipients.Split(';').Select(r => Path.Combine(this._tmp, r))),
          [DarFormatDescriptor.TapeMarksOption] = tapeMarks.ToString(),
        },
      });
    this.AssertDarRestores(basePath, src, sequential: tapeMarks);
    // and every recipient's secret key opens it here too
    foreach (var secret in recipients.Split(';').Select(r => r.Replace("public", "secret"))) {
      var outDir = Path.Combine(this._tmp, $"ours-{secret}");
      using var fs = File.OpenRead(basePath + ".1.dar");
      new DarFormatDescriptor().Extract(fs, outDir, this.SecretKey(secret), null);
      AssertSameTree(src, outDir);
    }
  }

  // ── signed archives (dar --sign, header flag 0x0200) ──────────────────────

  [TestCase(EccRecipient, "ecc-secret.asc", "")]
  [TestCase(RsaRecipient, "rsa-secret.asc", "-at")]
  [TestCase(P256Signer, "p256-secret.asc", "")]
  public void GivenDarSignedArchive_WhenWeReadAndEditIt_ThenTheSignatureVerifiesAndDarAcceptsTheEdit(string signer, string signerKey, string options) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "signed");
    var create = this.Shell($"{DarBinary} -Q -c {FsInteropToolbox.WinToWsl(basePath)} -K gnupg::{EccRecipient} --sign {signer} {options} -R {FsInteropToolbox.WinToWsl(src)} < /dev/null");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c --sign failed:\n{create.StdOut}\n{create.StdErr}");
    var first = basePath + ".1.dar";
    var password = Path.Combine(this._tmp, "ecc-secret.asc") + ";" + Path.Combine(this._tmp, signerKey) + "|" + Passphrase;
    using (var fs = File.OpenRead(first)) {
      using var set = DarSliceSet.Open(fs);
      var archive = DarArchive.Read(set.Archive, password);
      Assert.That((archive.Trailer.Flags & 0x0200) != 0, Is.True, "dar sets flag 0x0200");
      Assert.That(archive.Signers.Single(), Is.EqualTo(Compression.Core.Crypto.OpenPgp.OpenPgpKey.Load(OpenPgpTests.Vector(signerKey))[0].Fingerprint));
    }
    if (signerKey != "ecc-secret.asc")
      using (var fs = File.OpenRead(first))
        Assert.That(() => new DarFormatDescriptor().List(fs, this.SecretKey("ecc-secret.asc")), Throws.InstanceOf<ArchivePasswordException>(),
          "without the signer's key the signature cannot be checked");

    var added = "added to a signed archive\n"u8.ToArray();
    using (var fs = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("added.txt", added)], new ArchiveMutationOptions { Password = password });
    File.WriteAllBytes(Path.Combine(src, "added.txt"), added);
    this.AssertDarRestores(basePath, src, sequential: !options.Contains("-at"));
  }

  [TestCase("ecc-secret.asc", "A076F346C8844D63F4065625D6F494848082CEB6")]
  [TestCase("rsa-secret.asc", "02AAE52A50B9261967A9DDD09CB234AB9157E743")]
  [TestCase("p256-secret.asc", "C64374F0BD9DDFA05DF9DA58C583D4E6AD2EF20B")]
  public void GivenOurSignedArchive_WhenDarAndGpgReadIt_ThenDarRestoresItAndGpgReportsAGoodSignature(string signerFile, string fingerprint) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "oursigned");
    var inputs = Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(src, p).Replace('\\', '/'), Directory.Exists(p))).ToList();
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
        EncryptionMethod = "aes256",
        FormatSpecific = {
          [DarFormatDescriptor.GnuPgRecipientsOption] = Path.Combine(this._tmp, "ecc-public.asc"),
          [DarFormatDescriptor.GnuPgSignerOption] = this.SecretKey(signerFile),
          [DarFormatDescriptor.TapeMarksOption] = "true",
        },
      });
    this.AssertDarRestores(basePath, src, sequential: true);
    // gpg checks the signature inside the key message the header carries
    byte[] message;
    using (var fs = File.OpenRead(basePath + ".1.dar"))
    using (var set = DarSliceSet.Open(fs))
      message = DarArchive.Read(set.Archive, Path.Combine(this._tmp, "ecc-secret.asc") + ";" + Path.Combine(this._tmp, signerFile) + "|" + Passphrase).Trailer.CryptedKey!;
    File.WriteAllBytes(Path.Combine(this._tmp, "key.pgp"), message);
    var verify = this.Shell($"gpg --batch --status-fd 1 --pinentry-mode loopback --passphrase {Passphrase} -o /dev/null -d {Wsl("key.pgp")}");
    Assert.That(verify.ExitCode, Is.Zero, verify.StdErr);
    Assert.That(verify.StdOut, Does.Contain("[GNUPG:] GOODSIG").And.Contain("[GNUPG:] VALIDSIG " + fingerprint));
  }
}
