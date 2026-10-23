using System.Reflection;
using System.Text;
using FileFormat.Egg;

namespace Compression.Tests.Egg;

/// <summary>
/// EGG archives ALZip wrote, from EggDotNet's test corpus (MIT). Each data block carries the CRC-32
/// ALZip computed, which the reader verifies, so a member that decodes is byte-exact; the content
/// assertions are the ones EggDotNet's own tests make. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class EggReferenceTests {
  private const string Password = "password12345!";

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EggVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded EGG vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static EggReader Open(string name) => new(new MemoryStream(Vector(name)));

  [TestCase("azoshort_txt.egg", "AZO")]
  [TestCase("azonumbers.egg", "AZO")]
  [TestCase("lzma_simple.egg", "LZMA")]
  [Category("ThirdPartyVector")]
  public void Given_CompressedMember_When_Extracted_Then_AlZipCrcHolds(string vector, string method) {
    using var r = Open(vector);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.MethodName), Has.Some.EqualTo(method));
    foreach (var e in files)
      Assert.That(r.Extract(e), Has.Length.EqualTo(e.UncompressedSize), e.Name);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_LzmaArchive_When_Extracted_Then_SizesMatchEggDotNet() {
    using var r = Open("lzma_simple.egg");
    var e = r.Entries.Single(x => x.Name == "lorem_ipsum.txt");
    Assert.That(e.CompressedSize, Is.EqualTo(5723));
    Assert.That(r.Extract(e), Has.Length.EqualTo(34446));
  }

  [TestCase("lorem_long_zipEnc.egg")]
  [TestCase("lorem_long_aes128.egg")]
  [TestCase("lorem_long_aes256.egg")]
  [TestCase("lorem_long_store_lea128.egg")]
  [TestCase("lorem_long_store_lea256.egg")]
  [Category("ThirdPartyVector")]
  public void Given_EncryptedArchive_When_Decrypted_Then_LoremText(string vector) {
    using var r = Open(vector);
    var e = r.Entries.Single(x => !x.IsDirectory);
    Assert.That(e.IsEncrypted, Is.True);
    var text = Encoding.UTF8.GetString(r.Extract(e, Password));
    Assert.That(text, Has.Length.EqualTo(15_238));
    Assert.That(text, Does.StartWith("Lorem ipsum dolor sit amet"));
    Assert.That(text, Does.EndWith("sed faucibus orci ligula eu nisi."));
  }

  [TestCase("lorem_long_zipEnc.egg")]
  [TestCase("lorem_long_aes256.egg")]
  [TestCase("lorem_long_store_lea128.egg")]
  [Category("EdgeCase")]
  public void Given_EncryptedArchive_When_WrongOrNoPassword_Then_Refused(string vector) {
    using var r = Open(vector);
    var e = r.Entries.Single(x => !x.IsDirectory);
    Assert.Throws<InvalidOperationException>(() => r.Extract(e));
    Assert.Throws<InvalidDataException>(() => r.Extract(e, "badpassword"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_SolidArchive_When_Extracted_Then_FirstEqualsLast() {
    using var r = Open("solid.egg");
    Assert.That(r.IsSolid, Is.True);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(r.Extract(files[^1]), Is.EqualTo(r.Extract(files[0])));
  }

  [TestCase("small_kor.egg", "작은.txt")]
  [TestCase("small_shift.egg", "小さい.txt")]
  [Category("ThirdPartyVector")]
  public void Given_CodePageName_When_Read_Then_DecodedAndHelloWorld(string vector, string name) {
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    using var r = Open(vector);
    var e = r.Entries.First();
    Assert.That(e.Name, Is.EqualTo(name));
    Assert.That(Encoding.UTF8.GetString(r.Extract(e)), Is.EqualTo("Hello, world!"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_NestedDirectories_When_Listed_Then_DeepFilePresent() {
    using var r = Open("directories.egg");
    Assert.That(r.Entries.Select(e => e.Name), Has.Some.EqualTo("dir1/dir2/dir3/test.txt"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_LargeMember_When_Listed_Then_SizesMatchEggDotNet() {
    using var r = Open("zeros.egg");
    var e = r.Entries.Single();
    Assert.That(e.CompressedSize, Is.EqualTo(338));
    Assert.That(e.UncompressedSize, Is.EqualTo(197_540_460));
  }

  [TestCase("posix_small.egg")]
  [TestCase("globalcomment.egg")]
  [Category("ThirdPartyVector")]
  public void Given_Archive_When_EveryMemberExtracted_Then_CrcHolds(string vector) {
    using var r = Open(vector);
    foreach (var e in r.Entries.Where(e => !e.IsDirectory))
      Assert.That(r.Extract(e), Has.Length.EqualTo(e.UncompressedSize), e.Name);
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptAzoBlock_When_Extracted_Then_InvalidData() {
    var bytes = Vector("azoshort_txt.egg");
    using var probe = new EggReader(new MemoryStream(bytes));
    var e = probe.Entries.Single();
    bytes[bytes.Length - 20] ^= 0x5A;
    using var r = new EggReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.Single()));
  }

  private static byte[] OursText() =>
    Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"EGG line {i % 17}\r\n")));

  private static byte[] OursRaw() => [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))];

  // ours.egg is what our writer produced for these three inputs; EggDotNet read it back
  // (ReferenceVectors/eggdotnet-listing.txt: both members Deflate, CRC valid, SHA-256 as below).
  [Test, Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_BytesEqualTheEggDotNetAcceptedFixture() {
    using var ms = new MemoryStream();
    new EggFormatDescriptor().Create(ms, [
      Compression.Registry.ArchiveInputInfo.InMemory("docs/text.txt", OursText()),
      Compression.Registry.ArchiveInputInfo.InMemory("empty.txt", []),
      Compression.Registry.ArchiveInputInfo.InMemory("raw.bin", OursRaw()),
    ], new Compression.Registry.FormatCreateOptions());
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("ours.egg")));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_OurFixture_When_Read_Then_MatchesEggDotNetListing() {
    using var r = Open("ours.egg");
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "docs/text.txt", "empty.txt", "raw.bin" }));
    var text = r.Extract(r.Entries.Single(e => e.Name == "docs/text.txt"));
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(text)),
      Is.EqualTo("53ff0c178b3267b2dffbdd59979fba67927f8cc7cc60a4eb209b7c06e92096c9"));
    Assert.That(text, Is.EqualTo(OursText()));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "raw.bin")), Is.EqualTo(OursRaw()));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "empty.txt")), Is.Empty);
  }
}
