using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// StuffIt X encryption against the password-protected archives of ssokolow/stuffit-test-files (CC0;
/// see ReferenceVectors/README.md), all written with the password "password": every fork must match
/// the SHA-256 of the file it was made from and every stream its stored CRC-32, so the key
/// derivation, the password check and each cipher are proven by StuffIt's own output.
/// </summary>
[TestFixture]
public class StuffItXCryptoTests {
  private const string Password = "password";
  private const string Jpg = "e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1";
  private const string Png = "fdda20984cc1591419ec4583e24e72e4dba39d0b96608253f853a2dfb238ad1a";
  private const string WinTxt = "b2f51cd17b3cbe77f091f887d91110164a2cb5a5a9ebe828c44d655c83dca8eb";

  private static readonly Dictionary<string, string> WinSources = new() {
    ["sources/testfile.jpg"] = Jpg,
    ["sources/testfile.png"] = Png,
    ["sources/testfile.txt"] = WinTxt,
  };

  private static readonly Dictionary<string, string> MacSources = new() {
    ["Test Image"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    ["Test Image.rsrc"] = "4b8175653903645616d9e07627957ae0dba4c7ac3b3e9aa6afc8e07144dcfbb0",
    ["Test Text"] = "9734aef6d3788ba985e78f7b3785dc4817e770be92a4e5e57e64a92cc9c2fc25",
    ["Test Text.rsrc"] = "5f0c7e77ac2430be40532730665ea27f0cf1088ac049e0c06851d62085b87315",
    ["testfile.jpg"] = Jpg,
    ["testfile.PICT"] = "318d71cd4d027c6bec6917af3ddc3b7df0ec8b07031045a9cdd9052b94c7782e",
    ["testfile.PICT.rsrc"] = "011604ad448ef4451081d04bd395c2a974cab637877fb64b45e62ebe39bc452e",
    ["testfile.png"] = Png,
    ["testfile.txt"] = "b645efee0ed710034959eae942277a750d08687c30bcf0e9ec6ea7641527462f",
    ["testfile.txt.rsrc"] = "f788dcd5313a531a27fc62a9b4c951a6653ef11b49f2262ee0796f72c5564b0a",
  };

  private static byte[] Vector(string name) {
    using var s = typeof(StuffItXCryptoTests).Assembly.GetManifestResourceStream("StuffItXVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  private static IEnumerable<TestCaseData> Encrypted() {
    foreach (var year in new[] { 2009, 2010 })
      foreach (var (cipher, method) in new[] { ("aes", "AES-256"), ("blowfish", "Blowfish"), ("des", "DES"), ("rc4", "RC4") })
        yield return new TestCaseData($"testfile.stuffit_deluxe_{year}.win.password.{cipher}.sitx", method, WinSources);
    yield return new TestCaseData("testfile.stuffit7_dlx.mac9.password.sitx", "AES-256", MacSources);
    yield return new TestCaseData("testfile.stuffit7_dlx.macx1.password.sitx", "AES-256", MacSources);
  }

  private static IEnumerable<string> EncryptedNames() => Encrypted().Select(t => (string)t.Arguments[0]!);

  [TestCaseSource(nameof(Encrypted)), Category("ThemVsUs")]
  public void GivenEncryptedArchive_WhenExtractedWithThePassword_ThenEveryForkMatchesItsSourceFile(string vector, string cipher, Dictionary<string, string> expected) {
    using var r = new StuffItXReader(new MemoryStream(Vector(vector)), password: Password);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FullPath), Is.EquivalentTo(expected.Keys));
    foreach (var e in files.Where(e => e.OriginalSize > 0)) {
      Assert.That(e.IsEncrypted, Is.True, e.FullPath);
      Assert.That(e.Method, Does.EndWith("+" + cipher), e.FullPath);
      if (e.Method.StartsWith("Method 7", StringComparison.Ordinal)) {
        // the JPEG recompression is decrypted, then refused: it has no decoder here
        Assert.That(() => r.Extract(e), Throws.InstanceOf<NotSupportedException>(), e.FullPath);
        continue;
      }
      Assert.That(Sha(r.Extract(e)), Is.EqualTo(expected[e.FullPath]), e.FullPath);
    }
  }

  [TestCaseSource(nameof(EncryptedNames)), Category("ThemVsUs")]
  public void GivenEncryptedArchive_WhenEveryStreamIsDecoded_ThenEachPassesItsStoredCrc(string vector) {
    using var r = new StuffItXReader(new MemoryStream(Vector(vector)), password: Password);
    var checkedStreams = 0;
    foreach (var id in r.StreamIds) {
      if (r.StreamMethod(id).StartsWith("Method 7", StringComparison.Ordinal)) continue;
      r.DecodeStream(id);
      ++checkedStreams;
    }
    Assert.That(checkedStreams, Is.GreaterThan(0));
  }

  [TestCaseSource(nameof(EncryptedNames)), Category("Exception")]
  public void GivenEncryptedArchive_WhenExtractedWithAWrongPassword_ThenRefusedAsAWrongPassword(string vector) {
    var ex = Assert.Throws<ArchivePasswordException>(() => {
      using var r = new StuffItXReader(new MemoryStream(Vector(vector)), password: "Password");
      foreach (var e in r.Entries.Where(e => !e.IsDirectory && e.OriginalSize > 0)) r.Extract(e);
    });
    Assert.That(ex!.PasswordWasSupplied, Is.True);
  }

