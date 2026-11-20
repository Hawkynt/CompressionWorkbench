using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// Encrypted installers compiled by ISCC (Encryption=yes, 5.3.8 ANSI with ARC4-MD5, 5.6.1 Unicode
/// with ARC4-SHA1, 6.4.3 with XChaCha20; Encryption=full, 6.5.4, headers encrypted too; see
/// ReferenceVectors/README.md): with the password every file matches innoextract 1.9 (or, from
/// 6.4, which no innoextract reads, the payload compiled in); without it, or with a wrong one,
/// nothing is decrypted.
/// </summary>
[TestFixture]
public class InnoSetupEncryptionTests {
  private const string AnsiPassword = "Inno-Secret_1";
  private const string UnicodePassword = "Pässwört-€1";

  public static IEnumerable<TestCaseData> Fixtures() {
    yield return new TestCaseData("inno538a-enc-zip.exe", AnsiPassword, InnoEncryption.Arc4Md5);
    yield return new TestCaseData("inno561u-enc-lzma2-solid.exe", UnicodePassword, InnoEncryption.Arc4Sha1);
    yield return new TestCaseData("inno643-enc-zip.exe", UnicodePassword, InnoEncryption.XChaCha20);
    yield return new TestCaseData("inno654-encfull-zip.exe", UnicodePassword, InnoEncryption.XChaCha20);
  }

  private static byte[] Load(string name) {
    using var resource = typeof(InnoSetupEncryptionTests).Assembly.GetManifestResourceStream("InnoVectors." + name + ".gz")
      ?? throw new InvalidOperationException($"Missing embedded fixture {name}.");
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> Oracle(string fixture) {
    using var resource = typeof(InnoSetupEncryptionTests).Assembly.GetManifestResourceStream("InnoVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    while (reader.ReadLine() is { } line)
      if (!line.StartsWith('#') && line.Split('\t') is [var f, var path, _, var sha] && f == fixture)
        result[path] = sha;
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Fixtures))]
  public void Given_AnEncryptedInstaller_When_ExtractedWithItsPassword_Then_EveryFileMatches(string fixture, string password, InnoEncryption _) {
    var oracle = Oracle(fixture);
    Assert.That(oracle, Has.Count.EqualTo(4));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-innoenc-" + Guid.NewGuid().ToString("N"));
    try {
      new InnoSetupFormatDescriptor().Extract(new MemoryStream(Load(fixture)), dir, password, null);
      var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => Sha(File.ReadAllBytes(p)));
      Assert.That(written, Is.EquivalentTo(oracle));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [TestCase("inno538a-enc-zip.exe"), TestCase("inno561u-enc-lzma2-solid.exe"), TestCase("inno643-enc-zip.exe")]
  public void Given_AnInstallerEncryptingOnlyItsFiles_When_OpenedWithoutPassword_Then_ItListsButRefusesToExtract(string fixture) {
    var data = Load(fixture);
    var entries = new InnoSetupFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(Oracle(fixture).Keys));
    Assert.That(entries.All(e => e.IsEncrypted));
    var reader = new InnoSetupReader(new MemoryStream(data));
    var e = Assert.Throws<ArchivePasswordException>(() => reader.ExtractMany(reader.Entries, (_, _) => { }));
    Assert.That(e!.PasswordWasSupplied, Is.False);
  }

  [Test]
  public void Given_AFullyEncryptedInstaller_When_OpenedWithoutPassword_Then_ThePasswordIsAskedFor() {
    var e = Assert.Throws<ArchivePasswordException>(() => new InnoSetupFormatDescriptor().List(new MemoryStream(Load("inno654-encfull-zip.exe")), null));
    Assert.That(e!.PasswordWasSupplied, Is.False);
  }

