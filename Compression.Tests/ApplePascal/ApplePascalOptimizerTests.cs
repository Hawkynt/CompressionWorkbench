#pragma warning disable CS1591
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

[TestFixture]
public class ApplePascalOptimizerTests {

  [Test]
  public void Find_BlockSizeAlways512() {
    var g = ApplePascalOptimizer.Find([1000, 2000]);
    Assert.That(g.BlockSize, Is.EqualTo(512),
                "Apple Pascal volumes always use 512-byte blocks (spec-mandated).");
  }

  [Test]
  public void Find_VolumeBlocksAreMultipleOf8() {
    var g = ApplePascalOptimizer.Find([1000, 2000, 3000]);
    Assert.That(g.VolumeBlocks % 8, Is.EqualTo(0),
                "Volume blocks must be a multiple of 8 (Pascal 8-block allocation tile).");
  }

  [Test]
  public void Find_MinimumIs280Blocks() {
    var g = ApplePascalOptimizer.Find([10]);
    Assert.That(g.VolumeBlocks, Is.GreaterThanOrEqualTo(280),
                "Standard SS Apple Pascal floppy = 280 blocks.");
  }

  [Test]
  public void Find_ResultIsValidWriterInput() {
    var g = ApplePascalOptimizer.Find([3000, 5000]);
    var w = new ApplePascalWriter();
    w.AddFile("ALPHA", new byte[3000]);
    w.AddFile("BETA", new byte[5000]);
    var img = w.Build(g.VolumeBlocks);
    Assert.That(img.Length, Is.EqualTo(g.VolumeBlocks * 512));
    using var r = new ApplePascalReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("ALPHA"));
    Assert.That(names, Does.Contain("BETA"));
  }
}
