using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.LiveMaker;

namespace Compression.Tests.LiveMaker;

/// <summary>
/// Split LiveMaker archives: the directory in <c>name.ext</c> or at the head of <c>name.dat</c>,
/// the data in <c>name.dat</c>, <c>name.001</c>, ... pylivemaker 1.2.1 wrote the <c>pylm-*</c> sets
/// and read them back (<c>pylivemaker-listing.txt</c>); the <c>cut-*</c> sets are the same bytes cut
/// into parts, and GARbro 1.5.44 reads all of them, opened through their <c>.dat</c>
/// (<c>garbro-listing.txt</c>). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class LiveMakerSplitTests {
  private string _dir = "";

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), $"cwb-lmsplit-{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._dir);
    var asm = Assembly.GetExecutingAssembly();
    foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("LiveMakerSplit.", StringComparison.Ordinal))) {
      using var src = asm.GetManifestResourceStream(name)!;
      using var dst = File.Create(Path.Combine(this._dir, name["LiveMakerSplit.".Length..]));
      src.CopyTo(dst);
    }
  }

  [TearDown]
  public void TearDown() {
    foreach (var f in Directory.GetFiles(this._dir)) File.Delete(f);
    Directory.Delete(this._dir);
  }

  private string PathOf(string name) => Path.Combine(this._dir, name);

  private List<string[]> Listing(string listing, string key)
    => File.ReadAllText(this.PathOf(listing), Encoding.UTF8).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split('\t')).Where(f => f[0] == key).Select(f => f[1..]).ToList();

  private List<(string Name, byte[] Data)> ReadAll(string file) {
    using var fs = File.OpenRead(this.PathOf(file));
    using var set = LiveMakerArchiveSet.Open(fs);
    return [.. set.Reader.Entries.Select(e => (e.Name, set.Reader.Extract(e)))];
  }

  [Category("ThirdPartyVector")]
  [TestCase("pylm-ext.ext", "pylm-ext.dat")]
  [TestCase("pylm-ext.dat", "pylm-ext.dat")]
  [TestCase("pylm-inline.dat", "pylm-inline.dat")]
  [TestCase("cut-ext.ext", "cut-ext.dat")]
  [TestCase("cut-ext.dat", "cut-ext.dat")]
  [TestCase("cut-inline.dat", "cut-inline.dat")]
  public void Given_ASplitSet_When_OpenedThroughEitherFile_Then_EveryMemberMatchesGarbro(string opened, string garbroKey) {
    var expected = this.Listing("garbro-listing.txt", garbroKey);
    Assert.That(expected, Is.Not.Empty);

    var actual = this.ReadAll(opened);

    Assert.That(actual.Select(a => a.Name), Is.EqualTo(expected.Select(f => f[0])));
    foreach (var ((name, data), f) in actual.Zip(expected)) {
      Assert.That(data, Has.Length.EqualTo(int.Parse(f[1])), name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(f[3]), name);
    }
  }

  [Category("ThirdPartyVector")]
  [TestCase("pylm-ext.ext")]
  [TestCase("pylm-inline.dat")]
  public void Given_APylivemakerSet_When_Read_Then_ItMatchesPylivemakersOwnReading(string file) {
    var expected = this.Listing("pylivemaker-listing.txt", file);
    Assert.That(expected, Is.Not.Empty);

    var actual = this.ReadAll(file);

    Assert.That(actual.Select(a => (a.Name, Convert.ToHexStringLower(SHA256.HashData(a.Data)))),
      Is.EqualTo(expected.Select(f => (f[0], f[3]))));
  }

  [Test]
  public void Given_TheDescriptor_When_ListingAndExtractingTheExtFile_Then_TheSetIsRead() {
    var d = new LiveMakerFormatDescriptor();
    var output = Path.Combine(this._dir, "out");
    try {
      using (var fs = File.OpenRead(this.PathOf("cut-ext.ext"))) {
        Assert.That(d.List(fs, null).Select(e => e.Name), Is.EqualTo(new[] { "hello.txt", "sub/data.bin", "big.bin" }));
        fs.Position = 0;
        d.Extract(fs, output, null, null);
      }
      Assert.That(File.ReadAllBytes(Path.Combine(output, "hello.txt")), Is.EqualTo("Hello world!\n"u8.ToArray()));
      Assert.That(new FileInfo(Path.Combine(output, "big.bin")).Length, Is.EqualTo(3000));
    } finally {
      if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
    }
  }

  [Test]
  public void Given_AnExtFile_When_Detected_Then_LiveMaker()
    => Assert.That(Compression.Lib.FormatDetector.Detect(this.PathOf("pylm-ext.ext")).ToString(), Is.EqualTo("LiveMaker"));

  [Category("Exception")]
  [Test]
  public void Given_AMissingLastPart_When_Opened_Then_TheMemberPointingThereIsRefused() {
    File.Delete(this.PathOf("cut-ext.002"));

    Assert.Throws<InvalidDataException>(() => this.ReadAll("cut-ext.ext"));
  }

  [Category("Exception")]
  [Test]
  public void Given_AnExtWithoutItsDat_When_Opened_Then_ItIsRefused() {
    File.Delete(this.PathOf("pylm-ext.dat"));

    Assert.Throws<InvalidDataException>(() => this.ReadAll("pylm-ext.ext"));
  }

  [Category("Exception")]
  [Test]
  public void Given_AnExtDirectoryWithoutAFileName_When_Read_Then_ItIsRefused() {
    using var ms = new MemoryStream(File.ReadAllBytes(this.PathOf("pylm-ext.ext")));

    Assert.Throws<InvalidDataException>(() => _ = new LiveMakerReader(ms));
  }

  [Category("Exception")]
  [TestCase("cut-ext.ext")]
  [TestCase("cut-inline.dat")]
  public void Given_ASplitSet_When_Modified_Then_ItIsRefusedAndNothingChanges(string file) {
    var before = Directory.GetFiles(this._dir).ToDictionary(f => f, File.ReadAllBytes);
    using (var fs = new FileStream(this.PathOf(file), FileMode.Open, FileAccess.ReadWrite))
      Assert.Throws<NotSupportedException>(() => new LiveMakerFormatDescriptor().Remove(fs, ["hello.txt"]));

    foreach (var (f, bytes) in before)
      Assert.That(File.ReadAllBytes(f), Is.EqualTo(bytes), f);
  }
}
