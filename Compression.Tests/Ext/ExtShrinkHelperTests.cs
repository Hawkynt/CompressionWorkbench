#pragma warning disable CS1591
using FileSystem.Ext;

namespace Compression.Tests.Ext;

[TestFixture]
public class ExtShrinkHelperTests {

  [Test]
  public void Shrink_ReducesImageSize() {
    // Build a 4 MB ext image with one tiny file
    var w = new ExtWriter();
    w.AddFile("tiny.txt", "hello"u8.ToArray());
    var image = w.Build(blockSize: 1024, totalBlocks: 4096); // 4 MB

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = ExtShrinkHelper.Shrink(ms);
    Assert.That(result.WasReduced, Is.True);
    Assert.That(result.NewSize, Is.LessThan(result.OriginalSize));
    Assert.That(ms.Length, Is.EqualTo(result.NewSize));

    // Verify the file is still readable
    ms.Position = 0;
    var reader = new ExtReader(ms);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("tiny.txt"));
    var data = reader.Extract(entries[0]);
    Assert.That(System.Text.Encoding.UTF8.GetString(data), Is.EqualTo("hello"));
  }

  [Test]
  public void Shrink_PreservesMultipleFiles() {
    var w = new ExtWriter();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    w.AddFile("b.txt", "beta"u8.ToArray());
    var image = w.Build(blockSize: 1024, totalBlocks: 4096);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = ExtShrinkHelper.Shrink(ms);
    Assert.That(result.WasReduced, Is.True);

    ms.Position = 0;
    var reader = new ExtReader(ms);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(
      e => e.Name, e => System.Text.Encoding.UTF8.GetString(reader.Extract(e)));
    Assert.That(files, Contains.Key("a.txt"));
    Assert.That(files, Contains.Key("b.txt"));
    Assert.That(files["a.txt"], Is.EqualTo("alpha"));
    Assert.That(files["b.txt"], Is.EqualTo("beta"));
  }

  [Test]
  public void Shrink_ReturnsResult() {
    var w = new ExtWriter();
    w.AddFile("test.bin", new byte[100]);
    var image = w.Build(blockSize: 1024, totalBlocks: 4096);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = ExtShrinkHelper.Shrink(ms);
    Assert.That(result.OriginalSize, Is.EqualTo(4096 * 1024));
    Assert.That(result.NewSize, Is.GreaterThan(0));
    Assert.That(result.NewSize, Is.LessThanOrEqualTo(result.OriginalSize));
  }
}
