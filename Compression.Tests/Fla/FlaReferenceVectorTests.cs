#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Fla;

namespace Compression.Tests.Fla;

/// <summary>
/// The reader and writer against FLA files Flash Professional CS6 saved itself. The expected member
/// lists are xfl2svg's captured reports (Python's <c>zipfile</c> did the reading), so these run on
/// every runner; <see cref="FlaXfl2SvgOracleTests"/> asks xfl2svg live wherever it is installed.
/// </summary>
[TestFixture]
public sealed class FlaReferenceVectorTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_fla_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static IEnumerable<string> AdobeFiles => FlaReferenceVectors.AdobeFlaFiles;

  /// <summary>The members the report names, as "name\tsize\tsha256" (the mimetype line apart).</summary>
  private static string[] ReportedMembers(string[] report)
    => [.. report.Where(static l => l.StartsWith("member\t", StringComparison.Ordinal)).Select(static l => l["member\t".Length..])];

  private static List<(string Name, bool IsDirectory)> CentralDirectory(byte[] fla) {
    using var zip = new ZipArchive(new MemoryStream(fla, writable: false), ZipArchiveMode.Read);
    return [.. zip.Entries.Select(static e => (e.FullName, e.FullName.EndsWith('/')))];
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenAFlaSavedByFlash_WhenListed_ThenEveryMemberAndFolderIsSeenUnderItsZipName(string vector) {
    var fla = FlaReferenceVectors.Bytes(vector);
    var listed = new FlaFormatDescriptor().List(new MemoryStream(fla), null);

    var expectedFiles = ReportedMembers(FlaReferenceVectors.Report(vector)).Select(static l => l.Split('\t')[0]).Append("mimetype");
    Assert.That(listed.Where(static e => !e.IsDirectory && e.Name is not ("FULL.fla" or "metadata.ini")).Select(static e => e.Name),
      Is.EquivalentTo(expectedFiles));
    Assert.That(listed.Where(static e => e.IsDirectory).Select(static e => e.Name.TrimEnd('/')),
      Is.EquivalentTo(new[] { "LIBRARY", "META-INF" }));
    Assert.That(listed.Single(static e => e.Name == "mimetype").Method, Is.EqualTo("stored"));
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenAFlaSavedByFlash_WhenExtracted_ThenEveryMemberMatchesWhatPythonsZipfileRead(string vector) {
    new FlaFormatDescriptor().Extract(new MemoryStream(FlaReferenceVectors.Bytes(vector)), this._tmp, null, null);

    foreach (var line in ReportedMembers(FlaReferenceVectors.Report(vector))) {
      var (name, size, sha) = line.Split('\t') is [var n, var s, var h] ? (n, int.Parse(s), h) : throw new FormatException(line);
      var data = File.ReadAllBytes(Path.Combine(this._tmp, name.Replace('/', Path.DirectorySeparatorChar)));
      Assert.That((data.Length, FlaReferenceVectors.Sha256(data)), Is.EqualTo((size, sha)), name);
    }
    Assert.That(File.ReadAllText(Path.Combine(this._tmp, "mimetype")), Is.EqualTo("application/vnd.adobe.xfl"));
    Assert.That(File.ReadAllText(Path.Combine(this._tmp, "metadata.ini")), Does.Contain("format=xfl"));
  }

  [Test]
  public void GivenAFlaWithAnEmptyLibrary_WhenExtracted_ThenTheEmptyLibraryFolderIsRecreated() {
    new FlaFormatDescriptor().Extract(new MemoryStream(FlaReferenceVectors.Bytes("namespaces.fla")), this._tmp, null, null);

    var library = Path.Combine(this._tmp, "LIBRARY");
    Assert.That(Directory.Exists(library), Is.True);
    Assert.That(Directory.EnumerateFileSystemEntries(library), Is.Empty);
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenTheMembersOfAFlaSavedByFlash_WhenRepacked_ThenTheMemberOrderIsFlashsOwn(string vector) {
    var original = FlaReferenceVectors.Bytes(vector);
    var expected = CentralDirectory(original);
    Assert.That(expected[^1].Name, Is.EqualTo("mimetype"), "Flash CS6 writes mimetype last");

    using var source = new ZipArchive(new MemoryStream(original, writable: false), ZipArchiveMode.Read);
    var inputs = source.Entries.Select(static e => {
      if (e.FullName.EndsWith('/')) return new ArchiveInputInfo(e.FullName, e.FullName, IsDirectory: true);
      using var s = e.Open();
      return ArchiveInputInfo.InMemory(e.FullName, s);
    }).ToArray();
    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var written = output.ToArray();
    Assert.That(CentralDirectory(written), Is.EqualTo(expected));
    using var ours = new ZipArchive(new MemoryStream(written, writable: false), ZipArchiveMode.Read);
    foreach (var entry in source.Entries.Where(static e => !e.FullName.EndsWith('/'))) {
      using var a = entry.Open();
      using var b = ours.GetEntry(entry.FullName)!.Open();
      using var x = new MemoryStream();
      using var y = new MemoryStream();
      a.CopyTo(x);
      b.CopyTo(y);
      Assert.That(y.ToArray(), Is.EqualTo(x.ToArray()), entry.FullName);
    }
  }

  [Test]
  public void GivenAnXflProjectWithBinaryMedia_WhenPacked_ThenTheMediaAndEveryOtherFileComeBackByteIdentical() {
    var project = FlaReferenceVectors.Project("test_images");
    var root = Path.Combine(this._tmp, "test_images");
    FlaReferenceVectors.WriteProject(project, root);

    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, FlaReferenceVectors.InputsFromFolder(root), new FormatCreateOptions());

    var extracted = Path.Combine(this._tmp, "out");
    output.Position = 0;
    new FlaFormatDescriptor().Extract(output, extracted, null, null);
    foreach (var (name, data) in project)
      Assert.That(File.ReadAllBytes(Path.Combine(extracted, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
    Assert.That(project.Keys, Does.Contain("bin/M 3 1697872498.dat"), "the vector carries Flash's binary bitmap data");
  }

  [Test]
  public void GivenNestedFoldersAndNonAsciiNames_WhenPacked_ThenTheNamesAreUtf8FlaggedAndComeBackUnchanged() {
    const string symbol = "LIBRARY/Ordner/Unterordner/Ünïcødé 按钮.xml";
    var inputs = new[] {
      ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8),
      ArchiveInputInfo.InMemory(symbol, Encoding.UTF8.GetBytes("<DOMSymbolItem/>")),
    };
    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var bytes = output.ToArray();
    var nameBytes = Encoding.UTF8.GetBytes(symbol);
    var header = bytes.AsSpan().IndexOf(nameBytes) - 30;
    Assert.That(header, Is.GreaterThanOrEqualTo(0), "the name is stored as UTF-8");
    Assert.That(BitConverter.ToUInt16(bytes, header + 6) & 0x0800, Is.EqualTo(0x0800), "general purpose bit 11 (UTF-8) is set");
    output.Position = 0;
    Assert.That(new FlaFormatDescriptor().List(output, null).Select(static e => e.Name), Does.Contain(symbol));
  }
}
