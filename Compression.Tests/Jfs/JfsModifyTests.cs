using Compression.Registry;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// JFS is now read+write (<see cref="IArchiveModifiable"/>) via read-extract-rebuild:
/// add / replace / remove rebuild a fresh fsck.jfs-clean image with the writer.
/// These tests pin that the mutations round-trip through <see cref="JfsReader"/>.
/// </summary>
[TestFixture]
public class JfsModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new JfsReader(image);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  [Test, Category("RoundTrip")]
  public void Add_NewFile_AppearsAlongsideExisting() {
    using var img = BuildImage(("readme.txt", "hello"u8.ToArray()), ("docs/guide.txt", "in docs"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("notes.txt", "added"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("readme.txt"), Is.True, "existing root file kept");
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "existing nested file kept");
    Assert.That(files["notes.txt"], Is.EqualTo("added"u8.ToArray()), "new file added with intact content");
  }

  [Test, Category("RoundTrip")]
  public void Add_SameName_ReplacesContent() {
    using var img = BuildImage(("data.bin", new byte[] { 1, 2, 3 }));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("data.bin", new byte[] { 9, 9, 9, 9 })]);

    var files = ReadAll(img);
    Assert.That(files["data.bin"], Is.EqualTo(new byte[] { 9, 9, 9, 9 }), "same-name add replaces content");
  }

  [Test, Category("RoundTrip")]
  public void Remove_DropsEntry_KeepsOthers() {
    using var img = BuildImage(("keep.txt", "k"u8.ToArray()), ("drop.txt", "d"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Remove(img, ["drop.txt"]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("drop.txt"), Is.False, "removed file is gone");
    Assert.That(files["keep.txt"], Is.EqualTo("k"u8.ToArray()), "other file kept");
  }
}
