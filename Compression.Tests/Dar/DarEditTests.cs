#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// In-place editing of DAR archives (docs/DAR-ON-DISK.md, section 13) without dar: kept entries
/// carried over byte for byte, settings kept, refusals leave the archive untouched. dar judges the
/// edited archives in <see cref="DarEditOracleTests"/>.
/// </summary>
[TestFixture]
public class DarEditTests {
  private static readonly byte[] Added = "added by an edit\n"u8.ToArray();
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("edit");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private string WriteOurs(DarWriter.Settings settings, string name = "ours") {
    var first = Path.Combine(this._tmp, $"{name}.1.dar");
    using (var fs = File.Create(first))
      DarWriter.Write(n => n == 1 ? fs : File.Create(Path.Combine(this._tmp, $"{name}.{n}.dar")), DarEncryptionTests.Items(), settings);
    return first;
  }

  /// <summary>An unsliced archive, read from memory so that no file stays open.</summary>
  private static DarArchive Open(string path, string? password, out DarSliceSet set) {
    set = DarSliceSet.Open(new MemoryStream(File.ReadAllBytes(path)));
    return DarArchive.Read(set.Archive, password);
  }

  private static Dictionary<string, byte[]> Files(string path, string? password = null) {
    var fs = File.OpenRead(path);
    try {
      using var set = DarSliceSet.Open(fs);
      var archive = DarArchive.Read(set.Archive, password);
      return archive.Entries.Where(e => e.Kind == DarEntryKind.File).ToDictionary(e => e.Path, archive.ReadData);
    } finally {
      fs.Dispose();
    }
  }

  private static void Edit(string path, ArchiveInputInfo[] add, string[] remove, string? password = null) {
    using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
    if (add.Length > 0)
      new DarFormatDescriptor().Add(fs, add, new ArchiveMutationOptions { Password = password });
    if (remove.Length > 0)
      new DarFormatDescriptor().Remove(fs, remove, new ArchiveMutationOptions { Password = password });
  }

  private static ArchiveInputInfo[] Additions() => [
    ArchiveInputInfo.InMemory("hello.txt", Added),
    ArchiveInputInfo.InMemory("new/added.txt", Added),
  ];

