using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Ace;

namespace Compression.Tests.Ace;

/// <summary>
/// Multi-volume ACE sets ACE 2.6 (ACE32.EXE) wrote with <c>-v65</c>; the expected contents in
/// volumes.tsv are what unace 2.5 extracts (ReferenceVectors/mkvolumes.py).
/// </summary>
[TestFixture]
public sealed class AceVolumeTests {
  private sealed record Row(string Set, string Name, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => Encoding.UTF8.GetString(Vector("volumes.tsv"))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(l => l.TrimEnd('\r'))
    .Where(l => !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))
    .ToArray());

  private static readonly Dictionary<string, string[]> Volumes = new() {
    ["vol-store"] = ["vol-store.ace", "vol-store.c00", "vol-store.c01", "vol-store.c02"],
    ["vol-solid"] = ["vol-solid.ace", "vol-solid.c00"],
    ["vol-ace1"] = ["vol-ace1.ace", "vol-ace1.c00"],
    ["vol-enc"] = ["vol-enc.ace", "vol-enc.c00"],
  };

  private string _dir = "";

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-ace-volumes-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AceVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ACE vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private string Place(string set, int count = int.MaxValue) {
    foreach (var name in Volumes[set].Take(count))
      File.WriteAllBytes(Path.Combine(this._dir, name), Vector(name));
    return Path.Combine(this._dir, Volumes[set][0]);
  }

  [TestCase("vol-store", null)]
  [TestCase("vol-solid", null)]
  [TestCase("vol-ace1", null)]
  [TestCase("vol-enc", "Secret")]
  public void GivenTheFirstVolume_WhenExtracted_ThenEveryMemberMatchesUnace(string set, string? password) {
    var path = this.Place(set);
    using var file = File.OpenRead(path);
    using var reader = AceVolumeSet.Open(file, password);

    Assert.That(reader.VolumeCount, Is.EqualTo(Volumes[set].Length));
    Assert.That(reader.Entries.Any(e => e.VolumeCount > 1), Is.True, "no member spans volumes");
    var expected = Manifest.Value.Where(r => r.Set == set).ToArray();
    var files = reader.Entries.Where(e => !e.IsDirectory).ToArray();
    Assert.That(files.Select(e => e.FileName), Is.EquivalentTo(expected.Select(r => r.Name)));
    Assert.Multiple(() => {
      foreach (var row in expected) {
        var data = reader.ExtractEntry(files.Single(e => e.FileName == row.Name));
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), row.Name);
      }
    });
  }

  [Test]
  public void GivenTheDescriptor_WhenItExtractsTheFirstVolume_ThenTheFollowingVolumesAreRead() {
    var path = this.Place("vol-store");
    var output = Path.Combine(this._dir, "out");
    using (var file = File.OpenRead(path))
      new AceFormatDescriptor().Extract(file, output, null, null);
    foreach (var row in Manifest.Value.Where(r => r.Set == "vol-store")) {
      var data = File.ReadAllBytes(Path.Combine(output, row.Name.Replace('/', Path.DirectorySeparatorChar)));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), row.Name);
    }
  }

  [Test]
  public void GivenAMissingLastVolume_WhenListed_ThenTheSplitMemberIsIncompleteAndRefused() {
    var path = this.Place("vol-store", count: 3);
    using var file = File.OpenRead(path);
    using var reader = AceVolumeSet.Open(file, null);

    Assert.That(reader.VolumeCount, Is.EqualTo(3));
    var incomplete = reader.Entries.Where(e => !e.IsComplete).ToArray();
    Assert.That(incomplete, Has.Length.EqualTo(1));
    Assert.That(() => reader.ExtractEntry(incomplete[0]), Throws.TypeOf<InvalidDataException>());
    using var again = File.OpenRead(path);
    var listed = new AceFormatDescriptor().List(again, null);
    Assert.That(listed, Has.Count.EqualTo(reader.Entries.Count));
  }

  [Test]
  public void GivenAMemoryStreamOfAVolume_WhenRead_ThenOnlyItsOwnCompleteMembersAreExtractable() {
    using var reader = new AceReader(new MemoryStream(Vector("vol-store.ace")));
    Assert.That(reader.IsMultiVolume, Is.True);
    Assert.That(reader.VolumeCount, Is.EqualTo(1));
    Assert.That(reader.Entries.All(e => !e.IsComplete || e.VolumeCount == 1), Is.True);
  }

  [Test]
  public void GivenALaterVolume_WhenOpened_ThenTheMemberContinuedFromBeforeIsSkipped() {
    this.Place("vol-store");
    using var file = File.OpenRead(Path.Combine(this._dir, "vol-store.c01"));
    using var reader = AceVolumeSet.Open(file, null);
    Assert.That(reader.FirstVolumeNumber, Is.EqualTo(2));
    Assert.That(reader.VolumeCount, Is.EqualTo(2));
    foreach (var entry in reader.Entries.Where(e => e.IsComplete && !e.IsDirectory)) {
      var row = Manifest.Value.Single(r => r.Set == "vol-store" && r.Name == entry.FileName);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(entry))), Is.EqualTo(row.Sha256));
    }
  }

  [Test]
  public void GivenVolumesOutOfOrder_WhenRead_ThenTheSetIsRejected() {
    var volumes = new List<Stream> { new MemoryStream(Vector("vol-store.ace")), new MemoryStream(Vector("vol-store.c01")) };
    Assert.Throws<InvalidDataException>(() => _ = new AceReader(volumes));
  }

  [Test]
  public void GivenAVolume_WhenAFileIsAdded_ThenTheEditIsRefusedAndTheVolumeIsUntouched() {
    var original = Vector("vol-solid.ace");
    var stream = new MemoryStream();
    stream.Write(original);
    var inputs = new[] { new Compression.Registry.ArchiveInputInfo("x.txt", "x.txt", false, "x"u8.ToArray()) };
    Assert.Throws<NotSupportedException>(() => new AceFormatDescriptor().Add(stream, inputs));
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  [TestCase("a/set.ace", new[] { "a/set.c00", "a/set.C00" })]
  [TestCase("a/SET.ACE", new[] { "a/SET.C00", "a/SET.c00" })]
  [TestCase("a/set.c07", new[] { "a/set.c08", "a/set.C08" })]
  [TestCase("a/set.c99", new[] { "a/set.100" })]
  [TestCase("a/set.100", new[] { "a/set.101" })]
  [TestCase("a/set.999", new string[0])]
  public void GivenAVolumeName_WhenTheNextIsDerived_ThenWinAcesNamingIsFollowed(string path, string[] expected)
    => Assert.That(AceVolumeSet.NextVolumeNames(path), Is.EqualTo(expected));
}
