using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Encrypted (<c>-key</c>), multi-part (<c>arc???</c>) and indexed (<c>-index</c>) archives written by
/// zpaq 7.15 (see ReferenceVectors/Sets/README.md), checked against what zpaq extracts from them
/// (<c>manifest.tsv</c>, per version).
/// </summary>
[TestFixture]
public sealed class ZpaqArchiveSetTests {
  private const string Password = "pässwört 1";

  private sealed record Row(string Set, int Version, string Name, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => {
    using var reader = new StreamReader(Open("manifest.tsv"));
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r'))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split('\t'))
      .Select(f => new Row(f[0], int.Parse(f[1]), f[2], long.Parse(f[3]), f[4]))
      .ToArray();
  });

  private static Stream Open(string name) =>
    Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZpaqSetVectors.{name}")
    ?? throw new FileNotFoundException($"Embedded zpaq vector '{name}' is missing from the test assembly.");

  private static byte[] Load(string name) {
    using var s = Open(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-zpaq-set-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private string Place(params string[] names) {
    foreach (var n in names)
      File.WriteAllBytes(Path.Combine(this._dir, n), Load(n));
    return Path.Combine(this._dir, names[0]);
  }

  private static void AssertMatchesManifest(ZpaqReader reader, string set) {
    var versions = Manifest.Value.Where(r => r.Set == set).Select(r => r.Version).Distinct().ToArray();
    Assert.That(reader.VersionCount, Is.EqualTo(versions.Length), "versions");
    Assert.Multiple(() => {
      foreach (var version in versions) {
        var expected = Manifest.Value.Where(r => r.Set == set && r.Version == version).ToArray();
        var entries = reader.GetEntries(version).Where(e => !e.IsDirectory).ToDictionary(e => e.FileName);
        Assert.That(entries.Keys, Is.EquivalentTo(expected.Select(r => r.Name)), $"v{version} names");
        foreach (var row in expected) {
          if (!entries.TryGetValue(row.Name, out var e))
            continue;
          var data = reader.ExtractBytes(e);
          Assert.That(data.LongLength, Is.EqualTo(row.Size), $"v{version} {row.Name} size");
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), $"v{version} {row.Name}");
        }
      }
    });
  }

  // ── encryption ─────────────────────────────────────────────────────────────

  [Test]
  public void GivenEncryptedArchive_WhenOpenedWithItsKey_ThenEveryVersionMatchesZpaq() {
    using var reader = new ZpaqReader(new MemoryStream(Load("enc.zpaq")), Password);

    Assert.That(reader.IsEncrypted, Is.True);
    AssertMatchesManifest(reader, "enc.zpaq");
  }

  [Test]
  public void GivenEncryptedArchive_WhenOpenedWithAWrongKey_ThenThePasswordIsReportedIncorrect() {
    var e = Assert.Throws<InvalidDataException>(() => _ = new ZpaqReader(new MemoryStream(Load("enc.zpaq")), "wrong"));
    Assert.That(e!.Message, Does.Contain("password"));
  }