  private static void AssertEdited(Dictionary<string, byte[]> files) {
    var expected = DarEncryptionTests.Items().Where(i => i.Path != "text.txt").ToDictionary(i => i.Path, i => {
      using var s = i.Open!();
      using var ms = new MemoryStream();
      s.CopyTo(ms);
      return ms.ToArray();
    });
    expected["hello.txt"] = Added;
    expected["new/added.txt"] = Added;
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (path, data) in expected)
      Assert.That(files[path], Is.EqualTo(data), path);
  }

  [TestCase('n', false)]
  [TestCase('z', true)]
  [TestCase('x', false)]
  [TestCase('q', true)]
  [Category("HappyPath")]
  public void GivenOurArchive_WhenFilesAreAddedReplacedAndRemoved_ThenTheEditLands(char algorithm, bool tapeMarks) {
    var path = this.WriteOurs(new DarWriter.Settings(algorithm, TapeMarks: tapeMarks));
    Edit(path, Additions(), ["text.txt"]);
    AssertEdited(Files(path));
  }

  [Test, Category("HappyPath")]
  public void GivenOurArchive_WhenEdited_ThenKeptEntriesKeepTheirInodeAndStoredBytesAndTheSettingsStay() {
    var path = this.WriteOurs(new DarWriter.Settings('z', TapeMarks: true, BlockSize: 4096));
    var before = File.ReadAllBytes(path);
    Edit(path, Additions(), ["text.txt"]);

    using var oldSet = DarSliceSet.Open(new MemoryStream(before));
    var old = DarArchive.Read(oldSet.Archive);
    var edited = Open(path, null, out var set);
    using (set) {
      Assert.That((edited.Trailer.Algorithm, edited.Trailer.HasTapeMarks, edited.Trailer.BlockSize), Is.EqualTo(('z', true, 4096UL)));
      Assert.That(edited.RootInode, Is.EqualTo(old.RootInode));
      foreach (var e in old.Entries.Where(e => e.Path is not ("hello.txt" or "text.txt"))) {
        var kept = edited.Entries.Single(k => k.Path == e.Path);
        Assert.That(kept.RawInode, Is.EqualTo(e.RawInode), e.Path);
        if (e.Kind != DarEntryKind.File) continue;
        Assert.That((kept.Algorithm, kept.RawDataFlags, kept.StorageSize, kept.Checksum), Is.EqualTo((e.Algorithm, e.RawDataFlags, e.StorageSize, e.Checksum)), e.Path);
        Assert.That(Stored(edited, kept), Is.EqualTo(Stored(old, e)), e.Path);
      }
    }
  }

  private static byte[] Stored(DarArchive archive, DarEntry entry) {
    using var s = archive.OpenStored(entry);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenADirectory_WhenRemoved_ThenEverythingBelowGoesToo() {
    var path = this.WriteOurs(new DarWriter.Settings('n'));
    Edit(path, [], ["dir"]);
    Assert.That(Files(path).Keys, Has.None.StartsWith("dir/"));
    Assert.That(Files(path).Keys, Does.Contain("hello.txt"));
  }

  [Test, Category("EdgeCase")]
  public void GivenEveryEntryRemoved_WhenRead_ThenTheArchiveIsEmptyButValid() {
    var path = this.WriteOurs(new DarWriter.Settings('z'));
    var all = DarEncryptionTests.Items().Select(i => i.Path.Split('/')[0]).Distinct().ToArray();
    Edit(path, [], all);
    var archive = Open(path, null, out var set);
    using (set)
      Assert.That(archive.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void GivenEncryptedArchive_WhenEditedWithItsPassword_ThenItStaysEncryptedTheSameWay() {
    var path = this.WriteOurs(new DarWriter.Settings('z') { Encryption = new DarWriter.Encryption('t', "pw", '5', 700) });
    Edit(path, Additions(), ["text.txt"], "pw");
    var archive = Open(path, "pw", out var set);
    using (set)
      Assert.That((archive.Trailer.Cipher, archive.Trailer.Kdf!.Hash, archive.Trailer.Kdf.Iterations), Is.EqualTo(('t', '5', 700UL)));
    AssertEdited(Files(path, "pw"));
  }

  [TestCase(null)]
  [TestCase("wrong")]
  [Category("Exception")]
  public void GivenEncryptedArchive_WhenEditedWithoutTheRightPassword_ThenRefusedAndUntouched(string? password) {
    var path = this.WriteOurs(new DarWriter.Settings('n') { Encryption = new DarWriter.Encryption('s', "pw") });
    var before = File.ReadAllBytes(path);
    Assert.That(() => Edit(path, Additions(), [], password), Throws.InstanceOf<ArchivePasswordException>());
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
  }

  [Test, Category("HappyPath")]
  public void GivenSlicedArchive_WhenAnEditGrowsAndShrinksIt_ThenSlicesFollowAndKeepTheirSize() {
    var path = this.WriteOurs(new DarWriter.Settings('n', TapeMarks: true, SliceSize: 8192, FirstSliceSize: 12000));
    var slices = Directory.GetFiles(this._tmp, "ours.*.dar").Length;
    var big = new byte[60_000];
    new Random(5).NextBytes(big);
    Edit(path, [ArchiveInputInfo.InMemory("big.bin", big)], []);
    var grown = Directory.GetFiles(this._tmp, "ours.*.dar").Length;
    Assert.That(grown, Is.GreaterThan(slices + 5));
    Assert.That(new FileInfo(path).Length, Is.EqualTo(12000));
    Assert.That(new FileInfo(Path.Combine(this._tmp, "ours.2.dar")).Length, Is.EqualTo(8192));
    Assert.That(Files(path)["big.bin"], Is.EqualTo(big));

    Edit(path, [], ["big.bin", "dir"]);
    var shrunk = Directory.GetFiles(this._tmp, "ours.*.dar").Length;
    Assert.That(shrunk, Is.LessThan(slices));
    Assert.That(File.Exists(Path.Combine(this._tmp, $"ours.{shrunk + 1}.dar")), Is.False);
    Assert.That(Files(path).Keys, Does.Contain("hello.txt"));
  }

  [Test, Category("Exception")]
  public void GivenSlicedArchive_WhenEditedThroughASliceTheEditWouldDelete_ThenRefusedAndUntouched() {
    this.WriteOurs(new DarWriter.Settings('n', SliceSize: 4096));
    var last = Directory.GetFiles(this._tmp, "ours.*.dar").Length;
    var before = Enumerable.Range(1, last).Select(n => File.ReadAllBytes(Path.Combine(this._tmp, $"ours.{n}.dar"))).ToList();
    Assert.That(() => Edit(Path.Combine(this._tmp, $"ours.{last}.dar"), [], ["dir"]), Throws.InstanceOf<NotSupportedException>());
    Assert.That(Enumerable.Range(1, last).Select(n => File.ReadAllBytes(Path.Combine(this._tmp, $"ours.{n}.dar"))), Is.EqualTo(before));
  }

  [Test, Category("Exception")]
  public void GivenArchiveWithADamagedKeptFile_WhenEdited_ThenRefusedAndUntouched() {
    var path = this.WriteOurs(new DarWriter.Settings('n'));
    var bytes = File.ReadAllBytes(path);
    var at = bytes.AsSpan().IndexOf("The quick brown"u8);
    bytes[at + 3] ^= 0x20;
    File.WriteAllBytes(path, bytes);
    Assert.That(() => Edit(path, Additions(), []), Throws.InstanceOf<InvalidDataException>());
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
  }

  [Test, Category("Exception")]
  public void GivenDarArchiveWithHardLinksAndFileSystemAttributes_WhenEdited_ThenRefusedAndUntouched() {
    var path = DarFixtures.Materialise(this._tmp, "tree.1.dar")[0];
    var before = File.ReadAllBytes(path);
    Assert.That(() => Edit(path, Additions(), []), Throws.InstanceOf<NotSupportedException>().With.Message.Contains("cannot be carried over"));
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
  }

  [Test, Category("HappyPath")]
  public void GivenArchiveInAMemoryStream_WhenEdited_ThenTheStreamHoldsTheEditedArchive() {
    using var ms = new MemoryStream();
    DarWriter.Write(ms, DarEncryptionTests.Items(), new DarWriter.Settings('y'));
    new DarFormatDescriptor().Remove(ms, ["text.txt"]);
    ms.Position = 0;
    Assert.That(new DarFormatDescriptor().List(ms, null).Select(e => e.Name), Does.Not.Contain("text.txt").And.Contain("hello.txt"));
  }
}
