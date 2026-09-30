#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// An edit the in-place 7z adder/remover cannot take (a same-name update, part of a
/// solid folder, an encoded header — which every archive 7-Zip itself writes has)
/// rewrites the archive. That rewrite used to go through a temporary folder and
/// came back with no modification times, no attributes (so no Unix modes and no
/// symlinks) and no directory entries; it must keep them.
/// </summary>
[TestFixture]
public class SevenZipMetadataPreservingEditTests {

  private static readonly DateTime Stamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

  private static MemoryStream Archive() {
    var ms = new MemoryStream();
    var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true);
    w.AddDirectory(new SevenZipEntry { Name = "empty", LastWriteTime = Stamp, Attributes = 0x10 });
    w.AddEntry(new SevenZipEntry { Name = "docs/readme.txt", LastWriteTime = Stamp, Attributes = 0x20 | (0x81A4u << 16) | 0x8000 },
      "readme"u8.ToArray());
    w.AddEntry(new SevenZipEntry { Name = "top.txt", LastWriteTime = Stamp, Attributes = 0x21 }, "top"u8.ToArray());
    w.Finish();
    return ms;
  }

  private static Dictionary<string, SevenZipEntry> Entries(Stream s) {
    s.Position = 0;
    return new SevenZipReader(s).Entries.ToDictionary(e => e.Name);
  }

  [Test]
  public void GivenASameNameUpdate_WhenAdded_ThenEveryOtherEntryKeepsItsTimesAttributesAndFolders() {
    using var ms = Archive();
    new SevenZipFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("top.txt", "replaced"u8.ToArray())]);

    var e = Entries(ms);
    Assert.That(e.ContainsKey("empty") && e["empty"].IsDirectory, Is.True, "the empty folder was dropped");
    Assert.That(e["empty"].LastWriteTime, Is.EqualTo(Stamp));
    Assert.That(e["docs/readme.txt"].LastWriteTime, Is.EqualTo(Stamp));
    Assert.That(e["docs/readme.txt"].Attributes, Is.EqualTo(0x20 | (0x81A4u << 16) | 0x8000));
    ms.Position = 0;
    var r = new SevenZipReader(ms);
    Assert.That(r.Extract(r.Entries.ToList().FindIndex(x => x.Name == "top.txt")), Is.EqualTo("replaced"u8.ToArray()));
  }

  [Test]
  public void GivenPartOfASolidFolder_WhenRemoved_ThenOnlyThatNameGoesAndTheRestKeepItsMetadata() {
    using var ms = Archive();
    new SevenZipFormatDescriptor().Remove(ms, ["top.txt"]);

    var e = Entries(ms);
    Assert.That(e.Keys, Is.EquivalentTo(new[] { "empty", "docs/readme.txt" }));
    Assert.That(e["docs/readme.txt"].LastWriteTime, Is.EqualTo(Stamp));
    Assert.That(e["docs/readme.txt"].Attributes, Is.EqualTo(0x20 | (0x81A4u << 16) | 0x8000));
  }

  [Test]
  public void GivenALeafNameThatOccursInAFolder_WhenTheRootNameIsRemoved_ThenTheFolderCopyStays() {
    var ms = new MemoryStream();
    var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true);
    w.AddEntry(new SevenZipEntry { Name = "readme.txt" }, "root"u8.ToArray());
    w.AddEntry(new SevenZipEntry { Name = "docs/readme.txt" }, "nested"u8.ToArray());
    w.Finish();

    new SevenZipFormatDescriptor().Remove(ms, ["readme.txt"]);

    Assert.That(Entries(ms).Keys, Is.EquivalentTo(new[] { "docs/readme.txt" }));
  }
}
