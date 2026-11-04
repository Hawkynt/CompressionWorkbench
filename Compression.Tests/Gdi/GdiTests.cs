using System.Text;
using FileFormat.Gdi;

namespace Compression.Tests.Gdi;

[TestFixture]
public class GdiTests {
  // The canonical three-track GD-ROM layout: low-density data, audio, high-density data at LBA 45000.
  private const string Canonical = "3\r\n1 0 4 2352 track01.bin 0\r\n2 756 0 2352 track02.raw 0\r\n3 45000 4 2352 track03.bin 0\r\n";

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-gdi-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  [Test]
  public void GivenTheCanonicalLayout_WhenParsed_ThenEveryFieldIsRead() {
    var tracks = GdiFormatDescriptor.Parse(Canonical);
    Assert.That(tracks, Has.Count.EqualTo(3));
    Assert.That(tracks[2], Is.EqualTo(new GdiTrack(3, 45000, 4, 2352, "track03.bin", 0)));
    Assert.That(tracks[1].IsData, Is.False);
  }

  [Test]
  public void GivenQuotedNamesWithSpaces_WhenParsed_ThenTheNameIsKeptWhole() {
    var tracks = GdiFormatDescriptor.Parse("1\n1 0 4 2048 \"Track 01 (data).iso\" 0\n");
    Assert.That(tracks[0].FileName, Is.EqualTo("Track 01 (data).iso"));
  }

  [Test]
  public void GivenAManifestBesideItsTracks_WhenExtracted_ThenTheTrackFilesComeBackUnchanged() {
    var t1 = new byte[2352 * 3];
    var t3 = new byte[2352 * 5];
    new Random(1).NextBytes(t1);
    new Random(3).NextBytes(t3);
    File.WriteAllText(Path.Combine(this._tmp, "disc.gdi"), Canonical, Encoding.ASCII);
    File.WriteAllBytes(Path.Combine(this._tmp, "track01.bin"), t1);
    File.WriteAllBytes(Path.Combine(this._tmp, "track03.bin"), t3);   // track02.raw deliberately absent

    var d = new GdiFormatDescriptor();
    using (var fs = File.OpenRead(Path.Combine(this._tmp, "disc.gdi"))) {
      var names = d.List(fs, null).Select(e => e.Name).ToList();
      Assert.That(names, Is.EqualTo(new[] { "metadata.ini", "track01.bin", "track03.bin" }));
    }
    var outDir = Path.Combine(this._tmp, "out");
    using (var fs = File.OpenRead(Path.Combine(this._tmp, "disc.gdi")))
      d.Extract(fs, outDir, null, null);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "track01.bin")), Is.EqualTo(t1));
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "track03.bin")), Is.EqualTo(t3));
    Assert.That(File.ReadAllText(Path.Combine(outDir, "metadata.ini")), Does.Contain("file=track02.raw").And.Contain("present=no"));
  }

  [Test]
  public void GivenAManifestWithoutAFile_WhenListed_ThenOnlyMetadataAppears()
    => Assert.That(new GdiFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes(Canonical)), null).Single().Name, Is.EqualTo("metadata.ini"));

  [TestCase("")]
  [TestCase("x\n")]
  [TestCase("2\n1 0 4 2352 a.bin 0\n")]
  [TestCase("1\n1 0 7 2352 a.bin 0\n")]
  [TestCase("1\n1 0 4 1000 a.bin 0\n")]
  [TestCase("1\n1 0 4 2352 ../escape.bin 0\n")]
  [TestCase("1\n1 0 4 2352 \"open.bin 0\n")]
  public void GivenAMalformedManifest_WhenParsed_ThenInvalidData(string text)
    => Assert.Throws<InvalidDataException>(() => GdiFormatDescriptor.Parse(text));

  [Test]
  public void GivenGarbage_WhenListed_ThenItFallsBack()
    => Assert.That(new GdiFormatDescriptor().List(new MemoryStream([0, 1, 2, 3]), null).Single().Name, Is.EqualTo("FULL.gdi"));
}
