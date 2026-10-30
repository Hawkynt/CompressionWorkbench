using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// The checked-in 7-Zip archives of SevenZip/ConformanceVectors: every listing and every byte
/// against the manifest 7-Zip's own source tree produced, on any machine.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class SevenZipConformanceVectorTests {
  private const string Prefix = "SevenZipConformanceVectors.";
  private const string Password = "S3cret!";

  private static Stream Resource(string name)
    => Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
       ?? throw new FileNotFoundException($"Embedded 7z vector '{name}' is missing.");

  private static ILookup<string, (string Name, string Kind, long Size, string Sha256)> Manifest() {
    using var reader = new StreamReader(Resource("manifest.tsv"));
    var rows = new List<(string Archive, string Name, string Kind, long Size, string Sha256)>();
    for (string? line; (line = reader.ReadLine()) != null;) {
      var c = line.Split('\t');
      if (c.Length == 5)
        rows.Add((c[0], c[1], c[2], long.Parse(c[3]), c[4]));
    }
    return rows.ToLookup(static r => r.Archive, static r => (r.Name, r.Kind, r.Size, r.Sha256));
  }

  private static IEnumerable<TestCaseData> Archives()
    => Manifest().Select(static g => new TestCaseData(g.Key).SetName($"Given_7ZipVector_{g.Key}_When_Read_Then_ItMatchesTheManifest"));

  [TestCaseSource(nameof(Archives))]
  public void Given_7ZipVector_When_Read_Then_ItMatchesTheManifest(string archive) {
    var expected = Manifest()[archive].ToList();
    using var reader = new SevenZipReader(Resource(archive), password: archive.StartsWith("aes", StringComparison.Ordinal) ? Password : null);

    var listed = reader.Entries.Select(static e => (Name: e.Name.Replace('\\', '/'), Kind: e.IsAnti ? "anti" : e.IsDirectory ? "dir" : "file"))
      .OrderBy(static e => e.Name, StringComparer.Ordinal).ToList();
    Assert.That(listed, Is.EqualTo(expected.Select(static e => (e.Name, e.Kind)).OrderBy(static e => e.Name, StringComparer.Ordinal).ToList()));

    for (var i = 0; i < reader.Entries.Count; ++i) {
      var entry = reader.Entries[i];
      var row = expected.Single(e => e.Name == entry.Name.Replace('\\', '/'));
      if (row.Kind != "file")
        continue;
      var data = reader.Extract(i);
      Assert.That(data.LongLength, Is.EqualTo(row.Size), entry.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), entry.Name);
    }
  }

  [TestCaseSource(nameof(Archives))]
  public void Given_7ZipVector_When_UnusedSpaceIsWiped_Then_EveryEntryStillMatches(string archive) {
    using var copy = new MemoryStream();
    using (var original = Resource(archive))
      original.CopyTo(copy);
    new SevenZipFormatDescriptor().WipeUnusedSpace(copy);

    copy.Position = 0;
    var expected = Manifest()[archive].Where(static e => e.Kind == "file").ToList();
    using var reader = new SevenZipReader(copy, leaveOpen: true, password: archive.StartsWith("aes", StringComparison.Ordinal) ? Password : null);
    foreach (var row in expected) {
      var index = reader.Entries.ToList().FindIndex(e => e.Name.Replace('\\', '/') == row.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(index))), Is.EqualTo(row.Sha256), row.Name);
    }
  }

  [Test]
  public void Given_Unix7ZipVectorWithLinks_When_Listed_Then_LinksAndModesAreReported() {
    using var stream = Resource("unix-links.7z");
    var listed = new SevenZipFormatDescriptor().List(stream, null).ToDictionary(static e => e.Name);
    Assert.That(listed["link.txt"].IsSymlink, Is.True);
    Assert.That(listed["link.txt"].LinkTarget, Is.EqualTo("target.txt"));
    Assert.That(listed["linkdir"].IsSymlink, Is.True);
    Assert.That(listed["linkdir"].LinkTarget, Is.EqualTo("sub"));
    Assert.That(listed["target.txt"].IsSymlink, Is.False);
    Assert.That(listed["target.txt"].LinkTarget, Is.Null);

    stream.Position = 0;
    using var reader = new SevenZipReader(stream, leaveOpen: true);
    var byName = reader.Entries.ToDictionary(static e => e.Name);
    Assert.That(byName["target.txt"].UnixMode, Is.EqualTo(0x81A0)); // -rw-r-----
    Assert.That(byName["sub"].UnixMode, Is.EqualTo(0x41E8));        // drwxr-x---
    Assert.That(byName["link.txt"].UnixMode & 0xF000, Is.EqualTo(0xA000));
  }

  [Test]
  public void Given_7ZipVectorWithTimes_When_Read_Then_CreationAndAccessTimesAreThere() {
    using var reader = new SevenZipReader(Resource("times-all.7z"));
    var file = reader.Entries.First(static e => e.Name == "text.txt");
    Assert.That(file.LastWriteTime, Is.Not.Null);
    Assert.That(file.CreationTime, Is.Not.Null);
    Assert.That(file.LastAccessTime, Is.Not.Null);
  }

  [TestCase("aes.7z")]
  [TestCase("aes-bcj2.7z")]
  [TestCase("aes-mhe.7z")]
  public void Given_7ZipEncryptedVector_When_ReadWithTheWrongPassword_Then_APasswordErrorAndNoBytes(string archive) {
    Assert.That(() => {
      using var reader = new SevenZipReader(Resource(archive), password: "wrong");
      for (var i = 0; i < reader.Entries.Count; ++i)
        _ = reader.Extract(i);
    }, Throws.TypeOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).True);
  }

  [TestCase("aes.7z")]
  [TestCase("aes-mhe.7z")]
  public void Given_7ZipEncryptedVector_When_ReadWithoutPassword_Then_APasswordIsAskedFor(string archive) {
    Assert.That(() => {
      using var reader = new SevenZipReader(Resource(archive));
      for (var i = 0; i < reader.Entries.Count; ++i)
        _ = reader.Extract(i);
    }, Throws.TypeOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).False);
  }

  [Test]
  public void Given_7ZipEncryptedHeaderVector_When_ListedWithoutPassword_Then_NothingIsListedButAPasswordIsAskedFor() {
    using var stream = Resource("aes-mhe.7z");
    Assert.That(() => new SevenZipFormatDescriptor().List(stream, null), Throws.TypeOf<ArchivePasswordException>());
  }

  [Test]
  public void Given_7ZipContentEncryptedVector_When_Listed_Then_EntriesAreMarkedEncrypted() {
    using var stream = Resource("aes.7z");
    var entries = new SevenZipFormatDescriptor().List(stream, null);
    Assert.That(entries.Where(static e => !e.IsDirectory && e.OriginalSize > 0).All(static e => e.IsEncrypted), Is.True);
  }

  [Test]
  public void Given_7ZipAntiItemVector_When_ListedThroughTheDescriptor_Then_AntiItemsAreLeftOut() {
    using var stream = Resource("anti.7z");
    var names = new SevenZipFormatDescriptor().List(stream, null).Select(static e => e.Name).ToList();
    Assert.That(names, Is.EqualTo(new[] { "new.txt" }));
  }

  [Test]
  public void Given_7ZipVectorBehindAStub_When_Read_Then_TheArchiveIsFoundAfterTheStub() {
    using var original = new MemoryStream();
    Resource("bcj2.7z").CopyTo(original);
    var stub = new byte[70_000];
    new Random(5).NextBytes(stub);
    "MZ"u8.CopyTo(stub);
    var sfx = new MemoryStream([.. stub, .. original.ToArray()]);

    using var reader = new SevenZipReader(sfx);
    Assert.That(reader.ArchiveOffset, Is.EqualTo(stub.Length));
    var index = reader.Entries.ToList().FindIndex(static e => e.Name == "code/x86.bin");
    Assert.That(reader.Extract(index), Has.Length.EqualTo(9000));
  }

  [Test]
  public void Given_7ZipVectorCutShort_When_Opened_Then_TheMissingDataIsNamed() {
    using var original = new MemoryStream();
    Resource("solid-2f.7z").CopyTo(original);
    var cut = original.ToArray()[..(int)(original.Length / 2)];

    Assert.That(() => new SevenZipReader(new MemoryStream(cut)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("volume is missing"));
  }
}