  [TestCaseSource(nameof(EncryptedNames)), Category("Exception")]
  public void GivenEncryptedArchive_WhenExtractedWithoutAPassword_ThenAPasswordIsAskedFor(string vector) {
    var ex = Assert.Throws<ArchivePasswordException>(() => {
      using var r = new StuffItXReader(new MemoryStream(Vector(vector)));
      foreach (var e in r.Entries.Where(e => !e.IsDirectory && e.OriginalSize > 0)) r.Extract(e);
    });
    Assert.That(ex!.PasswordWasSupplied, Is.False);
  }

  [Test, Category("HappyPath")]
  public void GivenArchiveWithAPlainCatalog_WhenListedWithoutAPassword_ThenEntriesShowAsEncrypted() {
    var list = new StuffItXFormatDescriptor().List(new MemoryStream(Vector("testfile.stuffit_deluxe_2010.win.password.aes.sitx")), null);
    var txt = list.Single(e => e.Name == "sources/testfile.txt");
    Assert.That(txt.IsEncrypted, Is.True);
    Assert.That(txt.OriginalSize, Is.EqualTo(12));
    Assert.That(list.Single(e => e.Name == "sources").IsEncrypted, Is.False);
  }

  [Test, Category("Exception")]
  public void GivenArchiveWithAnEncryptedCatalog_WhenListedWithoutAPassword_ThenAPasswordIsAskedFor() {
    var ex = Assert.Throws<ArchivePasswordException>(() => new StuffItXFormatDescriptor().List(new MemoryStream(Vector("testfile.stuffit_deluxe_2009.win.password.des.sitx")), null));
    Assert.That(ex!.PasswordWasSupplied, Is.False);
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptor_WhenExtractingWithThePassword_ThenFilesLandUnderTheirPaths() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sitxc_{Guid.NewGuid():N}");
    try {
      new StuffItXFormatDescriptor().Extract(new MemoryStream(Vector("testfile.stuffit_deluxe_2010.win.password.blowfish.sitx")), dir, Password, ["sources/testfile.txt"]);
      Assert.That(Sha(File.ReadAllBytes(Path.Combine(dir, "sources", "testfile.txt"))), Is.EqualTo(WinTxt));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  // ── key lengths: each cipher's equivalence classes and boundaries ──────────────

  [TestCase(0L, 16L), TestCase(0L, 24L), TestCase(0L, 32L)]
  [TestCase(1L, 5L), TestCase(1L, 56L)]
  [TestCase(2L, 8L)]
  [TestCase(3L, 1L), TestCase(3L, 64L), TestCase(3L, 1024L)]
  [Category("Boundary")]
  public void GivenKeyLengthTheCipherTakes_WhenValidated_ThenAccepted(long cipher, long length)
    => Assert.That(StuffItXCrypto.Validate([(cipher, length)]), Is.EqualTo((int)length));

  [TestCase(0L, 15L), TestCase(0L, 20L), TestCase(0L, 33L)]
  [TestCase(1L, 4L), TestCase(1L, 57L)]
  [TestCase(2L, 7L), TestCase(2L, 16L)]
  [TestCase(3L, 0L), TestCase(3L, 1025L)]
  [Category("Boundary")]
  public void GivenKeyLengthTheCipherCannotTake_WhenValidated_ThenRefusedAsMalformed(long cipher, long length)
    => Assert.That(() => StuffItXCrypto.Validate([(cipher, length)]), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exception")]
  public void GivenUnknownCipher_WhenValidated_ThenRefusedAsNotSupported()
    => Assert.That(() => StuffItXCrypto.Validate([(4, 16)]), Throws.InstanceOf<NotSupportedException>());

  [Test, Category("Boundary")]
  public void GivenSeveralLayers_WhenValidated_ThenTheirKeyLengthsAdd()
    => Assert.That(StuffItXCrypto.Validate([(0, 32), (1, 16), (3, 64)]), Is.EqualTo(112));

  [Test, Category("HappyPath")]
  public void GivenDerivation_WhenAskedForAPrefix_ThenItIsThePrefixOfTheLongerDerivationOfTheSameRoundedLength() {
    // the rounded length fixes the rounds' inputs, so 17..32 bytes all come from the same 32-byte string
    var p = "password"u8.ToArray();
    var full = StuffItXCrypto.Derive(p, 32);
    Assert.That(StuffItXCrypto.Derive(p, 17), Is.EqualTo(full[..17]));
    Assert.That(StuffItXCrypto.Derive(p, 16), Is.Not.EqualTo(full[..16]));
  }

  [Test, Category("Exception")]
  public void GivenTruncatedEncryptedStream_WhenDecrypted_ThenRefused() {
    var crypto = new StuffItXCrypto(Password, [(0, 32)]);
    Assert.That(() => crypto.Decrypt(new byte[17]), Throws.InstanceOf<InvalidDataException>());
  }
}
