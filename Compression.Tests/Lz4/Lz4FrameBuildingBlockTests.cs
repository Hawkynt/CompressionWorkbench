using Compression.Core.Dictionary.Lz4;

namespace Compression.Tests.Lz4;

[TestFixture]
public class Lz4FrameBuildingBlockTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SmallData() {
    var bb = new Lz4FrameBuildingBlock();
    var data = "Hello, LZ4 Frame Building Block!"u8.ToArray();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Zeroes() {
    var bb = new Lz4FrameBuildingBlock();
    var data = new byte[8192];
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_RandomData() {
    var bb = new Lz4FrameBuildingBlock();
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void CompressedOutput_StartsWithLz4Magic() {
    var bb = new Lz4FrameBuildingBlock();
    var data = "test data"u8.ToArray();
    var compressed = bb.Compress(data);
    // LZ4 frame magic: 0x04224D18 (little-endian)
    Assert.That(compressed[0], Is.EqualTo(0x04));
    Assert.That(compressed[1], Is.EqualTo(0x22));
    Assert.That(compressed[2], Is.EqualTo(0x4D));
    Assert.That(compressed[3], Is.EqualTo(0x18));
  }

  [Test, Category("HappyPath")]
  public void Metadata_IsCorrect() {
    var bb = new Lz4FrameBuildingBlock();
    Assert.That(bb.Id, Is.EqualTo("BB_Lz4Frame"));
    Assert.That(bb.DisplayName, Is.EqualTo("LZ4 Frame"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  [Test, Category("Boundary")]
  public void RoundTrip_EmptyData() {
    var bb = new Lz4FrameBuildingBlock();
    var data = Array.Empty<byte>();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeData_MultiBlock() {
    var bb = new Lz4FrameBuildingBlock();
    // 5 MB - exceeds the 4 MB block max, forcing multi-block
    var data = new byte[5 * 1024 * 1024];
    var rng = new Random(99);
    rng.NextBytes(data);
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}
