using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// ZFS is now read/write (<see cref="IArchiveModifiable"/>) via read-extract-rebuild:
/// add / replace / remove rebuild a fresh image (preserving the label footprint)
/// with the fat-ZAP-capable writer.
/// </summary>
[TestFixture]
public class ZfsModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new ZfsReader(image);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  [Test, Category("RoundTrip")]
  public void Add_NewFile_AppearsAlongsideExisting() {
    using var img = BuildImage(("readme.txt", "hello"u8.ToArray()), ("docs/guide.txt", "in docs"u8.ToArray()));
    ((IArchiveModifiable)new ZfsFormatDescriptor()).Add(img, [ArchiveInputInfo.InMemory("notes.txt", "added"u8.ToArray())]);
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("readme.txt"), Is.True);
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True);
    Assert.That(files["notes.txt"], Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Remove_DropsEntry_KeepsOthers() {
    using var img = BuildImage(("keep.txt", "k"u8.ToArray()), ("drop.txt", "d"u8.ToArray()));
    ((IArchiveModifiable)new ZfsFormatDescriptor()).Remove(img, ["drop.txt"]);
    var files = ReadAll(img);
    Assert.That(files.ContainsKey("drop.txt"), Is.False);
    Assert.That(files["keep.txt"], Is.EqualTo("k"u8.ToArray()));
  }
}
