using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// StuffIt X against archives StuffIt itself wrote (ssokolow/stuffit-test-files, CC0; see
/// ReferenceVectors/README.md). Every fork is compared with the SHA-256 of the file it was made
/// from (resource forks with the AppleDouble copy of the source); every stream must also pass the
/// CRC-32 the archive stores; The Unarchiver's unar is the oracle where it can read the archive.
/// </summary>
[TestFixture]
public class StuffItXTests {
  private const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
  private const string Jpg = "e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1";
  private const string Png = "fdda20984cc1591419ec4583e24e72e4dba39d0b96608253f853a2dfb238ad1a";
  private const string WinTxt = "b2f51cd17b3cbe77f091f887d91110164a2cb5a5a9ebe828c44d655c83dca8eb";

  private static readonly Dictionary<string, string> MacSources = new() {
    ["Test Image"] = Empty,
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

  private static readonly Dictionary<string, string> WinSources = new() {
    ["sources/testfile.jpg"] = Jpg,
    ["sources/testfile.png"] = Png,
    ["sources/testfile.txt"] = WinTxt,
  };

  private static readonly Dictionary<string, string> WinBasicSources = new() { ["testfile.txt"] = WinTxt };

  private static readonly string[] AllVectors = [
    "testfile.stuffit7_dlx.mac9.sitx",
    "testfile.stuffit7_dlx.macx1.sitx",
    "testfile.stuffit_deluxe_2009.win.backcompat.sitx",
    "testfile.stuffit_deluxe_2009.win.basic.sitx",
    "testfile.stuffit_deluxe_2009.win.sitx",
    "testfile.stuffit_deluxe_2010.win.basic.sitx",
    "testfile.stuffit_deluxe_2010.win.sitx",
  ];

  private static byte[] Vector(string name) {
    using var s = typeof(StuffItXTests).Assembly.GetManifestResourceStream("StuffItXVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  private static IEnumerable<TestCaseData> FullyDecodable() {
    yield return new TestCaseData("testfile.stuffit7_dlx.mac9.sitx", "Brimstone+Cyanide", MacSources);
    yield return new TestCaseData("testfile.stuffit7_dlx.macx1.sitx", "Brimstone+Cyanide", MacSources);
    yield return new TestCaseData("testfile.stuffit_deluxe_2009.win.backcompat.sitx", "Cyanide", WinSources);
    yield return new TestCaseData("testfile.stuffit_deluxe_2009.win.basic.sitx", "Iron", WinBasicSources);
    yield return new TestCaseData("testfile.stuffit_deluxe_2010.win.basic.sitx", "Iron", WinBasicSources);
  }

  [TestCaseSource(nameof(FullyDecodable)), Category("ThemVsUs")]
  public void GivenStuffItWrittenArchive_WhenExtracted_ThenEveryForkMatchesItsSourceFile(string vector, string methods, Dictionary<string, string> expected) {
    using var r = new StuffItXReader(new MemoryStream(Vector(vector)));
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FullPath, e => Sha(r.Extract(e)));
    Assert.That(files, Is.EquivalentTo(expected), methods);
  }

  [TestCase("testfile.stuffit_deluxe_2009.win.sitx")]
  [TestCase("testfile.stuffit_deluxe_2010.win.sitx")]
  [Category("ThemVsUs")]
  public void GivenArchiveWithMethod7_WhenExtracted_ThenIronForksMatchAndMethod7IsRefused(string vector) {
    using var r = new StuffItXReader(new MemoryStream(Vector(vector)));
    var jpg = r.Entries.Single(e => e.FullPath == "sources/testfile.jpg");
    Assert.That(jpg.Method, Is.EqualTo("Method 7"));
    Assert.That(jpg.OriginalSize, Is.EqualTo(220));
    Assert.That(() => r.Extract(jpg), Throws.InstanceOf<NotSupportedException>());
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.FullPath == "sources/testfile.png"))), Is.EqualTo(Png));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.FullPath == "sources/testfile.txt"))), Is.EqualTo(WinTxt));
    Assert.That(r.Entries.Single(e => e.FullPath == "sources").IsDirectory, Is.True);
  }

  [TestCaseSource(nameof(AllVectors)), Category("ThemVsUs")]
  public void GivenStuffItWrittenArchive_WhenEveryStreamIsDecoded_ThenEachPassesItsStoredCrc(string vector) {
    using var r = new StuffItXReader(new MemoryStream(Vector(vector)));
    var checkedStreams = 0;
    foreach (var id in r.StreamIds) {
      try {
        r.DecodeStream(id);
        ++checkedStreams;
      } catch (NotSupportedException) {
        // method 7 has no decoder anywhere
      }
    }
    Assert.That(checkedStreams, Is.GreaterThan(0));
  }

  [Test, Category("ThemVsUs")]
  public void GivenWin2009Archive_WhenThePreviewStreamIsDecoded_ThenTheObscuredStoredMethodYieldsAJpeg() {
    // The thumbnail stream uses method 5 (stored, RC4-obscured) and its CRC covers all 661 bytes,
    // more than the preview fork claims.
    using var r = new StuffItXReader(new MemoryStream(Vector("testfile.stuffit_deluxe_2009.win.sitx")));
    var preview = r.DecodeStream(9);
    Assert.That(preview, Has.Length.EqualTo(661));
    Assert.That(preview[..4], Is.EqualTo(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
  }

  [Test, Category("HappyPath")]
  public void GivenMacArchive_WhenListed_ThenCatalogDatesAreRead() {
    using var r = new StuffItXReader(new MemoryStream(Vector("testfile.stuffit7_dlx.mac9.sitx")));
    var txt = r.Entries.Single(e => e.FullPath == "testfile.txt");
    Assert.That(txt.LastModified, Is.Not.Null);
    Assert.That(txt.LastModified!.Value.Year, Is.InRange(2001, 2030));
    Assert.That(r.Entries.Single(e => e.FullPath == "testfile.txt.rsrc").IsResourceFork, Is.True);
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptor_WhenListingThroughTheRegistryApi_ThenNamesSizesAndMethodsAreReported() {
    var d = new StuffItXFormatDescriptor();
    var list = d.List(new MemoryStream(Vector("testfile.stuffit_deluxe_2009.win.backcompat.sitx")), null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "sources", "sources/testfile.jpg", "sources/testfile.png", "sources/testfile.txt" }));
    Assert.That(list.Single(e => e.Name == "sources/testfile.jpg").OriginalSize, Is.EqualTo(220));
    Assert.That(list.Single(e => e.Name == "sources/testfile.jpg").Method, Is.EqualTo("Cyanide"));
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptor_WhenExtracting_ThenFilesLandUnderTheirPaths() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sitx_{Guid.NewGuid():N}");
    try {
      new StuffItXFormatDescriptor().Extract(new MemoryStream(Vector("testfile.stuffit_deluxe_2009.win.backcompat.sitx")), dir, null, null);
      Assert.That(Sha(File.ReadAllBytes(Path.Combine(dir, "sources", "testfile.txt"))), Is.EqualTo(WinTxt));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("Contract")]
  public void GivenDescriptor_ThenItIsReadOnly() {
    var d = new StuffItXFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(StuffItXReader.IsStuffItX(Vector("testfile.stuffit_deluxe_2010.win.basic.sitx")), Is.True);
  }

  // ── P2 numbers ───────────────────────────────────────────────────────────────

  [TestCase(new byte[] { 0x02 }, 0UL)]          // "0 1": one value bit at position 0
  [TestCase(new byte[] { 0x5B }, 10UL)]         // a catalog string length
  [TestCase(new byte[] { 0x6B }, 12UL)]
  [TestCase(new byte[] { 0x5C, 0x03 }, 12UL)]   // crosses a byte boundary
  [Category("Boundary")]
  public void GivenP2Bits_WhenRead_ThenValueMatches(byte[] bits, ulong expected) {
    var input = new StuffItXInput(bits);
    if (bits[0] == 0x5C) input.ReadP2(); // the catalog key in front of the length
    Assert.That(input.ReadP2(), Is.EqualTo(expected));
  }

  [Test, Category("Exception")]
  public void GivenAllOnesP2_WhenRead_ThenRefused()
    => Assert.That(() => new StuffItXInput(Enumerable.Repeat((byte)0xFF, 16).ToArray()).ReadP2(), Throws.InstanceOf<InvalidDataException>());

  // ── refusals ─────────────────────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void GivenNonStuffItXData_WhenOpened_ThenRefused() {
    Assert.That(() => new StuffItXReader(new MemoryStream("PK\x03\x04 not stuffit"u8.ToArray())), Throws.InstanceOf<InvalidDataException>());
    Assert.That(() => new StuffItXReader(new MemoryStream([])), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenBaseNEncodedArchive_WhenOpened_ThenNotSupported()
    => Assert.That(() => new StuffItXReader(new MemoryStream("StuffIt?AAAA"u8.ToArray())), Throws.InstanceOf<NotSupportedException>());

  [Test, Category("Exception")]
  public void GivenTruncatedArchive_WhenOpened_ThenRefused() {
    var bytes = Vector("testfile.stuffit_deluxe_2009.win.backcompat.sitx");
    Assert.That(() => new StuffItXReader(new MemoryStream(bytes[..(bytes.Length / 2)])), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenStoredCrcThatDisagrees_WhenExtracted_ThenRefused() {
    var corrupt = Vector("testfile.stuffit_deluxe_2009.win.basic.sitx");
    // CRC-32 of "Testing 123\n", stored big-endian after the stream's blocks
    var at = corrupt.AsSpan().IndexOf(new byte[] { 0x6E, 0xC1, 0x8F, 0xFE });
    Assert.That(at, Is.GreaterThan(0));
    corrupt[at + 3] ^= 0x01;
    using var r = new StuffItXReader(new MemoryStream(corrupt));
    Assert.That(() => r.Extract(r.Entries.Single(e => e.FullPath == "testfile.txt")), Throws.InstanceOf<InvalidDataException>());
  }

  // ── The Unarchiver ───────────────────────────────────────────────────────────

  [TestCase("testfile.stuffit_deluxe_2009.win.backcompat.sitx")]
  [TestCase("testfile.stuffit_deluxe_2009.win.basic.sitx")]
  [TestCase("testfile.stuffit_deluxe_2009.win.sitx")]
  [TestCase("testfile.stuffit_deluxe_2010.win.basic.sitx")]
  [TestCase("testfile.stuffit_deluxe_2010.win.sitx")]
  [Category("ExternalInterop")]
  public void GivenArchiveUnarReads_WhenExtractedByBoth_ThenDataForksMatch(string vector) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitx_unar_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var archive = Path.Combine(tmp, vector);
      var bytes = Vector(vector);
      File.WriteAllBytes(archive, bytes);
      var outDir = Path.Combine(tmp, "out");
      FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(archive)}'");
      using var r = new StuffItXReader(new MemoryStream(bytes));
      var compared = 0;
      foreach (var e in r.Entries.Where(e => !e.IsDirectory && !e.IsResourceFork && e.Method != "Method 7")) {
        var theirs = Path.Combine(outDir, e.FullPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(theirs), Is.True, $"unar did not write {e.FullPath}");
        Assert.That(r.Extract(e), Is.EqualTo(File.ReadAllBytes(theirs)), e.FullPath);
        ++compared;
      }
      Assert.That(compared, Is.GreaterThan(0));
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
