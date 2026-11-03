using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Editing a journaling archive appends a version, as <c>zpaq add</c> does: checked on
/// <c>versions.zpaq</c>, which zpaq 7.15 wrote with three versions (ReferenceVectors/README.md).
/// </summary>
[TestFixture]
public sealed class ZpaqAppendTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZpaqVectors." + name)!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> Snapshot(ZpaqReader r, int version)
    => r.GetEntries(version).Where(e => !e.IsDirectory)
      .ToDictionary(e => e.FileName, e => Convert.ToHexStringLower(SHA256.HashData(r.ExtractBytes(e))));

  [Test]
  public void GivenZpaqsArchive_WhenAFileIsAdded_ThenANewVersionIsAppendedAndTheOldOnesStay() {
    var original = Vector("versions.zpaq");
    var archive = new MemoryStream();
    archive.Write(original);
    Dictionary<string, string>[] before;
    using (var r = new ZpaqReader(new MemoryStream(original)))
      before = [.. Enumerable.Range(1, r.VersionCount).Select(v => Snapshot(r, v))];

    var added = "a file added by a fourth version\n"u8.ToArray();
    new ZpaqFormatDescriptor().Add(archive, [ArchiveInputInfo.InMemory("v/new.txt", added)]);

    var bytes = archive.ToArray();
    Assert.That(bytes.AsSpan(0, original.Length).SequenceEqual(original), "earlier versions are untouched bytes");
    using var after = new ZpaqReader(new MemoryStream(bytes));
    Assert.That(after.VersionCount, Is.EqualTo(4));
    for (var v = 1; v <= 3; ++v)
      Assert.That(Snapshot(after, v), Is.EquivalentTo(before[v - 1]), $"version {v}");
    var latest = Snapshot(after, 4);
    Assert.That(latest.Keys, Is.EquivalentTo(before[2].Keys.Append("v/new.txt")));
    Assert.That(latest["v/new.txt"], Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(added))));
  }

  [Test]
  public void GivenZpaqsArchive_WhenAFileIsRemoved_ThenTheNewVersionLacksItAndTheOldOnesStillHaveIt() {
    var original = Vector("versions.zpaq");
    var archive = new MemoryStream();
    archive.Write(original);
    string victim;
    using (var r = new ZpaqReader(new MemoryStream(original)))
      victim = r.Entries.First(e => !e.IsDirectory).FileName;

    new ZpaqFormatDescriptor().Remove(archive, [victim]);

    using var after = new ZpaqReader(new MemoryStream(archive.ToArray()));
    Assert.That(after.VersionCount, Is.EqualTo(4));
    Assert.That(after.Entries.Select(e => e.FileName), Does.Not.Contain(victim));
    Assert.That(after.GetEntries(3).Select(e => e.FileName), Does.Contain(victim));
  }

  [Test]
  public void GivenAFileIdenticalToOneAlreadyStored_WhenAdded_ThenItsDataIsNotStoredAgain() {
    var original = Vector("versions.zpaq");
    var archive = new MemoryStream();
    archive.Write(original);
    byte[] existing;
    using (var r = new ZpaqReader(new MemoryStream(original))) {
      var big = r.Entries.Where(e => !e.IsDirectory).MaxBy(e => e.Size)!;
      existing = r.ExtractBytes(big);
    }
    Assert.That(existing.Length, Is.GreaterThan(2000));

    new ZpaqFormatDescriptor().Add(archive, [ArchiveInputInfo.InMemory("v/copy.bin", existing)]);

    Assert.That(archive.Length - original.Length, Is.LessThan(600), "only the version header and index were added");
    using var after = new ZpaqReader(new MemoryStream(archive.ToArray()));
    Assert.That(after.ExtractBytes(after.Entries.Single(e => e.FileName == "v/copy.bin")), Is.EqualTo(existing));
  }

  [Test]
  public void GivenANameThatIsNotInTheArchive_WhenRemoved_ThenNothingIsAppended() {
    var original = Vector("versions.zpaq");
    var archive = new MemoryStream();
    archive.Write(original);

    new ZpaqFormatDescriptor().Remove(archive, ["no/such/file.txt"]);

    Assert.That(archive.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void GivenAStreamingArchive_WhenAFileIsAdded_ThenItIsRebuiltAsBefore() {
    var archive = new MemoryStream();
    archive.Write(Vector("stream.zpaq"));

    new ZpaqFormatDescriptor().Add(archive, [ArchiveInputInfo.InMemory("extra.txt", "x"u8.ToArray())]);

    using var after = new ZpaqReader(new MemoryStream(archive.ToArray()));
    Assert.That(after.Entries.Select(e => e.FileName), Does.Contain("extra.txt"));
  }
}
