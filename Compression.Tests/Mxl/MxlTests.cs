using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileFormat.Mxl;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace Compression.Tests.Mxl;

/// <summary>
/// Compressed MusicXML. The fixtures are unmodified scores from the OpenSheetMusicDisplay test
/// corpus; the expected listings are 7-Zip 26.04's (<c>7z l -slt</c>). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class MxlTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MxlVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded MXL vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<TestCaseData> SevenZipListings() {
    yield return new TestCaseData("OSMD_function_test_metronome_marks.mxl", new (string, long, uint)[] {
      ("META-INF/container.xml", 155, 0x9A226A8A), ("Untitled.xml", 6410, 0x86C117D7),
    }).SetName("Given_MetronomeMarksScore_When_Read_Then_MatchesSevenZip");
    yield return new TestCaseData("test_mxl_with_utf-16_xml.mxl", new (string, long, uint)[] {
      ("META-INF/container.xml", 163, 0x39F41A52), ("mxl_test_minimal.xml", 5828, 0x41D2FC1F),
    }).SetName("Given_Utf16Score_When_Read_Then_MatchesSevenZip");
  }

  [TestCaseSource(nameof(SevenZipListings)), Category("ThirdPartyVector")]
  public void Given_OsmdScore_When_Read_Then_MatchesSevenZipListing(string vector, (string Name, long Size, uint Crc)[] expected) {
    var d = new MxlFormatDescriptor();
    var bytes = Vector(vector);
    var list = d.List(new MemoryStream(bytes), null).Where(e => !e.IsDirectory).ToList();
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(expected.Select(x => (x.Name, x.Size))));
    foreach (var (name, _, crc) in expected)
      Assert.That(Crc32.Compute(d.ExtractEntryToMemory(new MemoryStream(bytes), name, null)), Is.EqualTo(crc), name);
  }

  [TestCase("OSMD_function_test_metronome_marks.mxl", "Untitled.xml")]
  [TestCase("test_mxl_with_utf-16_xml.mxl", "mxl_test_minimal.xml")]
  [Category("ThirdPartyVector")]
  public void Given_OsmdScore_When_ContainerRead_Then_RootfileNamesAMember(string vector, string rootfile) {
    var d = new MxlFormatDescriptor();
    var bytes = Vector(vector);
    var container = Encoding.UTF8.GetString(d.ExtractEntryToMemory(new MemoryStream(bytes), "META-INF/container.xml", null));
    var named = Regex.Match(container, "full-path=\"([^\"]+)\"").Groups[1].Value;
    Assert.That(named, Is.EqualTo(rootfile));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(bytes), named, null), Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void Given_MxlPath_When_Detected_Then_Mxl() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_mxl_{Guid.NewGuid():N}.mxl");
    File.WriteAllBytes(path, Vector("test_mxl_with_utf-16_xml.mxl"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Mxl"));
    } finally { File.Delete(path); }
  }

  [Test, Category("RoundTrip")]
  public void Given_ScoreAndContainer_When_Created_Then_ReadBackIdentical() {
    var container = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><container><rootfiles><rootfile full-path=\"score.musicxml\"/></rootfiles></container>"u8.ToArray();
    var score = Encoding.UTF8.GetBytes("<score-partwise version=\"4.0\">" + new string(' ', 3000) + "</score-partwise>");
    var d = new MxlFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("META-INF/container.xml", container), ArchiveInputInfo.InMemory("score.musicxml", score)],
      new FormatCreateOptions());
    var bytes = ms.ToArray();
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(bytes), "META-INF/container.xml", null), Is.EqualTo(container));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(bytes), "score.musicxml", null), Is.EqualTo(score));
  }

  [Test, Category("EndToEnd")]
  public void Given_CreatedMxl_When_ExtractedBySevenZip_Then_ScoreIdentical() {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_mxl_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var score = Encoding.UTF8.GetBytes("<score-partwise version=\"4.0\"/>");
      var path = Path.Combine(dir, "song.mxl");
      using (var fs = File.Create(path))
        new MxlFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("META-INF/container.xml", "<container/>"u8.ToArray()),
          ArchiveInputInfo.InMemory("song.musicxml", score)], new FormatCreateOptions());
      var (stdout, _, exit) = FsInteropToolbox.Run7z($"x \"{path}\" -o\"{Path.Combine(dir, "out")}\" -y");
      Assert.That(exit, Is.Zero, stdout);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "song.musicxml")), Is.EqualTo(score));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EdgeCase")]
  public void Given_EmptyInput_When_Created_Then_EmptyListing() {
    var d = new MxlFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(d.List(ms, null), Is.Empty);
  }
}
