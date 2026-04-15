using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

[TestFixture]
public class EdgeCaseTests {

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  private static IEnumerable<TestCaseData> AllBuildingBlocks() {
    FormatRegistration.EnsureInitialized();
    foreach (var block in BuildingBlockRegistry.All)
      yield return new TestCaseData(block).SetName(block.DisplayName);
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_SingleByte(IBuildingBlock block) {
    var data = new byte[] { 0x42 };
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on single byte input");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_TwoIdenticalBytes(IBuildingBlock block) {
    var data = new byte[] { 0xAA, 0xAA };
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on two identical bytes");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_MaxRepetition_256Bytes(IBuildingBlock block) {
    var data = new byte[256];
    Array.Fill(data, (byte)0xBB);
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on 256 repeated bytes");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_AlternatingPattern_64Bytes(IBuildingBlock block) {
    var data = new byte[64];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on alternating 0x00/0xFF pattern");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_AllByteValues(IBuildingBlock block) {
    // All 256 byte values in sequence
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on all 256 byte values");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_ThreeBytes(IBuildingBlock block) {
    var data = new byte[] { 0x01, 0x02, 0x03 };
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on three bytes");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void RoundTrip_HighEntropySmall(IBuildingBlock block) {
    // 16 bytes of pseudo-random data (high entropy, small size)
    var rng = new Random(999);
    var data = new byte[16];
    rng.NextBytes(data);
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName} failed round-trip on 16-byte high-entropy data");
  }

  [TestCaseSource(nameof(AllBuildingBlocks))]
  public void Compress_ProducesNonEmptyOutput(IBuildingBlock block) {
    var data = new byte[] { 0x42 };
    var compressed = block.Compress(data);
    Assert.That(compressed, Is.Not.Null, $"{block.DisplayName} returned null from Compress");
    Assert.That(compressed.Length, Is.GreaterThan(0),
      $"{block.DisplayName} produced empty compressed output for single byte");
  }
}
