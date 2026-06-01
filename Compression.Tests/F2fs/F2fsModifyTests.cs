using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// F2FS is now read/write (<see cref="IArchiveModifiable"/>) via read-extract-rebuild:
/// add / replace / remove rebuild a fresh fsck.f2fs-clean image with the writer.
/// </summary>
[TestFixture]
public class F2fsModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new F2fsReader(image);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  [Test, Category("RoundTrip")]
  public void Add_NewFile_AppearsAlongsideExisting() {
    using var img = BuildImage(("readme.txt", "hello"u8.ToArray()), ("docs/guide.txt", "in docs"u8.ToArray()));
    ((IArchiveModifiable)new F2fsFormatDescriptor()).Add(img, [ArchiveInputInfo.InMemory("notes.txt", "added"u8.ToArray())]);
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("readme.txt"), Is.True);
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True);
    Assert.That(files["notes.txt"], Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Remove_DropsEntry_KeepsOthers() {
    using var img = BuildImage(("keep.txt", "k"u8.ToArray()), ("drop.txt", "d"u8.ToArray()));
    ((IArchiveModifiable)new F2fsFormatDescriptor()).Remove(img, ["drop.txt"]);
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("drop.txt"), Is.False);
    Assert.That(files["keep.txt"], Is.EqualTo("k"u8.ToArray()));
  }
}
