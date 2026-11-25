#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar as the oracle for encryption, both ways: archives we encrypt with every cipher, key
/// derivation and slicing must pass <c>dar -t -K</c> and come back byte for byte from
/// <c>dar -x -K</c> (and fail under a wrong password); archives <c>dar -c -K</c> encrypts must come
/// back byte for byte from our reader. Needs a dar built with libgcrypt (Ubuntu's 2.7.13 package
/// is; <c>CWB_DAR</c> picks another binary).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarEncryptionOracleTests {
  private const string Password = "s3cret pass";
  private static readonly string DarBinary = Environment.GetEnvironmentVariable("CWB_DAR") is { Length: > 0 } custom ? custom : "dar";
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(DarBinary))
      Assert.Ignore($"'{DarBinary}' is not installed (sudo apt-get install -y dar).");
    if (!FsInteropToolbox.RunWsl($"{DarBinary} --version").StdOut.Contains("Strong encryption (libgcrypt): YES"))
      Assert.Ignore($"'{DarBinary}' is built without libgcrypt.");
    this._tmp = DarFixtures.TempDirectory("encoracle");
  }

  [TearDown]
  public void TearDown() {
    try { if (this._tmp != null) Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static (string StdOut, string StdErr, int ExitCode) Dar(string arguments) {
    var r = FsInteropToolbox.RunWsl($"{DarBinary} -Q {arguments}");
    TestContext.Out.WriteLine($"{DarBinary} -Q {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  private static string DarName(char cipher) => DarCrypto.CipherName(cipher);

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

  /// <summary>dar tests, refuses under a wrong key, and restores our archive at <paramref name="basePath"/>.</summary>
  private void AssertDarDecrypts(string basePath, string src, char cipher, bool sequential) {
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var key = $"-K '{DarName(cipher)}:{Password}'";
    var test = Dar($"-t {darBase} {key}");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t refused our {DarName(cipher)} archive:\n{test.StdOut}\n{test.StdErr}");
    if (sequential) {
      var seq = Dar($"-t {darBase} {key} --sequential-read");
      Assert.That(seq.ExitCode, Is.Zero, $"dar -t --sequential-read refused our {DarName(cipher)} archive:\n{seq.StdOut}\n{seq.StdErr}");
    }
    var wrong = Dar($"-t {darBase} -K '{DarName(cipher)}:not it'");
    Assert.That(wrong.ExitCode, Is.Not.Zero, "dar must not accept a wrong key");
    var outDir = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(outDir);
    var extract = Dar($"-O -x {darBase} {key} -R {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed on our {DarName(cipher)} archive:\n{extract.StdOut}\n{extract.StdErr}");
    AssertSameTree(src, outDir);
  }

  // ── ours -> dar ─────────────────────────────────────────────────────────────

  [TestCase('a')]
  [TestCase('b')]
  [TestCase('t')]
  [TestCase('p')]
  [TestCase('c')]
  [TestCase('s')]
  public void GivenOurArchiveEncryptedWithEachCipher_WhenDarTestsAndRestoresIt_ThenItIsAcceptedAndByteIdentical(char cipher) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "ours");
    using (var fs = File.Create(basePath + ".1.dar"))
      DarWriter.Write(fs, DarEncryptionTests.Items(), new DarWriter.Settings('z', TapeMarks: true) { Encryption = new DarWriter.Encryption(cipher, Password) });
    this.AssertDarDecrypts(basePath, src, cipher, sequential: true);
  }

  [TestCase("sha512", "1000")]
  [TestCase("sha1", "")]
  [TestCase("md5", "3000")]
  public void GivenOurArchiveUnderAPbkdf2KeyDerivation_WhenDarRestoresIt_ThenItIsByteIdentical(string hash, string iterations) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "kdf");
    var inputs = Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(src, p).Replace('\\', '/'), Directory.Exists(p))).ToList();
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
        MethodName = "xz", Password = Password, EncryptionMethod = "serpent256",
        FormatSpecific = { [DarFormatDescriptor.KdfHashOption] = hash, [DarFormatDescriptor.KdfIterationsOption] = iterations },
      });
    this.AssertDarDecrypts(basePath, src, 'p', sequential: false);
  }

  [TestCase('a', "9k", "")]
  [TestCase('b', "5k", "12k")]
  [TestCase('s', "7k", "")]
  public void GivenOurEncryptedSliceSet_WhenDarReadsTheSet_ThenItIsAcceptedAndByteIdentical(char cipher, string slice, string first) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "sliced");
    var inputs = Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(src, p).Replace('\\', '/'), Directory.Exists(p))).ToList();
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
        MethodName = "stored", Password = Password, EncryptionMethod = DarName(cipher),
        FormatSpecific = { [DarFormatDescriptor.SliceSizeOption] = slice, [DarFormatDescriptor.FirstSliceSizeOption] = first, [DarFormatDescriptor.TapeMarksOption] = "true" },
      });
    Assert.That(File.Exists(basePath + ".4.dar"), "the set should span several slices");
    this.AssertDarDecrypts(basePath, src, cipher, sequential: false);
  }

  // ── dar -> ours ─────────────────────────────────────────────────────────────

  [TestCase("aes256", "")]
  [TestCase("blowfish", "-at")]
  [TestCase("twofish256", "-zgzip")]
  [TestCase("serpent256", "-zxz -at")]
  [TestCase("camellia256", "")]
  [TestCase("scrambling", "-zbzip2")]
  [TestCase("aes256", "--kdf-param 2000:sha512")]
  [TestCase("blowfish", "--kdf-param 2000:sha1")]
  [TestCase("camellia256", "--kdf-param 2000:md5 -zzstd:6:16k")]
  [TestCase("aes256", "-s 16k -S 30k -zlz4")]
  [TestCase("scrambling", "-s 9k")]
  public void GivenDarEncryptedArchive_WhenWeExtractItWithThePassword_ThenItIsByteIdentical(string cipher, string options) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "theirs");
    var create = Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} -K '{cipher}:{Password}' {options} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    if (options.Contains("-s "))
      Assert.That(slices, Has.Length.GreaterThan(1), "the archive should span several slices");

    using (var fs = File.OpenRead(slices[^1])) {
      Assert.That(() => new DarFormatDescriptor().List(fs, "wrong password"), Throws.InstanceOf<ArchivePasswordException>());
      fs.Position = 0;
      Assert.That(() => new DarFormatDescriptor().List(fs, null), Throws.InstanceOf<ArchivePasswordException>());
    }
    var outDir = Path.Combine(this._tmp, "out");
    using (var fs = File.OpenRead(slices[^1]))
      new DarFormatDescriptor().Extract(fs, outDir, Password, null);
    AssertSameTree(src, outDir);
  }

  // ── a crypto block size other than dar's default (-#, not recorded in the archive) ─────────

  [TestCase("aes256", 1000, "")]
  [TestCase("aes256", 65536, "-zgzip")]
  [TestCase("blowfish", 4000, "-at")]
  [TestCase("twofish256", 333, "-s 20k")]
  [TestCase("camellia256", 16, "")]
  public void GivenDarArchiveWithAnotherCryptoBlockSize_WhenWeReadAndEditIt_ThenTheSizeIsInferredAndDarRestoresTheEdit(string cipher, int blockSize, string options) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "theirs");
    var key = $"-K '{cipher}:{Password}' -# {blockSize}";
    var create = Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} {key} {options} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var first = basePath + ".1.dar";
    var outDir = Path.Combine(this._tmp, "ours-out");
    using (var fs = File.OpenRead(first))
      new DarFormatDescriptor().Extract(fs, outDir, Password, null);
    AssertSameTree(src, outDir);

    var added = "added under -#\n"u8.ToArray();
    using (var fs = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("added.txt", added)], new ArchiveMutationOptions { Password = Password });
    File.WriteAllBytes(Path.Combine(src, "added.txt"), added);
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var test = Dar($"-t {darBase} {key}");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t -# {blockSize} refused the edited archive:\n{test.StdOut}\n{test.StdErr}");
    var restored = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(restored);
    var extract = Dar($"-O -x {darBase} {key} -R {FsInteropToolbox.WinToWsl(restored)}");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed on the edited archive:\n{extract.StdOut}\n{extract.StdErr}");
    AssertSameTree(src, restored);
  }

  [TestCase('a', 4096)]
  [TestCase('b', 777)]
  public void GivenOurArchiveWithAnotherCryptoBlockSize_WhenDarReadsItWithThatSize_ThenItIsByteIdentical(char cipher, int blockSize) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "ours");
    using (var fs = File.Create(basePath + ".1.dar"))
      DarWriter.Write(fs, DarEncryptionTests.Items(), new DarWriter.Settings('z', TapeMarks: true) { Encryption = new DarWriter.Encryption(cipher, Password, ClearBlockSize: blockSize) });
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var key = $"-K '{DarName(cipher)}:{Password}' -# {blockSize}";
    var test = Dar($"-t {darBase} {key}");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t -# {blockSize} refused our archive:\n{test.StdOut}\n{test.StdErr}");
    var outDir = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(outDir);
    var extract = Dar($"-O -x {darBase} {key} -R {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed on our archive:\n{extract.StdOut}\n{extract.StdErr}");
    AssertSameTree(src, outDir);
  }

  [Test]
  public void GivenDarArchiveEncryptedForAGnuPgRecipient_WhenListed_ThenItFallsBackAndNamesTheReason() {
    var home = $"/tmp/cwb-dar-gpg-{Guid.NewGuid():N}";
    try {
      var keygen = FsInteropToolbox.RunWsl($"mkdir -m 700 {home} && GNUPGHOME={home} gpg --batch --pinentry-mode loopback --passphrase '' --quick-gen-key 'cwb test <cwb@example.invalid>' default default never");
      if (keygen.ExitCode != 0)
        Assert.Ignore($"gpg could not make a key: {keygen.StdErr}");
      var src = this.Source();
      var basePath = Path.Combine(this._tmp, "gpg");
      var create = FsInteropToolbox.RunWsl($"GNUPGHOME={home} {DarBinary} -Q -c {FsInteropToolbox.WinToWsl(basePath)} -K 'gnupg::cwb@example.invalid' -R {FsInteropToolbox.WinToWsl(src)}");
      Assert.That(create.ExitCode, Is.Zero, $"dar -c -K gnupg failed:\n{create.StdOut}\n{create.StdErr}");
      using var fs = File.OpenRead(basePath + ".1.dar");
      var entries = new DarFormatDescriptor().List(fs, Password);
      Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
      var outDir = Path.Combine(this._tmp, "meta");
      fs.Position = 0;
      new DarFormatDescriptor().Extract(fs, outDir, Password, ["metadata.ini"]);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "metadata.ini")), Does.Contain("GnuPG"));
    } finally {
      FsInteropToolbox.RunWsl(FsInteropToolbox.RemoveScratch(home, null));
    }
  }
}
