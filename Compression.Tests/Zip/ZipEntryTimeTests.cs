using Compression.Lib;
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP entries carry the time of what they were made from, extraction gives it back, and a
/// folder added to an existing archive is kept. New archives used to stamp every entry with
/// 1980-01-01, so a rebuild reset every time; an added folder was dropped.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser (vendored 478d366d).</remarks>
[TestFixture]
public sealed class ZipEntryTimeTests {
  // DOS times have a two-second resolution and are local.
  private static readonly DateTime When = new(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_ziptime_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static MemoryStream Create(params ArchiveInputInfo[] inputs) {
    var stream = new MemoryStream();
    new ZipFormatDescriptor().Create(stream, inputs, new FormatCreateOptions());
    var expandable = new MemoryStream();
    expandable.Write(stream.ToArray()); // the writer may have closed its output
    expandable.Position = 0;
    return expandable;
  }

  [Test, Category("HappyPath")]
  public void GivenAnInputWithATime_WhenZipped_ThenTheEntryCarriesItAsLocalTime() {
    using var zip = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When)));

    var listed = new ZipFormatDescriptor().List(zip, null).Single();

    Assert.That(listed.LastModified, Is.EqualTo(When.ToLocalTime()).Within(TimeSpan.FromSeconds(2)));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolderWithATime_WhenZipped_ThenTheFolderEntryCarriesIt() {
    using var zip = Create(new ArchiveInputInfo("", "sub/", true, Metadata: new ArchiveEntryMetadata(LastWriteTimeUtc: When)));

    var listed = new ZipFormatDescriptor().List(zip, null).Single();

    Assert.That(listed.LastModified, Is.EqualTo(When.ToLocalTime()).Within(TimeSpan.FromSeconds(2)));
  }

  [Test, Category("HappyPath")]
  public void GivenAnEntryWithATime_WhenExtracted_ThenTheFileGetsThatTime() {
    using var zip = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When)));
    var outDir = Path.Combine(this._dir, "x");

    new ZipFormatDescriptor().Extract(zip, outDir, null, null);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(outDir, "a.txt")), Is.EqualTo(When).Within(TimeSpan.FromSeconds(2)));
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEntryWithoutATime_WhenExtracted_ThenTheFileKeepsItsExtractionTime() {
    using var zip = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8));
    var outDir = Path.Combine(this._dir, "x");
    var before = DateTime.UtcNow.AddMinutes(-1);

    new ZipFormatDescriptor().Extract(zip, outDir, null, null);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(outDir, "a.txt")), Is.GreaterThan(before));
  }

  [Test, Category("HappyPath")]
  public void GivenAZip_WhenAnEmptyFolderIsAdded_ThenTheFolderIsKept() {
    var file = Path.Combine(this._dir, "f.txt");
    File.WriteAllText(file, "x");
    var archive = Path.Combine(this._dir, "t.zip");
    ArchiveOperations.Create(archive, [new ArchiveInput(file, "f.txt")], new CompressionOptions());

    ArchiveOperations.Add(archive, [new ArchiveInput("", "folder/")]);

    var entries = ArchiveOperations.List(archive, null);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Name.TrimEnd('/')), Is.EquivalentTo(new[] { "f.txt", "folder" }));
      Assert.That(entries.Single(e => e.Name.StartsWith("folder", StringComparison.Ordinal)).IsDirectory, Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAZip_WhenAFileIsAddedFromDisk_ThenItKeepsItsTime() {
    var file = Path.Combine(this._dir, "f.txt");
    File.WriteAllText(file, "x");
    File.SetLastWriteTimeUtc(file, When);
    using var zip = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8));

    new ZipFormatDescriptor().Add(zip, [ArchiveInputInfo.InMemory("m.txt", "m"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When)), new ArchiveInputInfo(file, "f.txt", false)]);

    var listed = new ZipFormatDescriptor().List(zip, null);
    Assert.Multiple(() => {
      Assert.That(listed.Single(e => e.Name == "m.txt").LastModified, Is.EqualTo(When.ToLocalTime()).Within(TimeSpan.FromSeconds(2)));
      Assert.That(listed.Single(e => e.Name == "f.txt").LastModified, Is.EqualTo(When.ToLocalTime()).Within(TimeSpan.FromSeconds(2)));
    });
  }

  // ── oracle ───────────────────────────────────────────────────────────────────────────────

  [Test, Category("ArchiveExternalInterop")]
  public void GivenOurZip_When7ZipListsIt_ThenTheEntryTimeIsTheSources() {
    FsInteropToolbox.Require7z();
    var path = Path.Combine(this._dir, "ours.zip");
    using (var zip = Create(
             new ArchiveInputInfo("", "sub/", true, Metadata: new ArchiveEntryMetadata(LastWriteTimeUtc: When)),
             ArchiveInputInfo.InMemory("sub/a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When))))
      File.WriteAllBytes(path, zip.ToArray());

    var (stdOut, stdErr, exitCode) = FsInteropToolbox.Run7z($"l -slt \"{path}\"");

    var local = When.ToLocalTime();
    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, stdErr);
      Assert.That(stdOut, Does.Contain($"Modified = {local:yyyy-MM-dd HH:mm:ss}"));
      Assert.That(stdOut, Does.Contain("Folder = +"));
    });
  }
}
