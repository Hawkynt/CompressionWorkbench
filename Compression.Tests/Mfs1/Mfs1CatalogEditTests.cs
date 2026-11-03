using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// MFS-1 edits change only the catalogue entry and the file's own sectors. Adding a file used
/// to re-pack the image from the listed names, which the writer read as "D.NAME": every other
/// file was cut down to its extension ("A.TXT" became "TXT" in directory "A"), and the title,
/// locks and load and execution addresses were lost.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 9f55d8d, 548e367.</remarks>
[TestFixture]
public sealed class Mfs1CatalogEditTests {
  private static MemoryStream Image() {
    var w = new Mfs1Writer();
    w.AddFile("README", "alpha"u8.ToArray(), '$', locked: true, loadAddress: 0x1900, execAddress: 0x8023);
    w.AddFile("B.DATA", "beta"u8.ToArray());
    var stream = new MemoryStream();
    stream.Write(w.Build("MY DISK"));
    stream.Position = 0;
    return stream;
  }

  private static Mfs1Reader Read(Stream image) {
    image.Position = 0;
    return new Mfs1Reader(image);
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenAFileIsAdded_ThenTheOtherEntriesKeepNamesDirectoriesAndMetadata() {
    using var image = Image();

    new Mfs1FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("c.txt", "gamma"u8)]);

    var r = Read(image);
    var readme = r.Entries.Single(e => e.Name == "README");
    Assert.Multiple(() => {
      Assert.That(r.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { "README", "B.DATA", "C.TXT" }));
      Assert.That(readme.IsLocked, Is.True);
      Assert.That(readme.LoadAddress, Is.EqualTo(0x1900u));
      Assert.That(readme.ExecAddress, Is.EqualTo(0x8023u));
      Assert.That(r.DiskTitle, Is.EqualTo("MY DISK"));
      Assert.That(r.Extract(readme), Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(r.Extract(r.Entries.Single(e => e.FullName == "C.TXT")), Is.EqualTo("gamma"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenAFileIsRemovedByItsListedName_ThenOnlyThatOneGoes() {
    using var image = Image();

    new Mfs1FormatDescriptor().Remove(image, ["B.DATA"]);

    var r = Read(image);
    Assert.Multiple(() => {
      Assert.That(r.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "README" }));
      Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(r.DiskTitle, Is.EqualTo("MY DISK"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnExistingName_WhenAddedAgain_ThenItIsReplaced() {
    using var image = Image();

    new Mfs1FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("B.DATA", "new"u8)]);

    var r = Read(image);
    Assert.Multiple(() => {
      Assert.That(r.Entries.Count(e => e.FullName == "B.DATA"), Is.EqualTo(1));
      Assert.That(r.Extract(r.Entries.Single(e => e.FullName == "B.DATA")), Is.EqualTo("new"u8.ToArray()));
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenANameThatIsNotThere_WhenRemoved_ThenTheImageIsUnchanged() {
    using var image = Image();
    var before = image.ToArray();

    new Mfs1FormatDescriptor().Remove(image, ["DATA"]); // the leaf of B.DATA, in directory '$'

    Assert.That(image.ToArray(), Is.EqualTo(before));
  }
}
