#pragma warning disable CS1591
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

[TestFixture]
public class Nilfs1OptimizerTests {

  [Test]
  public void Find_LargeFiles_PicksLargerBlockSize() {
    var g = Nilfs1Optimizer.Find([1_000_000, 2_000_000, 500_000]);
    Assert.That(g.BlockSize, Is.GreaterThanOrEqualTo(1024));
    Assert.That(g.SegmentSize, Is.EqualTo(g.BlockSize * 8));
  }

  [Test]
  public void Find_TinyFilesAlwaysProduceValidBlockSize() {
    var g = Nilfs1Optimizer.Find([1, 10, 100]);
    Assert.That(g.BlockSize, Is.GreaterThanOrEqualTo(1024));
  }

  [Test]
  public void Find_EmptyFilesetReturnsDefault() {
    var g = Nilfs1Optimizer.Find([]);
    Assert.That(g.BlockSize, Is.AnyOf(1024, 2048, 4096, 8192, 16384, 32768, 65536));
  }

  [Test]
  public void Find_ResultIsValidWriterInput() {
    var g = Nilfs1Optimizer.Find([10_000, 20_000]);
    var w = new Nilfs1Writer();
    w.AddFile("a", new byte[10_000]);
    w.AddFile("b", new byte[20_000]);
    var img = w.Build(g.BlockSize, g.SegmentSize);
    Assert.That(img.Length, Is.GreaterThan(0));
    using var r = new Nilfs1Reader(new MemoryStream(img));
    Assert.That(r.ValidSuperblock, Is.True);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("a"));
    Assert.That(names, Does.Contain("b"));
  }
}
