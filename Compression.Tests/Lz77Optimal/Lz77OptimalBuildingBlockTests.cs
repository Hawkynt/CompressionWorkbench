using System.Text;
using Compression.Core.Dictionary.Lz77;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Lz77Optimal;

/// <summary>
/// End-to-end tests for the <see cref="Lz77OptimalBuildingBlock"/>: lossless round-trip across
/// equivalence classes, determinism, registry registration, and the optimal parse beating the
/// greedy LZ77 parse on text.
/// </summary>
[TestFixture]
public class Lz77OptimalBuildingBlockTests {

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  private static readonly Lz77OptimalBuildingBlock _block = new();

  private static void AssertRoundTrip(byte[] data) {
    var compressed = _block.Compress(data);
    var restored = _block.Decompress(compressed);
    Assert.That(restored, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Empty() => AssertRoundTrip([]);

  [Test]
  public void RoundTrip_SingleByte() => AssertRoundTrip([0x42]);

  [Test]
  public void RoundTrip_Text() =>
    AssertRoundTrip(Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. The quick brown fox."));

  [Test]
  public void RoundTrip_Binary() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)((i * 37 + 11) & 0xFF);
    AssertRoundTrip(data);
  }

  [Test]
  public void RoundTrip_Random() {
    var data = new byte[8192];
    new Random(1234).NextBytes(data);
    AssertRoundTrip(data);
  }

  [Test]
  public void RoundTrip_LongRun() {
    var data = new byte[16384];
    Array.Fill(data, (byte)0xAB);
    AssertRoundTrip(data);
  }

  [Test]
  public void RoundTrip_All256Bytes() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    AssertRoundTrip(data);
  }

  [Test]
  public void RoundTrip_RepeatingText_Large() =>
    AssertRoundTrip(Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet, consectetur. ", 4000))));

  [Test]
  public void Compress_IsDeterministic() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("repeatable-stream ", 500)));
    Assert.That(_block.Compress(data), Is.EqualTo(_block.Compress(data)));
  }

  [Test]
  public void Optimal_BeatsGreedy_OnText() {
    var data = Encoding.ASCII.GetBytes(string.Concat(
      Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 500)));

    var optimalSize = new Lz77OptimalBuildingBlock().Compress(data).Length;
    var greedySize = new Lz77BuildingBlock().Compress(data).Length;

    Assert.That(optimalSize, Is.LessThan(greedySize),
      $"optimal parse ({optimalSize} B) must beat greedy ({greedySize} B) on text");
  }

  [Test]
  public void Optimal_NeverWorseThanGreedy_OnVariousInputs() {
    var samples = new List<byte[]> {
      Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcdefgh", 300))),
      Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("aaaabbbbcccc", 200))),
      Encoding.UTF8.GetBytes("To be, or not to be, that is the question. To be or not."),
    };

    foreach (var data in samples) {
      var optimalSize = new Lz77OptimalBuildingBlock().Compress(data).Length;
      var greedySize = new Lz77BuildingBlock().Compress(data).Length;
      Assert.That(optimalSize, Is.LessThanOrEqualTo(greedySize),
        $"optimal must be <= greedy (opt={optimalSize}, greedy={greedySize})");
    }
  }

  [Test]
  public void Block_IsRegistered_WithUniqueId() {
    var optimal = BuildingBlockRegistry.GetById("BB_Lz77Optimal");
    Assert.That(optimal, Is.Not.Null, "BB_Lz77Optimal must be auto-registered");

    var ids = BuildingBlockRegistry.All.Select(b => b.Id).ToList();
    var unique = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.That(unique, Has.Count.EqualTo(ids.Count), "building-block IDs must be unique");
  }
}
