using System.IO.Compression;
using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Registry;

/// <summary>
/// Renaming entries without anything else about the container changing. Offered only where it is
/// lossless — ZIP through a raw rewrite of its names, filesystem images through their own drivers —
/// and refused, leaving the file untouched, everywhere else.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ArchiveRenameTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-mv-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  // ── resolving renames against the names a container holds ───────────────────────────────────

  private static IReadOnlyDictionary<string, string> Resolve(string[] names, params (string From, string To)[] renames)
    => ArchiveRenames.Resolve([.. renames.Select(r => new ArchiveRename(r.From, r.To))], names);

  [Test]
  public void GivenAFolder_WhenRenamed_ThenEveryNameBeneathItMovesAndASiblingWithACommonPrefixStays() {
    var map = Resolve(["docs/", "docs/a.txt", "docs/deep/b.txt", "docsextra/c.txt"], ("docs", "manual"));

    Assert.That(map, Is.EquivalentTo(new Dictionary<string, string> {
      ["docs/"] = "manual/", ["docs/a.txt"] = "manual/a.txt", ["docs/deep/b.txt"] = "manual/deep/b.txt",
    }));
  }

  [Test]
  public void GivenTwoNamesDifferingOnlyInCase_WhenOneIsRenamed_ThenOnlyThatOneMoves()
    => Assert.That(Resolve(["A.txt", "a.txt"], ("a.txt", "b.txt")), Is.EquivalentTo(new Dictionary<string, string> { ["a.txt"] = "b.txt" }));

  [Test]
  public void GivenAChangeOfCaseOnly_WhenResolved_ThenItIsAllowed()
    => Assert.That(Resolve(["readme.txt"], ("readme.txt", "README.txt")), Is.EquivalentTo(new Dictionary<string, string> { ["readme.txt"] = "README.txt" }));

  [TestCase("b.txt", TestName = "the same name")]
  [TestCase("B.TXT", TestName = "a name differing only in case")]
  public void GivenATakenName_WhenRenamedOnto_ThenItIsRefused(string taken)
    => Assert.Throws<IOException>(() => Resolve(["a.txt", taken], ("a.txt", "b.txt")));

  [Test]
  public void GivenAFolder_WhenMovedIntoItself_ThenItIsRefused()
    => Assert.Throws<IOException>(() => Resolve(["docs/a.txt"], ("docs", "docs/inner")));

  [Test]
  public void GivenANameThatIsNotThere_WhenRenamed_ThenItSaysSo()
    => Assert.Throws<FileNotFoundException>(() => Resolve(["a.txt"], ("missing.txt", "x.txt")));

  [TestCase("", "x.txt")]
  [TestCase("a.txt", "")]
  [TestCase("a.txt", "../outside.txt")]
  [TestCase("a.txt", "docs/../../b.txt")]
  [TestCase("a.txt", "docs//b.txt")]
  public void GivenAPathThatIsEmptyOrLeavesTheTree_WhenUsed_ThenItIsRejected(string from, string to)
    => Assert.Throws<ArgumentException>(() => Resolve(["a.txt"], (from, to)));

  [Test]
  public void GivenAFilesystemNameWithALeadingSlash_WhenRenamed_ThenTheSlashIsKept()
    => Assert.That(Resolve(["/HELLO.TXT"], ("HELLO.TXT", "WORLD.TXT")), Is.EquivalentTo(new Dictionary<string, string> { ["/HELLO.TXT"] = "/WORLD.TXT" }));

  // ── ZIP: nothing but the names changes ───────────────────────────────────────────────────────

  private static readonly DateTimeOffset Stamp = new(2021, 6, 15, 10, 30, 42, TimeSpan.Zero);

  /// <summary>
  /// A ZIP carrying what a rebuild would lose: timestamps, Unix permissions, entry and archive
  /// comments, a pair of names differing only in case, a Unicode name, and — because it is written
  /// to a stream that cannot seek — data descriptors after every entry.
  /// </summary>
  private string RichZip() {
    var path = Path.Combine(this._dir, "rich.zip");
    using var file = File.Create(path);
    using (var forwardOnly = new ForwardOnlyStream(file))
    using (var zip = new ZipArchive(forwardOnly, ZipArchiveMode.Create, leaveOpen: true)) {
      zip.Comment = "archive comment";
      Add(zip, "A.txt", "upper case");
      Add(zip, "a.txt", "lower case");
      Add(zip, "other.txt", "other");
      Add(zip, "docs/guide.txt", "guide");
      Add(zip, "bilder/ärger.txt", "umlaut");
    }

    return path;

    static void Add(ZipArchive zip, string name, string text) {
      var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
      entry.LastWriteTime = Stamp;
      entry.ExternalAttributes = unchecked((int)(0x81ED_0000u)); // -rwxr-xr-x
      entry.Comment = "about " + name;
      using var writer = new StreamWriter(entry.Open());
      writer.Write(text);
    }
  }

  private static List<(string Name, string Text, DateTimeOffset Time, int Attributes, string Comment)> Read(string path, out string comment) {
    using var zip = ZipFile.OpenRead(path);
    comment = zip.Comment;
    return [.. zip.Entries.Select(e => (e.FullName, new StreamReader(e.Open()).ReadToEnd(), e.LastWriteTime, e.ExternalAttributes, e.Comment))];
  }

  [Test]
  public void GivenARichZip_WhenAnEntryIsRenamed_ThenOnlyItsNameChangesAndTheFileGrowsByExactlyTheNameDifference() {
    var zip = this.RichZip();
    var before = Read(zip, out var commentBefore);
    var lengthBefore = new FileInfo(zip).Length;

    ArchiveOperations.Rename(zip, [new ArchiveRename("other.txt", "renamed-longer.txt")]);

    var after = Read(zip, out var commentAfter);
    var expected = before.Select(e => e.Name == "other.txt" ? e with { Name = "renamed-longer.txt" } : e).ToList();
    Assert.Multiple(() => {
      Assert.That(after, Is.EqualTo(expected), "every entry keeps its content, time, permissions and comment");
      Assert.That(commentAfter, Is.EqualTo(commentBefore));
      Assert.That(new FileInfo(zip).Length - lengthBefore, Is.EqualTo(2 * ("renamed-longer.txt".Length - "other.txt".Length)),
        "the name appears once in the local header and once in the directory; nothing else was rewritten");
    });
  }

  [Test]
  public void GivenTwoEntriesDifferingOnlyInCase_WhenAnUnrelatedEntryIsRenamed_ThenBothKeepTheirOwnContent() {
    var zip = this.RichZip();

    ArchiveOperations.Rename(zip, [new ArchiveRename("docs/guide.txt", "docs/manual.txt")]);

    var entries = Read(zip, out _).ToDictionary(e => e.Name, e => e.Text);
    Assert.That((entries["A.txt"], entries["a.txt"]), Is.EqualTo(("upper case", "lower case")));
  }

  [Test]
  public void GivenAZipFolder_WhenRenamedToAUnicodeName_ThenItsEntriesMoveAndReadBack() {
    var zip = this.RichZip();

    ArchiveOperations.Rename(zip, [new ArchiveRename("docs", "dokumente-ü")]);

    var entries = Read(zip, out _).ToDictionary(e => e.Name, e => e.Text);
    Assert.That(entries["dokumente-ü/guide.txt"], Is.EqualTo("guide"));
    Assert.That(entries.Keys, Has.None.StartsWith("docs/"));
  }

  [Test]
  public void GivenARenameThatFails_WhenRefused_ThenTheArchiveIsByteIdenticalAndNoTemporaryFileRemains() {
    var zip = this.RichZip();
    var bytes = File.ReadAllBytes(zip);

    Assert.Throws<IOException>(() => ArchiveOperations.Rename(zip, [new ArchiveRename("a.txt", "OTHER.TXT")]));

    Assert.That(File.ReadAllBytes(zip), Is.EqualTo(bytes));
    Assert.That(Directory.GetFiles(this._dir).Select(Path.GetFileName), Is.EqualTo(new[] { "rich.zip" }));
  }

  [Test]
  public void GivenAZip_WhenAskedWhetherItRenames_ThenItDoes()
    => Assert.That(ArchiveOperations.CanRename(this.RichZip()), Is.True);

  // ── ZIP removal: exact names, nothing else disturbed ────────────────────────────────────────

  [Test]
  public void GivenTwoEntriesDifferingOnlyInCase_WhenOneIsRemoved_ThenTheOtherStaysWithEveryFieldIntact() {
    var zip = this.RichZip();
    var before = Read(zip, out var commentBefore).Where(e => e.Name != "a.txt").ToList();

    ArchiveOperations.Remove(zip, ["a.txt"]);

    Assert.That(Read(zip, out var commentAfter), Is.EqualTo(before),
      "removing one name kept the other name, and every remaining entry's time, permissions and comment");
    Assert.That(commentAfter, Is.EqualTo(commentBefore));
  }

  // ── filesystem images: the driver renames one directory entry ───────────────────────────────

  private string Floppy() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("ONE.TXT", Encoding.ASCII.GetBytes("first"));
    writer.AddFile("TWO.TXT", Encoding.ASCII.GetBytes("second"));
    var path = Path.Combine(this._dir, "floppy.img");
    File.WriteAllBytes(path, writer.Build(volumeLabel: "MYDISK"));
    return path;
  }

  [Test]
  public void GivenAFatFloppy_WhenAFileIsRenamed_ThenTheImageKeepsItsSizeAndLabelAndTheFileItsContent() {
    var floppy = this.Floppy();
    var length = new FileInfo(floppy).Length;
    Assume.That(ArchiveOperations.CanRename(floppy), Is.True, "the FAT driver renames in place");

    ArchiveOperations.Rename(floppy, [new ArchiveRename("ONE.TXT", "UNO.TXT")]);

    var bytes = File.ReadAllBytes(floppy);
    var names = ArchiveOperations.List(floppy, password: null).Select(e => e.Name.TrimStart('/')).ToList();
    Assert.Multiple(() => {
      Assert.That(bytes.LongLength, Is.EqualTo(length), "a floppy stays a floppy");
      Assert.That(Encoding.ASCII.GetString(bytes, 43, 11).TrimEnd(), Is.EqualTo("MYDISK"));
      Assert.That(names, Does.Contain("UNO.TXT").And.Not.Contain("ONE.TXT").And.Contain("TWO.TXT"));
      Assert.That(Encoding.ASCII.GetString(ArchiveOperations.ExtractEntry(floppy, names.Single(n => n == "UNO.TXT"), null)), Is.EqualTo("first"));
    });
  }

  [Test]
  public void GivenAFatFloppyRenamed_WhenCheckedByTheLinuxTools_ThenFsckFindsNothingWrong() {
    if (!FsInteropToolbox.WslAvailable || FsInteropToolbox.RunWsl("command -v fsck.vfat").ExitCode != 0)
      Assert.Ignore("fsck.vfat is not available");

    var floppy = this.Floppy();
    var before = FsInteropToolbox.RunWsl($"fsck.vfat -n {FsInteropToolbox.WinToWsl(floppy)}");
    ArchiveOperations.Rename(floppy, [new ArchiveRename("ONE.TXT", "UNO.TXT")]);

    var fsck = FsInteropToolbox.RunWsl($"fsck.vfat -n {FsInteropToolbox.WinToWsl(floppy)}");
    Assert.That(fsck.ExitCode, Is.Zero, "BEFORE[" + before.ExitCode + "]: " + before.StdOut + " AFTER: " + fsck.StdOut + fsck.StdErr);
  }

  // ── everything else is refused ──────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFormatThatCouldOnlyRenameByRecreatingItself_WhenRenamed_ThenItIsRefusedAndUntouched() {
    var tar = Path.Combine(this._dir, "a.tar");
    File.WriteAllText(Path.Combine(this._dir, "x.txt"), "x");
    ArchiveOperations.Create(tar, ArchiveInput.Resolve([Path.Combine(this._dir, "x.txt")]), new CompressionOptions());
    var bytes = File.ReadAllBytes(tar);

    Assert.That(ArchiveOperations.CanRename(tar), Is.False);
    Assert.Throws<NotSupportedException>(() => ArchiveOperations.Rename(tar, [new ArchiveRename("x.txt", "y.txt")]));
    Assert.That(File.ReadAllBytes(tar), Is.EqualTo(bytes));
  }

  [Test]
  public void GivenAFormatThatIsNotAnArchive_WhenAskedWhetherItRenames_ThenItDoesNot() {
    var gz = Path.Combine(this._dir, "a.txt.gz");
    using (var gzip = new GZipStream(File.Create(gz), CompressionLevel.Optimal)) gzip.Write("single stream"u8);

    Assert.That(ArchiveOperations.CanRename(gz), Is.False);
  }

  /// <summary>Hides seeking, so System.IO.Compression writes data descriptors after each entry.</summary>
  private sealed class ForwardOnlyStream(Stream inner) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
  }
}
