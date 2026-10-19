using System.Buffers.Binary;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Directory hard links — Time Machine's snapshots — presented as POSIX shows them: each link is
/// a directory holding its inode's content, wherever the link lives, with the private folder that
/// keeps the inodes hidden once every inode in it is reached through a link. The volumes come from
/// <see cref="HfsPlusDirectoryLinkVolumes"/>; <c>fsck.hfsplus</c> (WSL), which walks every link
/// chain, accepts them, and HFSExplorer's <c>unhfs</c> — an independent reader that resolves
/// directory hard links — extracts the same tree.
/// </summary>
[TestFixture]
public sealed class HfsPlusDirectoryHardLinkTests {

  private const string FirstUsers = "Backups/2026-01-01/Users";
  private const string SecondUsers = "Backups/2026-01-02/Users";
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsdirlink_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static HfsPlusReader Read(byte[] image) => new(new MemoryStream(image));

  // ── Listing and extraction ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenTwoSnapshotsLinkingOneDirectory_WhenListed_ThenBothShowItsWholeTree() {
    var reader = Read(HfsPlusDirectoryLinkVolumes.Snapshots(out var inode));
    var byPath = reader.Entries.ToDictionary(static e => e.FullPath, StringComparer.Ordinal);
    Assert.Multiple(() => {
      foreach (var snapshot in new[] { FirstUsers, SecondUsers }) {
        Assert.That(byPath[snapshot].IsDirectory, Is.True, snapshot);
        Assert.That(byPath[snapshot].Cnid, Is.EqualTo(inode), "the link is its inode");
        Assert.That(byPath[snapshot + "/Pictures"].IsDirectory, Is.True);
        Assert.That(reader.Extract(byPath[snapshot + "/readme.txt"]), Is.EqualTo(HfsPlusDirectoryLinkVolumes.Readme));
        Assert.That(reader.Extract(byPath[snapshot + "/Pictures/photo.raw"]), Is.EqualTo(HfsPlusDirectoryLinkVolumes.Photo));
      }
      Assert.That(reader.Extract(byPath["plain.txt"]), Is.EqualTo("plain"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenEveryInodeLinked_WhenListed_ThenThePrivateFolderIsHidden() {
    var paths = Read(HfsPlusDirectoryLinkVolumes.Snapshots(out _)).Entries.Select(static e => e.FullPath).ToList();
    Assert.Multiple(() => {
      Assert.That(paths.Any(static p => p.Contains("Private Directory", StringComparison.Ordinal)), Is.False);
      Assert.That(paths.Any(static p => p.Contains("dir_", StringComparison.Ordinal)), Is.False);
      Assert.That(paths, Is.EquivalentTo(new[] {
        "Backups", "Backups/2026-01-01", "Backups/2026-01-02", "plain.txt",
        FirstUsers, FirstUsers + "/readme.txt", FirstUsers + "/Pictures", FirstUsers + "/Pictures/photo.raw",
        SecondUsers, SecondUsers + "/readme.txt", SecondUsers + "/Pictures", SecondUsers + "/Pictures/photo.raw",
      }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenASnapshotVolume_WhenTheWholeVolumeIsMappedOrChecked_ThenTheInodeFilesAreStillStoredOnce() {
    var reader = Read(HfsPlusDirectoryLinkVolumes.Snapshots(out _));
    Assert.That(reader.AllFiles.Count(static e => e.Name == "photo.raw"), Is.EqualTo(1), "stored once, shown twice");
  }

  [Test, Category("HappyPath")]
  public void GivenASnapshotVolume_WhenExtractedThroughTheDescriptor_ThenEverySnapshotIsWritten() {
    var output = Path.Combine(this._tmp, "x");
    new HfsPlusFormatDescriptor().Extract(new MemoryStream(HfsPlusDirectoryLinkVolumes.Snapshots(out _)), output, null, null);
    Assert.Multiple(() => {
      foreach (var snapshot in new[] { FirstUsers, SecondUsers }) {
        var root = Path.Combine([output, .. snapshot.Split('/')]);
        Assert.That(File.ReadAllBytes(Path.Combine(root, "readme.txt")), Is.EqualTo(HfsPlusDirectoryLinkVolumes.Readme));
        Assert.That(File.ReadAllBytes(Path.Combine(root, "Pictures", "photo.raw")), Is.EqualTo(HfsPlusDirectoryLinkVolumes.Photo));
      }
      Assert.That(Directory.GetDirectories(output).Select(Path.GetFileName), Is.EquivalentTo(new[] { "Backups" }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenALinkedFile_WhenOpenedByItsSecondPath_ThenTheStreamIsItsBytes() {
    var bytes = new HfsPlusFormatDescriptor().ExtractEntryToMemory(
      new MemoryStream(HfsPlusDirectoryLinkVolumes.Snapshots(out _)), SecondUsers + "/Pictures/photo.raw", null);
    Assert.That(bytes, Is.EqualTo(HfsPlusDirectoryLinkVolumes.Photo));
  }

  [Test, Category("HappyPath")]
  public void GivenAHfsxVolume_WhenItsSnapshotsAreListed_ThenTheyResolveToo() {
    var reader = Read(HfsPlusDirectoryLinkVolumes.Snapshots(out _, caseSensitive: true));
    Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == SecondUsers + "/readme.txt")),
      Is.EqualTo(HfsPlusDirectoryLinkVolumes.Readme));
  }

  // ── Nesting and cycles ─────────────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void GivenALinkInsideALinkedDirectory_WhenListed_ThenItResolvesAtEveryLevel() {
    var w = new HfsPlusWriter();
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/outer/library", []);
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/inner/book.txt", "book"u8.ToArray());
    w.AddFile("a/outer-link", []);
    w.AddFile("b/outer-link", []);
    var image = w.Build(4096);
    HfsPlusDirectoryLinkVolumes.Link(image, "inner", HfsPlusDirectoryLinkVolumes.PrivateFolder + "/outer/library");
    HfsPlusDirectoryLinkVolumes.Link(image, "outer", "a/outer-link", "b/outer-link");
    var reader = Read(image);
    Assert.Multiple(() => {
      foreach (var path in new[] { "a/outer-link/library/book.txt", "b/outer-link/library/book.txt" })
        Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == path)), Is.EqualTo("book"u8.ToArray()), path);
      Assert.That(reader.Entries.Any(static e => e.FullPath.Contains("Private", StringComparison.Ordinal)), Is.False);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenALinkBackToItsOwnDirectory_WhenListed_ThenTheCycleIsShownOnceAndTheListingEnds() {
    var w = new HfsPlusWriter();
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/loop/self", []);
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/loop/data.txt", "x"u8.ToArray());
    w.AddFile("entry", []);
    var image = w.Build(4096);
    HfsPlusDirectoryLinkVolumes.Link(image, "loop", "entry", HfsPlusDirectoryLinkVolumes.PrivateFolder + "/loop/self");
    var paths = Read(image).Entries.Select(static e => e.FullPath).ToList();
    Assert.That(paths, Is.EquivalentTo(new[] { "entry", "entry/data.txt", "entry/self" }));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenLinksThatFanOutEveryLevel_WhenListed_ThenTheListingIsBoundedAndNothingThrows() {
    // Twenty levels, each holding two links to the next: 2^20 paths to the bottom.
    var w = new HfsPlusWriter();
    const int Levels = 20;
    for (var level = 0; level < Levels; level++) {
      w.AddFile($"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/level{level:D2}/left", []);
      w.AddFile($"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/level{level:D2}/right", []);
    }
    w.AddFile($"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/level{Levels:D2}/leaf.txt", "leaf"u8.ToArray());
    w.AddFile("top", []);
    var image = w.Build(4096);
    for (var level = Levels; level > 0; level--)
      HfsPlusDirectoryLinkVolumes.Link(image, $"level{level:D2}",
        $"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/level{level - 1:D2}/left", $"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/level{level - 1:D2}/right");
    HfsPlusDirectoryLinkVolumes.Link(image, "level00", "top");
    var entries = new HfsPlusReader(new MemoryStream(image), leaveOpen: false, maxLinkedEntries: 5000).Entries;
    Assert.Multiple(() => {
      Assert.That(entries.Count, Is.InRange(5000, 5000 + 2 * Levels + 2), "the budget plus what each level adds before it stops");
      Assert.That(entries.Select(static e => e.FullPath), Does.Contain("top/left/left/left"), "listed depth-first up to the budget");
    });
  }

  // ── What stays as stored ───────────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void GivenAnInodeNoLinkReaches_WhenListed_ThenThePrivateFolderStaysVisible() {
    var w = new HfsPlusWriter();
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/linked/a.txt", [4]);
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/orphan/kept.txt", [5]);
    w.AddFile("link", []);
    var image = w.Build(4096);
    var inode = HfsPlusDirectoryLinkVolumes.Link(image, "linked", "link");
    var paths = Read(image).Entries.Select(static e => e.FullPath).ToList();
    Assert.Multiple(() => {
      Assert.That(paths, Does.Contain(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/orphan/kept.txt"), "the orphan stays reachable");
      Assert.That(paths, Does.Contain($"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/dir_{inode}/a.txt"), "as stored, the folder being visible");
      Assert.That(paths, Does.Contain("link/a.txt"), "and through its link");
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenALinkWhoseInodeIsMissing_WhenListed_ThenItStaysAnEmptyFile() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out var inode);
    var link = HfsPlusRemoveExtentsTests.FileRecordOffset(image, "Users");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(link + 44), inode + 1000);
    var entries = Read(image).Entries;
    Assert.Multiple(() => {
      var broken = entries.Where(static e => e.Name == "Users").ToList();
      Assert.That(broken.Count(static e => !e.IsDirectory && e.Size == 0), Is.EqualTo(1), "the broken link is shown as stored");
      Assert.That(broken.Count(static e => e.IsDirectory), Is.EqualTo(1), "the other still resolves");
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnAliasFileWithoutTheLinkChainFlag_WhenListed_ThenItIsAPlainFile() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out _);
    var link = HfsPlusRemoveExtentsTests.FileRecordOffset(image, "Users");
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(link + 2), 0x0002);
    Assert.That(Read(image).Entries.Count(static e => e.Name == "Users" && !e.IsDirectory), Is.EqualTo(1));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenADirectoryHardLink_WhenRemovedInPlace_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out _);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.RemoveFile(ms, FirstUsers));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [TestCase(true, TestName = "GivenAFileSeenThroughADirectoryHardLink_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged")]
  [TestCase(false, TestName = "GivenADirectoryHardLink_WhenAFileIsAddedThroughIt_ThenItIsRefusedAndTheVolumeIsUnchanged")]
  [Category("ExceptionalCase")]
  public void EditThroughLink(bool remove) {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out _);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => {
      if (remove) HfsPlusModifier.RemoveFile(ms, FirstUsers + "/readme.txt");
      else HfsPlusModifier.AddFile(ms, FirstUsers + "/new.txt", [1]);
    });
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFileInsideTheDirectoryInode_WhenRemovedThroughThePrivatePath_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out var inode);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() =>
      HfsPlusModifier.RemoveFile(ms, $"{HfsPlusDirectoryLinkVolumes.PrivateFolder}/dir_{inode}/readme.txt"));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  // ── Oracles ────────────────────────────────────────────────────────

  [Test, Category("ExternalFsInterop")]
  public void GivenASnapshotVolume_WhenCheckedByFsckHfsplus_ThenEveryLinkChainIsAccepted()
    => HfsPlusOracles.AssertFsckClean(HfsPlusDirectoryLinkVolumes.Snapshots(out _), this._tmp, "directory hard links");

  /// <summary>
  /// The control for the test above: the same volume with the inode counting a link it does
  /// not have is reported, so fsck.hfsplus does walk these chains rather than skip them.
  /// </summary>
  [Test, Category("ExternalFsInterop")]
  public void GivenAnInodeCountingALinkItDoesNotHave_WhenCheckedByFsckHfsplus_ThenTheChainIsReported() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out var inode);
    var name = System.Text.Encoding.BigEndianUnicode.GetBytes($"dir_{inode}");
    var at = image.AsSpan().IndexOf(name);
    Assert.That(at, Is.GreaterThan(0), "the inode's catalog key");
    var record = at + name.Length;   // the key ends with the name; the folder record follows, aligned
    record += record & 1;
    Assert.That(BinaryPrimitives.ReadInt16BigEndian(image.AsSpan(record)), Is.EqualTo(1), "a folder record");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(record + 44), 3);
    var (output, _) = HfsPlusOracles.Fsck(image, this._tmp);
    Assert.That(output, Does.Not.Contain("appears to be OK"), output);
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenANestedLinkVolume_WhenCheckedByFsckHfsplus_ThenItIsAccepted() {
    var w = new HfsPlusWriter();
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/outer/library", []);
    w.AddFile(HfsPlusDirectoryLinkVolumes.PrivateFolder + "/inner/book.txt", "book"u8.ToArray());
    w.AddFile("a/outer-link", []);
    w.AddFile("b/outer-link", []);
    var image = w.Build(4096);
    HfsPlusDirectoryLinkVolumes.Link(image, "inner", HfsPlusDirectoryLinkVolumes.PrivateFolder + "/outer/library");
    HfsPlusDirectoryLinkVolumes.Link(image, "outer", "a/outer-link", "b/outer-link");
    HfsPlusOracles.AssertFsckClean(image, this._tmp, "nested directory hard links");
  }

  [Test, Category("ArchiveExternal")]
  public void GivenASnapshotVolume_WhenUnhfsExtractsIt_ThenItsTreeIsOurs() {
    var image = HfsPlusDirectoryLinkVolumes.Snapshots(out _);
    var theirs = HfsPlusOracles.Unhfs(image, this._tmp);
    var reader = Read(image);
    var ours = reader.Entries.Where(static e => !e.IsDirectory).ToDictionary(static e => e.FullPath, e => reader.Extract(e), StringComparer.Ordinal);
    Assert.That(theirs.Keys, Is.EquivalentTo(ours.Keys), "files");
    Assert.Multiple(() => {
      foreach (var (path, bytes) in ours) Assert.That(theirs[path], Is.EqualTo(bytes), path);
    });
  }
}
