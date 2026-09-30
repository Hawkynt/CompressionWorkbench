#pragma warning disable CS1591
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// In-place edits anywhere in the tree. Nested paths used to be routed through a
/// rebuild that re-created the volume as a fixed 4 MiB ext2 with a new UUID and no
/// label, journal, owners, modes, times, symlinks or links; these edits must now
/// touch only what they name.
/// </summary>
[TestFixture]
public class ExtInPlaceTreeEditTests {

  private static MemoryStream Image(bool hardLinks = false) {
    var w = new ExtWriter { DeduplicateWithLinks = hardLinks };
    w.AddFile("docs/readme.txt", "readme"u8.ToArray());
    w.AddFile("docs/deep/nested.bin", new byte[5000]);
    w.AddFile("top.txt", "top"u8.ToArray());
    if (hardLinks) {
      w.AddFile("a.txt", "shared data"u8.ToArray());
      w.AddFile("b.txt", "shared data"u8.ToArray());
    }
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
  }

  private static Dictionary<string, byte[]> Files(Stream s) {
    s.Position = 0;
    var r = new ExtReader(s, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => r.Extract(e));
  }

  private static byte[] Uuid(Stream s) {
    var uuid = new byte[16];
    s.Position = 1024 + 104;
    s.ReadExactly(uuid);
    return uuid;
  }

  [Test]
  public void GivenANestedFolder_WhenAFileIsAdded_ThenOnlyThatFileAppearsAndTheVolumeKeepsItsIdentity() {
    using var ms = Image();
    var before = Files(ms);
    var uuid = Uuid(ms);
    var length = ms.Length;

    ExtModifier.AddFile(ms, "docs/added.txt", "added"u8.ToArray());

    var after = Files(ms);
    Assert.That(after["docs/added.txt"], Is.EqualTo("added"u8.ToArray()));
    foreach (var (name, data) in before) Assert.That(after[name], Is.EqualTo(data), name);
    Assert.That(ms.Length, Is.EqualTo(length), "the volume must not be re-created at another size");
    Assert.That(Uuid(ms), Is.EqualTo(uuid), "the volume must keep its UUID");
  }

  [Test]
  public void GivenAMissingFolder_WhenAFileIsAdded_ThenTheFolderIsCreated() {
    using var ms = Image();
    ExtModifier.AddFile(ms, "new/sub/file.txt", "x"u8.ToArray());
    ms.Position = 0;
    var r = new ExtReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Name == "new/sub"), Is.True);
    Assert.That(Files(ms)["new/sub/file.txt"], Is.EqualTo("x"u8.ToArray()));
  }

  [Test]
  public void GivenAnExistingFile_WhenAddedAgain_ThenItsContentIsReplacedInPlace() {
    using var ms = Image();
    ExtModifier.AddFile(ms, "docs/readme.txt", "replaced content"u8.ToArray());
    Assert.That(Files(ms)["docs/readme.txt"], Is.EqualTo("replaced content"u8.ToArray()));
    Assert.That(Files(ms).Count, Is.EqualTo(3));
  }

  [Test]
  public void GivenANestedFile_WhenRemoved_ThenOnlyThatFileDisappears() {
    using var ms = Image();
    var before = Files(ms);
    Assert.That(ExtModifier.RemoveFile(ms, "docs/readme.txt"), Is.True);
    var after = Files(ms);
    Assert.That(after.ContainsKey("docs/readme.txt"), Is.False);
    foreach (var (name, data) in before.Where(kv => kv.Key != "docs/readme.txt"))
      Assert.That(after[name], Is.EqualTo(data), name);
  }

  [Test]
  public void GivenAMissingPath_WhenRemoved_ThenNothingHappens() {
    using var ms = Image();
    var bytes = ms.ToArray();
    Assert.That(ExtModifier.RemoveFile(ms, "docs/absent.txt"), Is.False);
    Assert.That(ExtModifier.RemoveFile(ms, "nowhere/readme.txt"), Is.False);
    Assert.That(ms.ToArray(), Is.EqualTo(bytes));
  }

  [Test]
  public void GivenANonEmptyFolder_WhenRemovedDirectly_ThenItIsRefused() {
    using var ms = Image();
    Assert.Throws<IOException>(() => ExtModifier.RemoveFile(ms, "docs/deep"));
  }

  [Test]
  public void GivenAFolder_WhenRemovedThroughTheDescriptor_ThenEverythingBeneathGoes() {
    using var ms = Image();
    new ExtFormatDescriptor().Remove(ms, ["docs"]);
    ms.Position = 0;
    var r = new ExtReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "top.txt" }));
  }

  [Test]
  public void GivenAMissingName_WhenRemovedThroughTheDescriptor_ThenItSaysSo() {
    using var ms = Image();
    Assert.Throws<FileNotFoundException>(() => new ExtFormatDescriptor().Remove(ms, ["absent.txt"]));
  }

  [Test]
  public void GivenAHardLinkedFile_WhenOneNameIsRemoved_ThenTheOtherKeepsTheData() {
    using var ms = Image(hardLinks: true);
    Assert.That(Files(ms)["b.txt"], Is.EqualTo("shared data"u8.ToArray()), "precondition");

    Assert.That(ExtModifier.RemoveFile(ms, "a.txt"), Is.True);

    Assert.That(Files(ms)["b.txt"], Is.EqualTo("shared data"u8.ToArray()),
      "removing one name freed the inode the other name still uses");
  }
}