  [TestCase("inno538a-enc-zip.exe", "inno-secret_1")]
  [TestCase("inno561u-enc-lzma2-solid.exe", "Passwort-€1")]
  [TestCase("inno643-enc-zip.exe", "Pässwört-€")]
  [TestCase("inno654-encfull-zip.exe", "x")]
  public void Given_AWrongPassword_When_Extracted_Then_ItIsRefusedAsWrong(string fixture, string wrong) {
    var data = Load(fixture);
    var e = Assert.Throws<ArchivePasswordException>(() => {
      var reader = new InnoSetupReader(new MemoryStream(data), wrong);
      reader.ExtractMany(reader.Entries, (_, _) => { });
    });
    Assert.That(e!.PasswordWasSupplied, Is.True);
  }

  [TestCaseSource(nameof(Fixtures))]
  public void Given_AnEncryptedInstaller_When_Opened_Then_ItsEncryptionIsRecognised(string fixture, string password, InnoEncryption encryption) {
    var reader = new InnoSetupReader(new MemoryStream(Load(fixture)), password);
    Assert.That(reader.IsEncrypted);
    Assert.That(reader.AppName, Is.EqualTo("Fixture"));
    Assert.That(reader.EncryptionMethods, Is.EquivalentTo(new[] { encryption }));
  }

  // draft-irtf-cfrg-xchacha-03 (the vectors Inno Setup's own tests use)
  [Test]
  public void Given_TheHChaCha20Vector_When_DerivingTheSubkey_Then_ItMatches() {
    var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    var nonce = Convert.FromHexString("000000090000004a0000000031415927");
    Assert.That(Convert.ToHexStringLower(InnoXChaCha20.HChaCha20(key, nonce)),
      Is.EqualTo("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc"));
  }

  [Test]
  public void Given_TheXChaCha20Vector_When_Encrypting_Then_ItMatchesWholeAndInPieces() {
    var key = Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray();
    var nonce = Convert.FromHexString("404142434445464748494a4b4c4d4e4f5051525354555658");
    var plain = Encoding.ASCII.GetBytes("The dhole (pronounced \"dole\") is also known as the Asiatic wild dog, red dog, and whistling dog. "
      + "It is about the size of a German shepherd but looks more like a long-legged fox. This highly elusive and skilled jumper "
      + "is classified with wolves, coyotes, jackals, and foxes in the taxonomic family Canidae.");
    const string expected =
      "4559abba4e48c16102e8bb2c05e6947f50a786de162f9b0b7e592a9b53d0d4e98d8d6410d540a1a6375b26d80dace4fab52384c731acbf16a5923c0c48d3575d"
      + "4d0d2c673b666faa731061277701093a6bf7a158a8864292a41c48e3a9b4c0daece0f8d98d0d7e05b37a307bbb66333164ec9e1b24ea0d6c3ffddcec4f68e744"
      + "3056193a03c810e11344ca06d8ed8a2bfb1e8d48cfa6bc0eb4e2464b748142407c9f431aee769960e15ba8b96890466ef2457599852385c661f752ce20f9da0c"
      + "09ab6b19df74e76a95967446f8d0fd415e7bee2a12a114c20eb5292ae7a349ae577820d5520a1f3fb62a17ce6a7e68fa7c79111d8860920bc048ef43fe84486c"
      + "cb87c25f0ae045f0cce1e7989a9aa220a28bdd4827e751a24a6d5c62d790a66393b93111c1a55dd7421a10184974c7c5";
    var cipher = plain.ToArray();
    new InnoXChaCha20(key, nonce).Transform(cipher);
    Assert.That(Convert.ToHexStringLower(cipher), Is.EqualTo(expected));

    var pieces = plain.ToArray();
    var c = new InnoXChaCha20(key, nonce);
    c.Transform(pieces.AsSpan(0, 3));
    c.Transform(pieces.AsSpan(3, 61));
    c.Transform(pieces.AsSpan(64, 1));
    c.Transform(pieces.AsSpan(65));
    Assert.That(pieces, Is.EqualTo(cipher));
  }
}
