using System.Text;
using Compression.Core.Entropy.Ans;
using Compression.Core.Entropy.Neural;
using Compression.Registry;

namespace Compression.Tests.Neural;

/// <summary>
/// Round-trip, determinism and ratio coverage for the online neural predictor.
/// </summary>
[TestFixture]
public class NnCompressorTests {
  private static string SampleText() =>
    "The quick brown fox jumps over the lazy dog. " +
    "The quick brown fox jumps over the lazy dog. " +
    "Pack my box with five dozen liquor jugs. " +
    "A neural predictor learns the statistics of the data as it compresses, " +
    "then the decoder replays the identical learning to stay in lock-step. " +
    "The quick brown fox jumps over the lazy dog once more.";

  private static void AssertRoundTrips(byte[] data) {
    var compressed = NnCompressor.Compress(data);
    var decompressed = NnCompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_Empty() => AssertRoundTrips([]);

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() => AssertRoundTrips([0xA5]);

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void RoundTrip_TwoBytes() => AssertRoundTrips([0x00, 0xFF]);

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Text() => AssertRoundTrips(Encoding.UTF8.GetBytes(SampleText()));

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Binary() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)((i * 37 + (i >> 3)) ^ 0x5A);
    AssertRoundTrips(data);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_Random() {
    var data = new byte[4096];
    new Random(12345).NextBytes(data);
    AssertRoundTrips(data);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_LongRun() {
    var data = new byte[8192];
    Array.Fill(data, (byte)'Z');
    AssertRoundTrips(data);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    AssertRoundTrips(data);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AlternatingPattern() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xAB : 0xCD);
    AssertRoundTrips(data);
  }

  [Test, Category("HappyPath")]
  public void Deterministic_SameInputSameOutput() {
    var data = Encoding.UTF8.GetBytes(SampleText());
    var a = NnCompressor.Compress(data);
    var b = NnCompressor.Compress(data);
    Assert.That(b, Is.EqualTo(a));
  }

  [Test, Category("HappyPath")]
  public void CompressesTextBelowRawSize() {
    var data = Encoding.UTF8.GetBytes(SampleText());
    var compressed = NnCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath")]
  public void LongRun_HighlyCompressible() {
    var data = new byte[4000];
    Array.Fill(data, (byte)'A');
    var compressed = NnCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 8));
  }

  [Test, Category("HappyPath")]
  public void BeatsOrder0EntropyCoderOnText() {
    // A genuine neural predictor exploits inter-symbol structure that an
    // order-0 entropy coder (rANS over byte frequencies) cannot capture.
    var data = Encoding.UTF8.GetBytes(SampleText());
    var nn = NnCompressor.Compress(data).Length;
    var order0 = new RansBuildingBlock().Compress(data).Length;
    Assert.That(nn, Is.LessThan(order0),
      $"Neural={nn} should beat order-0 rANS={order0} on structured text");
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_RoundTrips() {
    var block = new NnBuildingBlock();
    var data = Encoding.UTF8.GetBytes(SampleText());
    var decompressed = block.Decompress(block.Compress(data));
    Assert.That(decompressed, Is.EqualTo(data));
    Assert.That(block.Family, Is.EqualTo(AlgorithmFamily.ContextMixing));
    Assert.That(block.Id, Is.EqualTo("BB_Neural"));
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_RegisteredWithUniqueId() {
    var ids = BuildingBlockRegistry.All.Select(b => b.Id).ToList();
    Assert.That(ids, Does.Contain("BB_Neural"), "Neural block must be discovered by the source generator");
    var unique = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.That(unique, Has.Count.EqualTo(ids.Count), "All building-block IDs must be unique");
  }
}
