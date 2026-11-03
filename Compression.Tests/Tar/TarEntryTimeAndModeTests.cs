using Compression.Lib;
using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

/// <summary>
/// TAR entries carry the time and mode of what they were made from: new archives used to stamp
/// every entry with the Unix epoch and give folders mode 0644 (so a GNU tar extraction produced
/// folders nobody could enter), listing reported times in no particular zone, and extraction
/// dropped both the time and the executable bits.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser (vendored 950b150e).</remarks>
[TestFixture]
public sealed class TarEntryTimeAndModeTests {
  private static readonly DateTime When = new(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_tartime_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static MemoryStream Create(params ArchiveInputInfo[] inputs) {
    var stream = new MemoryStream();
    new TarFormatDescriptor().Create(stream, inputs, new FormatCreateOptions());
    var expandable = new MemoryStream();
    expandable.Write(stream.ToArray()); // the writer may have closed its output
    expandable.Position = 0;
    return expandable;
  }

  private static TarEntry[] Headers(Stream tar) {
    tar.Position = 0;
    var r = new TarReader(tar, leaveOpen: true);
    var entries = new List<TarEntry>();
    while (r.GetNextEntry() is { } e) {
      entries.Add(e);
      r.Skip();
    }
    return [.. entries];
  }

  [Test, Category("HappyPath")]
  public void GivenAnInputWithATime_WhenArchived_ThenTheEntryCarriesItAndListsItAsUtc() {
    using var tar = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When)));

    tar.Position = 0;
    var listed = new TarFormatDescriptor().List(tar, null).Single();

    Assert.Multiple(() => {
      Assert.That(listed.LastModified, Is.EqualTo(When));
      Assert.That(listed.LastModified!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFileOnDisk_WhenArchived_ThenItsLastWriteTimeIsKept() {
    var file = Path.Combine(this._dir, "f.txt");
    File.WriteAllText(file, "f");
    File.SetLastWriteTimeUtc(file, When);

    using var tar = Create(new ArchiveInputInfo(file, "f.txt", false));

    Assert.That(Headers(tar).Single().ModifiedTime.UtcDateTime, Is.EqualTo(When));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolderAndAFile_WhenArchivedWithoutAMode_ThenTheFolderIs0755AndTheFile0644() {
    using var tar = Create(new ArchiveInputInfo("", "sub/", true), ArchiveInputInfo.InMemory("sub/a.txt", "a"u8));

    var headers = Headers(tar);

    Assert.Multiple(() => {
      Assert.That(headers.Single(h => h.IsDirectory).Mode, Is.EqualTo(0x1ED));
      Assert.That(headers.Single(h => !h.IsDirectory).Mode, Is.EqualTo(0x1A4));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAStatedUnixMode_WhenArchived_ThenTheEntryKeepsIt() {
    using var tar = Create(ArchiveInputInfo.InMemory("run.sh", "#!/bin/sh"u8, new ArchiveEntryMetadata(UnixMode: 0x1ED)));

    Assert.That(Headers(tar).Single().Mode, Is.EqualTo(0x1ED));
  }

  [Test, Category("HappyPath")]
  public void GivenAnEntryWithATime_WhenExtracted_ThenTheFileGetsThatTime() {
    using var tar = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When)));
    var outDir = Path.Combine(this._dir, "x");

    tar.Position = 0;
    new TarFormatDescriptor().Extract(tar, outDir, null, null);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(outDir, "a.txt")), Is.EqualTo(When));
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEntryAtTheEpoch_WhenExtracted_ThenTheFileKeepsItsExtractionTime() {
    using var tar = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8));
    var outDir = Path.Combine(this._dir, "x");
    var before = DateTime.UtcNow.AddMinutes(-1);

    tar.Position = 0;
    new TarFormatDescriptor().Extract(tar, outDir, null, null);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(outDir, "a.txt")), Is.GreaterThan(before));
  }

  [Test, Category("HappyPath")]
  public void GivenAnExecutableEntry_WhenExtractedOnUnix_ThenItStaysExecutable() {
    if (OperatingSystem.IsWindows()) {
      Assert.Ignore("Unix file modes only exist on a Unix host.");
      return;
    }
    using var tar = Create(ArchiveInputInfo.InMemory("run.sh", "#!/bin/sh"u8, new ArchiveEntryMetadata(UnixMode: 0x1ED)));
    var outDir = Path.Combine(this._dir, "x");

    tar.Position = 0;
    new TarFormatDescriptor().Extract(tar, outDir, null, null);

    Assert.That(File.GetUnixFileMode(Path.Combine(outDir, "run.sh")) & UnixFileMode.UserExecute, Is.EqualTo(UnixFileMode.UserExecute));
  }

  [Test, Category("HappyPath")]
  public void GivenATar_WhenAnEmptyFolderIsAdded_ThenItIsAnEntryOfItsOwn() {
    using var tar = Create(ArchiveInputInfo.InMemory("a.txt", "a"u8));

    new TarFormatDescriptor().Add(tar, [new ArchiveInputInfo("", "empty/", true)]);

    Assert.That(Headers(tar).Where(h => h.IsDirectory).Select(h => h.Name), Is.EqualTo(new[] { "empty/" }));
  }

  [Test, Category("HappyPath")]
  public void GivenACompressedTar_WhenAnEntryIsRemovedThroughARebuild_ThenTheOthersKeepTheirTimes() {
    var src = Path.Combine(this._dir, "src");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "nested");
    File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "other");
    foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
      File.SetLastWriteTimeUtc(f, When);
    var archive = Path.Combine(this._dir, "t.tar.gz");
    ArchiveOperations.Create(archive, [
      new ArchiveInput("", "sub/"),
      new ArchiveInput(Path.Combine(src, "sub", "a.txt"), "sub/a.txt"),
      new ArchiveInput(Path.Combine(src, "sub", "b.txt"), "sub/b.txt"),
    ], new CompressionOptions());

    ArchiveOperations.Remove(archive, ["sub/b.txt"]);

    var kept = ArchiveOperations.List(archive, null).Single(e => e.Name.Trim('/') == "sub/a.txt");
    Assert.That(kept.LastModified?.ToUniversalTime(), Is.EqualTo(When));
  }

  // ── oracle ───────────────────────────────────────────────────────────────────────────────

  [Test, Category("ArchiveExternalInterop")]
  public void GivenOurTar_WhenGnuTarListsIt_ThenFoldersAreEnterableAndTimesAreTheSources() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("tar"))
      Assert.Ignore("GNU tar in WSL is needed for this oracle.");
    var path = Path.Combine(this._dir, "ours.tar");
    using (var tar = Create(
             new ArchiveInputInfo("", "sub/", true, Metadata: new ArchiveEntryMetadata(LastWriteTimeUtc: When)),
             ArchiveInputInfo.InMemory("sub/a.txt", "a"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: When))))
      File.WriteAllBytes(path, tar.ToArray());

    var (stdOut, stdErr, exitCode) = FsInteropToolbox.RunWsl($"TZ=UTC tar --utc -tvf {FsInteropToolbox.WinToWsl(path)}");

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, stdErr);
      Assert.That(stdOut, Does.Match(@"drwxr-xr-x .* 2024-01-02 03:04 sub/"));
      Assert.That(stdOut, Does.Match(@"-rw-r--r-- .* 2024-01-02 03:04 sub/a\.txt"));
    });
  }
}
