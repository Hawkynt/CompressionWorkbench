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

  // ── edits are refused ──────────────────────────────────────────────────────

  [Test]
  public void GivenAnEncryptedArchive_WhenAFileIsAdded_ThenItIsRefusedAndUnchanged() {
    var original = Load("enc.zpaq");
    var archive = new MemoryStream(original.ToArray());

    Assert.Throws<NotSupportedException>(() => new ZpaqFormatDescriptor().Add(archive,
      [ArchiveInputInfo.InMemory("x.txt", "x"u8.ToArray())]));
    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void GivenTheFirstPartOfAMultiPartArchive_WhenAFileIsRemoved_ThenItIsRefusedAndUnchanged() {
    var first = this.Place("part001.zpaq", "part002.zpaq");
    var before = File.ReadAllBytes(first);
    using (var file = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      Assert.Throws<NotSupportedException>(() => new ZpaqFormatDescriptor().Remove(file, ["v/notes.txt"]));
    Assert.That(File.ReadAllBytes(first), Is.EqualTo(before));
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