  [Test]
  public void GivenEncryptedArchive_WhenOpenedWithoutAKey_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => _ = new ZpaqReader(new MemoryStream(Load("enc.zpaq"))));

  [Test]
  public void GivenEncryptedArchive_WhenListedThroughTheDescriptorWithItsKey_ThenTheLatestVersionIsListed() {
    var entries = new ZpaqFormatDescriptor().List(new MemoryStream(Load("enc.zpaq")), Password);

    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(Manifest.Value.Where(r => r.Set == "enc.zpaq" && r.Version == 2).Select(r => r.Name)));
  }

  [Test]
  public void GivenEncryptedArchive_WhenListedWithoutAKey_ThenNothingThrows() {
    var entries = new ZpaqFormatDescriptor().List(new MemoryStream(Load("enc.zpaq")), null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  // ── multi-part ─────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheFirstPartOfAMultiPartArchive_WhenOpened_ThenAllPartsAreRead() {
    var first = this.Place("part001.zpaq", "part002.zpaq", "part003.zpaq");
    using var file = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(file, null);

    Assert.That(reader.IsMultiPart, Is.True);
    AssertMatchesManifest(reader, "part???.zpaq");
  }

  [Test]
  public void GivenAnEncryptedMultiPartArchive_WhenOpenedWithItsKey_ThenTheLaterPartsContinueTheKeyStream() {
    var first = this.Place("ep001.zpaq", "ep002.zpaq");
    using var file = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(file, Password);

    AssertMatchesManifest(reader, "ep???.zpaq");
  }

  [Test]
  public void GivenTheFirstPartWhenALaterOneIsMissing_WhenOpened_ThenTheVersionsBeforeTheGapAreRead() {
    var first = this.Place("part001.zpaq", "part003.zpaq");
    using var file = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(file, null);

    Assert.That(reader.VersionCount, Is.EqualTo(1));
  }

  [Test]
  public void GivenTwoSeparateArchivesNamedLikeParts_WhenTheFirstIsOpened_ThenTheSecondIsNotJoined() {
    var first = this.Place("dec1.zpaq", "dec2.zpaq");
    using var file = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(file, null);

    Assert.That(reader.IsMultiPart, Is.False);
    AssertMatchesManifest(reader, "dec1.zpaq");
  }

  [Test]
  public void GivenAMultiPartArchive_WhenListedThroughTheDescriptor_ThenTheLatestVersionComesFromTheLastPart() {
    var first = this.Place("part001.zpaq", "part002.zpaq", "part003.zpaq");
    using var file = File.OpenRead(first);

    var entries = new ZpaqFormatDescriptor().List(file, null);

    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(Manifest.Value.Where(r => r.Set == "part???.zpaq" && r.Version == 3).Select(r => r.Name)));
  }

  // ── index ──────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnIndexNextToItsParts_WhenOpened_ThenThePartsAreRead() {
    var index = this.Place("ix000.zpaq", "ix001.zpaq", "ix002.zpaq");
    using var file = File.OpenRead(index);
    using var reader = ZpaqArchiveSet.Open(file, null);

    Assert.That(reader.IsIndex, Is.False);
    AssertMatchesManifest(reader, "ix???.zpaq");
  }

  [Test]
  public void GivenAnIndexAlone_WhenOpened_ThenItListsTheFilesButRefusesTheirBytes() {
    using var reader = new ZpaqReader(new MemoryStream(Load("ix000.zpaq")));

    Assert.That(reader.IsIndex, Is.True);
    Assert.That(reader.VersionCount, Is.EqualTo(2));
    var latest = reader.Entries.Where(e => !e.IsDirectory).ToArray();
    Assert.That(latest.Select(e => e.FileName),
      Is.EquivalentTo(Manifest.Value.Where(r => r.Set == "ix???.zpaq" && r.Version == 2).Select(r => r.Name)));
    Assert.Throws<NotSupportedException>(() => reader.ExtractBytes(latest[0]));
  }

  // ── edits ──────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnEncryptedArchive_WhenAFileIsAddedWithoutThePassword_ThenItIsRefusedAndUnchanged() {
    var original = Load("enc.zpaq");
    var archive = new MemoryStream(original.ToArray());

    Assert.Throws<UnauthorizedAccessException>(() => new ZpaqFormatDescriptor().Add(archive,
      [ArchiveInputInfo.InMemory("x.txt", "x"u8.ToArray())]));
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void GivenAnEncryptedArchive_WhenAFileIsAddedWithThePassword_ThenANewVersionIsAppendedEncrypted() {
    var original = Load("enc.zpaq");
    var archive = new MemoryStream();
    archive.Write(original);

    new ZpaqFormatDescriptor().Add(archive, [ArchiveInputInfo.InMemory("v/added.txt", "added"u8.ToArray())],
      new ArchiveMutationOptions { Password = Password });

    var bytes = archive.ToArray();
    Assert.That(bytes.AsSpan(0, original.Length).SequenceEqual(original), "the earlier versions stay as they were");
    Assert.That(bytes.AsSpan(original.Length).IndexOf("added"u8), Is.EqualTo(-1), "the new version is encrypted");
    using var reader = new ZpaqReader(new MemoryStream(bytes), Password);
    Assert.That(reader.VersionCount, Is.EqualTo(3));
    var added = reader.Entries.Single(e => e.FileName == "v/added.txt");
    Assert.That(reader.ExtractBytes(added), Is.EqualTo("added"u8.ToArray()));
    AssertVersionsUnchanged(reader, "enc.zpaq");
  }

  [Test]
  public void GivenTheFirstPartOfAMultiPartArchive_WhenAFileIsRemoved_ThenTheNextPartRecordsIt() {
    var first = this.Place("part001.zpaq", "part002.zpaq");
    var before = File.ReadAllBytes(first);
    using (var file = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Remove(file, ["v/notes.txt"]);
    Assert.That(File.ReadAllBytes(first), Is.EqualTo(before));
    Assert.That(File.Exists(Path.Combine(this._dir, "part003.zpaq")));
    using var stream = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(stream, null);
    Assert.That(reader.VersionCount, Is.EqualTo(3));
    Assert.That(reader.Entries.Any(e => e.FileName == "v/notes.txt"), Is.False);
    AssertVersionsUnchanged(reader, "part???.zpaq", upTo: 2);
  }

  [Test]
  public void GivenEncryptedParts_WhenAFileIsAdded_ThenTheNextPartContinuesTheKeyStream() {
    var first = this.Place("ep001.zpaq", "ep002.zpaq");
    using (var file = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Add(file, [ArchiveInputInfo.InMemory("v/third.txt", "third part"u8.ToArray())],
        new ArchiveMutationOptions { Password = Password });
    var third = File.ReadAllBytes(Path.Combine(this._dir, "ep003.zpaq"));
    Assert.That(third.AsSpan().IndexOf("third part"u8), Is.EqualTo(-1));
    using var stream = File.OpenRead(first);
    using var reader = ZpaqArchiveSet.Open(stream, Password);
    Assert.That(reader.VersionCount, Is.EqualTo(3));
    Assert.That(reader.ExtractBytes(reader.Entries.Single(e => e.FileName == "v/third.txt")), Is.EqualTo("third part"u8.ToArray()));
    AssertVersionsUnchanged(reader, "ep???.zpaq", upTo: 2);
  }

  [Test]
  public void GivenAnIndexFile_WhenAFileIsAdded_ThenTheNextPartIsWrittenBesideItAndTheIndexUpdated() {
    var index = this.Place("ix000.zpaq");
    using (var file = new FileStream(index, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Add(file, [ArchiveInputInfo.InMemory("v/remote.txt", "remote"u8.ToArray())]);
    Assert.That(File.Exists(Path.Combine(this._dir, "ix003.zpaq")));
    using (var stream = File.OpenRead(index)) {
      using var reader = new ZpaqReader(stream);
      Assert.That(reader.IsIndex);
      Assert.That(reader.VersionCount, Is.EqualTo(3));
    }
    // With the parts the index was written for, the whole set reads.
    File.WriteAllBytes(Path.Combine(this._dir, "ix001.zpaq"), Load("ix001.zpaq"));
    File.WriteAllBytes(Path.Combine(this._dir, "ix002.zpaq"), Load("ix002.zpaq"));
    using var set = File.OpenRead(index);
    using var all = ZpaqArchiveSet.Open(set, null);
    Assert.That(all.ExtractBytes(all.Entries.Single(e => e.FileName == "v/remote.txt")), Is.EqualTo("remote"u8.ToArray()));
    AssertVersionsUnchanged(all, "ix???.zpaq", upTo: 2);
  }

  [Test]
  public void GivenTheIndexedParts_WhenTheirIndexIsWritten_ThenItIsTheIndexZpaqWrote() {
    var first = this.Place("ix001.zpaq", "ix002.zpaq");
    using var file = File.OpenRead(first);
    using var index = new MemoryStream();
    ZpaqIndex.Write(file, index);
    Assert.That(index.ToArray(), Is.EqualTo(Load("ix000.zpaq")));
  }

  [Test]
  public void GivenEncryptedParts_WhenTheirIndexIsWritten_ThenItIsAnEncryptedIndex() {
    var first = this.Place("ep001.zpaq", "ep002.zpaq");
    using var file = File.OpenRead(first);
    using var index = new MemoryStream();
    ZpaqIndex.Write(file, index, Password);
    var salt = File.ReadAllBytes(first).AsSpan(0, 32).ToArray();
    salt[0] ^= (byte)('7' ^ 'z');
    Assert.That(index.ToArray().AsSpan(0, 32).SequenceEqual(salt));
    using var reader = new ZpaqReader(new MemoryStream(index.ToArray()), Password);
    Assert.That(reader.IsIndex);
    Assert.That(reader.VersionCount, Is.EqualTo(2));
  }

  [Test]
  public void GivenAPassword_WhenCreated_ThenTheArchiveIsEncryptedAndReadsBack() {
    using var ms = new MemoryStream();
    new ZpaqFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("secret.txt", "top secret text"u8.ToArray())],
      new FormatCreateOptions { Password = Password });
    var bytes = ms.ToArray();
    Assert.That(bytes.AsSpan().IndexOf("top secret"u8), Is.EqualTo(-1));
    Assert.That(bytes.AsSpan(0, 4).SequenceEqual("7kSt"u8), Is.False);
    using var reader = new ZpaqReader(new MemoryStream(bytes), Password);
    Assert.That(reader.ExtractBytes(reader.Entries.Single()), Is.EqualTo("top secret text"u8.ToArray()));
    Assert.Throws<InvalidDataException>(() => _ = new ZpaqReader(new MemoryStream(bytes), "wrong"));
  }

  private static void AssertVersionsUnchanged(ZpaqReader reader, string set, int upTo = int.MaxValue) {
    foreach (var version in Manifest.Value.Where(r => r.Set == set && r.Version <= upTo).Select(r => r.Version).Distinct()) {
      var entries = reader.GetEntries(version).Where(e => !e.IsDirectory).ToDictionary(e => e.FileName);
      foreach (var row in Manifest.Value.Where(r => r.Set == set && r.Version == version))
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractBytes(entries[row.Name]))), Is.EqualTo(row.Sha256), $"v{version} {row.Name}");
    }
  }

  [Test]
  public void GivenAnIndex_WhenAFileIsRemoved_ThenItIsRefusedAndUnchanged() {
    var original = Load("ix000.zpaq");
    var archive = new MemoryStream(original.ToArray());

    Assert.Throws<NotSupportedException>(() => new ZpaqFormatDescriptor().Remove(archive, ["v/notes.txt"]));
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  // ── key derivation ─────────────────────────────────────────────────────────

  [TestCase("", "", 16, 1, 1,
    "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906")]
  [TestCase("password", "NaCl", 1024, 8, 16,
    "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640")]
  public void GivenTheRfc7914Vectors_WhenScryptRuns_ThenTheKeysMatch(string password, string salt, int n, int r, int p, string expected) {
    var key = ZpaqCrypto.Scrypt(System.Text.Encoding.ASCII.GetBytes(password), System.Text.Encoding.ASCII.GetBytes(salt), n, r, p, 64);
    Assert.That(Convert.ToHexStringLower(key), Is.EqualTo(expected));
  }
}
