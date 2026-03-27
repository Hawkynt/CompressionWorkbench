using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

[TestFixture]
public class BuildingBlockTests {

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void AllBuildingBlocksAreRegistered() {
    var blocks = BuildingBlockRegistry.All;
    Assert.That(blocks, Has.Count.GreaterThanOrEqualTo(11),
      "Expected at least 11 building blocks (LZ77, LZ78, LZW, LZO, Deflate, Deflate64, Huffman, BWT, MTF, RLE, Delta)");
  }

  private static IEnumerable<TestCaseData> AllBuildingBlocks() {
    FormatRegistration.EnsureInitialized();
    foreach (var block in BuildingBlockRegistry.All)
      yield return new TestCaseData(block).SetName(block.DisplayName);
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_SmallData(IBuildingBlock block) {
    var data = "Hello, World! This is a test of building block round-trip."u8.ToArray();
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_Zeroes(IBuildingBlock block) {
    var data = new byte[4096];
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_Incrementing(IBuildingBlock block) {
    var data = new byte[4096];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i & 0xFF);
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_Random(IBuildingBlock block) {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_Empty(IBuildingBlock block) {
    var data = Array.Empty<byte>();
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  [CancelAfter(30000)]
  public void RoundTrip_Large_Text(IBuildingBlock block) {
    var lorem = "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet, consectetur adipiscing elit. "u8;
    var data = new byte[256 * 1024];
    for (var i = 0; i < data.Length; i++)
      data[i] = lorem[i % lorem.Length];
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  [CancelAfter(30000)]
  public void RoundTrip_Large_Zeroes(IBuildingBlock block) {
    var data = new byte[256 * 1024];
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void LZW_RoundTrip_Zeroes_VariousSizes() {
    var lzw = BuildingBlockRegistry.GetById("BB_Lzw")!;
    foreach (var size in new[] { 32768, 33024, 36864, 65536, 262144 }) {
      var data = new byte[size];
      var compressed = lzw.Compress(data);
      var decompressed = lzw.Decompress(compressed);
      Assert.That(decompressed, Is.EqualTo(data), $"LZW failed at size {size}");
    }
  }
}
