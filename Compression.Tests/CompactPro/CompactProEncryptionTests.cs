using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.CompactPro;

namespace Compression.Tests.CompactPro;

/// <summary>
/// Compact Pro's encrypted entries against MacExtract, whose reading of the cipher this one is
/// converted from: <c>macextract-listing.txt</c> holds what MacExtract's command-line extractor
/// writes for each encrypted fixture under the right and a wrong password. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public sealed class CompactProEncryptionTests {
  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CompactProVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Compact Pro vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>The files MacExtract wrote for <paramref name="vector"/> under its right password: path → SHA-256.</summary>
  private static Dictionary<string, string> Expected(string vector) {
    var lines = Encoding.UTF8.GetString(Vector("macextract-listing.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var inBlock = false;
    foreach (var line in lines) {
      if (line.StartsWith('#')) {
        inBlock = line.StartsWith($"# {vector} ", StringComparison.Ordinal) && line.EndsWith("exit=0", StringComparison.Ordinal);
        continue;
      }
      var p = line.Split('\t');
      if (inBlock && p[0] == vector && !p[1].EndsWith(".comment.txt", StringComparison.Ordinal))
        result[p[1]] = p[2];
    }
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static readonly string MacRomanUmlautPassword = "Kennwört"; // ö is 0x9A in Mac Roman

  [TestCase("enc-trashcht.cpt", "ohsecret")]                  // resource and data fork, LZH; preamble in the resource fork
  [TestCase("enc-dgsmrmcr.cpt", "correct horse battery")]     // data forks only, RLE and LZH; a password past eight bytes
  [TestCase("macextract-synth.cpt", "ohsecret")]              // MacExtract's own writer: RLE, resource-only and two-fork entries
  [Category("ThirdPartyVector")]
  public void GivenAnEncryptedArchive_WhenReadWithItsPassword_ThenEveryForkMatchesMacExtract(string vector, string password)
    => AssertMatches(vector, password, expectedEncrypted: null);

  [Test, Category("ThirdPartyVector")]
  public void GivenOneEncryptedEntryAndAMacRomanPassword_WhenRead_ThenEveryForkMatchesMacExtract()
    => AssertMatches("enc-ours-flat.cpt", MacRomanUmlautPassword, expectedEncrypted: ["readme.txt"]);

  private static void AssertMatches(string vector, string password, string[]? expectedEncrypted) {
    var expected = Expected(vector);
    Assert.That(expected, Is.Not.Empty);
    var r = new CompactProReader(new MemoryStream(Vector(vector)));
    var seen = 0;
    foreach (var e in r.Entries.Where(e => !e.IsDirectory)) {
      if (expectedEncrypted != null)
        Assert.That(e.IsEncrypted, Is.EqualTo(expectedEncrypted.Contains(e.FileName)), e.FileName);
      else
        Assert.That(e.IsEncrypted, Is.True, e.FileName);
      var (data, resource) = r.ExtractForks(e, password);
      Assert.That(Sha(data), Is.EqualTo(expected[e.FileName]), e.FileName);
      ++seen;
      if (resource.Length > 0) {
        Assert.That(Sha(resource), Is.EqualTo(expected[e.FileName + ".rsrc"]), e.FileName + ".rsrc");
        ++seen;
      }
    }
    Assert.That(seen, Is.EqualTo(expected.Count));
  }

  [TestCase("enc-trashcht.cpt", "ohsecreT")]
  [TestCase("enc-dgsmrmcr.cpt", "correct horse")]
  [TestCase("enc-ours-flat.cpt", "Kennwort")]
  [TestCase("macextract-synth.cpt", "")]
  public void GivenAWrongPassword_WhenAnEncryptedEntryIsRead_ThenItIsRefusedAndClearEntriesStillRead(string vector, string password) {
    var r = new CompactProReader(new MemoryStream(Vector(vector)));
    foreach (var e in r.Entries.Where(e => !e.IsDirectory)) {
      if (e.IsEncrypted) {
        var ex = Assert.Throws<ArchivePasswordException>(() => r.ExtractForks(e, password), e.FileName);
        Assert.That(ex!.PasswordWasSupplied, Is.True);
      } else
        Assert.DoesNotThrow(() => r.ExtractForks(e, password), e.FileName);
    }
  }

  [Test]
  public void GivenNoPassword_WhenAnEncryptedEntryIsRead_ThenAPasswordIsAskedFor() {
    var r = new CompactProReader(new MemoryStream(Vector("enc-trashcht.cpt")));
    var ex = Assert.Throws<ArchivePasswordException>(() => r.ExtractForks(r.Entries.First(e => !e.IsDirectory)));
    Assert.That(ex!.PasswordWasSupplied, Is.False);
  }

  [Test]
  public void GivenTheDescriptor_WhenExtractingWithAndWithoutThePassword_ThenThePasswordIsUsedOrAskedFor() {
    var d = new CompactProFormatDescriptor();
    var archive = Vector("enc-ours-flat.cpt");
    Assert.That(d.List(new MemoryStream(archive), null).Single(e => e.Name == "readme.txt").IsEncrypted, Is.True);
    Assert.That(Sha(d.ExtractEntryToMemory(new MemoryStream(archive), "readme.txt", MacRomanUmlautPassword)), Is.EqualTo(Expected("enc-ours-flat.cpt")["readme.txt"]));
    Assert.That(Sha(d.ExtractEntryToMemory(new MemoryStream(archive), "tricky.bin", null)), Is.EqualTo(Expected("enc-ours-flat.cpt")["tricky.bin"]));
    Assert.Throws<ArchivePasswordException>(() => d.ExtractEntryToMemory(new MemoryStream(archive), "readme.txt", null));
  }

  [Test]
  public void GivenAnEncryptedEntry_WhenListed_ThenThePreambleIsCountedInTheFirstNonEmptyFork() {
    var clear = new CompactProReader(new MemoryStream(Vector("dgsmrmcr.cpt"))).Entries.Where(e => !e.IsDirectory).ToList();
    var encrypted = new CompactProReader(new MemoryStream(Vector("enc-dgsmrmcr.cpt"))).Entries.Where(e => !e.IsDirectory).ToList();
    for (var i = 0; i < clear.Count; ++i) {
      Assert.That(encrypted[i].ResourceForkCompressedSize, Is.EqualTo(clear[i].ResourceForkCompressedSize));
      Assert.That(encrypted[i].DataForkCompressedSize, Is.EqualTo(clear[i].DataForkCompressedSize + CompactProCipher.PreambleLength));
    }
    var twoForks = new CompactProReader(new MemoryStream(Vector("enc-trashcht.cpt"))).Entries.Single(e => e.ResourceForkSize > 0);
    var twoForksClear = new CompactProReader(new MemoryStream(Vector("trashcht.cpt"))).Entries.Single(e => e.ResourceForkSize > 0);
    Assert.That(twoForks.ResourceForkCompressedSize, Is.EqualTo(twoForksClear.ResourceForkCompressedSize + CompactProCipher.PreambleLength));
    Assert.That(twoForks.DataForkCompressedSize, Is.EqualTo(twoForksClear.DataForkCompressedSize));
  }

  private static byte[] Keystream(string password, int length) {
    var buffer = new byte[length];
    new CompactProCipher(CompactProReader.MacRoman.GetBytes(password)).Encrypt(buffer);
    return buffer;
  }

  [TestCase("")]
  [TestCase("a")]
  [TestCase("12345678")]
  [TestCase("a much longer password than eight")]
  public void GivenAnyPassword_WhenEncryptedThenDecrypted_ThenTheBytesComeBack(string password) {
    var plain = Enumerable.Range(0, 1000).Select(i => (byte)(i * 37 + (i >> 3))).ToArray();
    var buffer = (byte[])plain.Clone();
    new CompactProCipher(CompactProReader.MacRoman.GetBytes(password)).Encrypt(buffer);
    Assert.That(buffer, Is.Not.EqualTo(plain));
    new CompactProCipher(CompactProReader.MacRoman.GetBytes(password)).Decrypt(buffer);
    Assert.That(buffer, Is.EqualTo(plain));
  }

  // The key is at most eight bytes: a ninth character is added into the first accumulator.
  [Test]
  public void GivenANinthCharacter_WhenKeyed_ThenItIsAddedIntoTheFirstKeyByte() {
    Assert.That(Keystream("Aaaaaaaa\u0001", 64), Is.EqualTo(Keystream("Baaaaaaa", 64)));
    Assert.That(Keystream("Aaaaaaaa", 64), Is.Not.EqualTo(Keystream("Baaaaaaa", 64)));
    Assert.That(Keystream("abc", 64), Is.Not.EqualTo(Keystream("abcd", 64)));
  }
}
