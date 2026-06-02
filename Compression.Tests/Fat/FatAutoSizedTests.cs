using System.Text;

namespace Compression.Tests.Fat;

/// <summary>
/// FatWriter.BuildAutoSized must actually fit. Before the fix it had a hard
/// 1.44 MB floor which made "convert to FAT" always produce a 1.44 MB image
/// regardless of how little data the user wrote — defeating auto-size for the
/// Convert Archive flow.
/// </summary>
[TestFixture]
public class FatAutoSizedTests {

  [Test, Category("Regression")]
  public void TinyFileSet_ProducesSubMegabyteImage() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("hello.txt", Encoding.ASCII.GetBytes("hi"));
    w.AddFile("notes.md", Encoding.ASCII.GetBytes("just a few bytes"));
    var disk = w.BuildAutoSized();
    Assert.That(disk.Length, Is.LessThan(512 * 1024),
      $"3-byte+16-byte payload should NOT round up to 1.44 MB. Got {disk.Length} bytes.");
  }

  [Test, Category("Regression")]
  public void EmptyImage_StaysSmall() {
    var w = new FileSystem.Fat.FatWriter();
    var disk = w.BuildAutoSized();
    Assert.That(disk.Length, Is.LessThan(256 * 1024),
      $"No files should produce a tiny image. Got {disk.Length} bytes.");
  }

  [Test, Category("RoundTrip")]
  public void SmallImage_FilesStillRoundTrip() {
    var payload = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("test.txt", payload);
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var entry = r.Entries.First(e => !e.IsDirectory);
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }
}
