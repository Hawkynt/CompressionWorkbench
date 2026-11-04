using System.Reflection;
using Compression.Registry;
using FileFormat.Aar;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace Compression.Tests.Aar;

/// <summary>
/// Android library archives. The fixtures are unmodified AndroidX releases from Google's Maven
/// repository; the expected listings are 7-Zip 26.04's (<c>7z l -slt</c>). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class AarTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded AAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<TestCaseData> SevenZipListings() {
    yield return new TestCaseData("cursoradapter-1.0.0.aar", new (string, long, uint)[] {
      ("AndroidManifest.xml", 836, 0x1F89DC0E), ("R.txt", 0, 0x00000000), ("classes.jar", 11504, 0xCFE1BB1D),
    }).SetName("Given_CursorAdapterAar_When_Read_Then_MatchesSevenZip");
    yield return new TestCaseData("annotation-experimental-1.0.0.aar", new (string, long, uint)[] {
      ("R.txt", 0, 0x00000000), ("AndroidManifest.xml", 862, 0x37961B35), ("lint.jar", 10997, 0x8FA1A7D8), ("classes.jar", 2020, 0x6083FCB9),
    }).SetName("Given_AnnotationExperimentalAar_When_Read_Then_MatchesSevenZip");
  }

  [TestCaseSource(nameof(SevenZipListings)), Category("ThirdPartyVector")]
  public void Given_GoogleMavenAar_When_Read_Then_MatchesSevenZipListing(string vector, (string Name, long Size, uint Crc)[] expected) {
    var d = new AarFormatDescriptor();
    var bytes = Vector(vector);
    var list = d.List(new MemoryStream(bytes), null).Where(e => !e.IsDirectory).ToList();
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(expected.Select(x => (x.Name, x.Size))));
    foreach (var (name, _, crc) in expected)
      Assert.That(Crc32.Compute(d.ExtractEntryToMemory(new MemoryStream(bytes), name, null)), Is.EqualTo(crc), name);
  }

  [Test, Category("HappyPath")]
  public void Given_AarPath_When_Detected_Then_Aar() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_aar_{Guid.NewGuid():N}.aar");
    File.WriteAllBytes(path, Vector("cursoradapter-1.0.0.aar"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Aar"));
    } finally { File.Delete(path); }
  }

  [Test, Category("RoundTrip")]
  public void Given_LibraryMembers_When_Created_Then_ReadBackIdentical() {
    var manifest = "<manifest package=\"org.example.lib\"/>"u8.ToArray();
    var jar = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
    var d = new AarFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("AndroidManifest.xml", manifest), ArchiveInputInfo.InMemory("classes.jar", jar),
      ArchiveInputInfo.InMemory("R.txt", [])], new FormatCreateOptions());
    var bytes = ms.ToArray();
    Assert.That(d.List(new MemoryStream(bytes), null).Select(e => e.Name), Is.EqualTo(new[] { "AndroidManifest.xml", "classes.jar", "R.txt" }));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(bytes), "classes.jar", null), Is.EqualTo(jar));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(bytes), "R.txt", null), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Given_RealAar_When_MemberAddedAndRemoved_Then_OthersSurvive() {
    var d = new AarFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("cursoradapter-1.0.0.aar"));
    d.Add(ms, [ArchiveInputInfo.InMemory("proguard.txt", "-keep class *"u8.ToArray())]);
    d.Remove(ms, ["R.txt"]);
    ms.Position = 0;
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "AndroidManifest.xml", "classes.jar", "proguard.txt" }));
    Assert.That(Crc32.Compute(d.ExtractEntryToMemory(ms, "classes.jar", null)), Is.EqualTo(0xCFE1BB1Du));
  }

  [Test, Category("EndToEnd")]
  public void Given_CreatedAar_When_TestedBySevenZip_Then_EverythingOk() {
    if (!FsInteropToolbox.SevenZipAvailable) Assert.Ignore("7-Zip not available.");
    var path = Path.Combine(Path.GetTempPath(), $"cwb_aar_{Guid.NewGuid():N}.aar");
    try {
      using (var fs = File.Create(path))
        new AarFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("AndroidManifest.xml", "<manifest/>"u8.ToArray()),
          ArchiveInputInfo.InMemory("res/values/values.xml", "<resources/>"u8.ToArray())], new FormatCreateOptions());
      var (stdout, _, exit) = FsInteropToolbox.Run7z($"t \"{path}\"");
      Assert.That(exit, Is.Zero, stdout);
      Assert.That(stdout, Does.Contain("Everything is Ok"));
      Assert.That(stdout, Does.Contain("Files: 2"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptedMemberData_When_Extracted_Then_RefusedInsteadOfWrongBytes() {
    var bytes = Vector("cursoradapter-1.0.0.aar");
    var name = "classes.jar"u8;
    var lfh = bytes.AsSpan().IndexOf(name) - 30;
    Assert.That(bytes.AsSpan(lfh, 4).SequenceEqual("PK\x03\x04"u8), Is.True, "local header of classes.jar");
    var dataStart = lfh + 30 + name.Length + BitConverter.ToUInt16(bytes, lfh + 28);
    bytes[dataStart + 600] ^= 0x5A;
    Assert.Catch<Exception>(() => new AarFormatDescriptor().ExtractEntryToMemory(new MemoryStream(bytes), "classes.jar", null));
  }
}
