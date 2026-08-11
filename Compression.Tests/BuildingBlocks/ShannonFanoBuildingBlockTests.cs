using System.Text;
using Compression.Core.Entropy;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ShannonFanoBuildingBlockTests {

  private static readonly ShannonFanoBuildingBlock Bb = new();

  /// <summary>
  /// The header carries one uint16 count per symbol. Above this the table is
  /// rescaled, which is the point at which encoder and decoder used to derive
  /// their trees from different numbers.
  /// </summary>
  private const int ScalingPoint = ushort.MaxValue + 1;

  /// <summary>
  /// Nine of the pangram's forty-five characters are spaces, so the space count
  /// reaches <see cref="ScalingPoint"/> at exactly five times that value.
  /// </summary>
  private const string Pangram = "the quick brown fox jumps over the lazy dog. ";

  private static byte[] Pangrams(int length) {
    var sb = new StringBuilder(length + Pangram.Length);
    while (sb.Length < length)
      sb.Append(Pangram);
    return Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    foreach (byte b in new byte[] { 0x00, 0x01, 0x41, 0x7F, 0xFF }) {
      var round = Bb.Decompress(Bb.Compress([b]));
      Assert.That(round, Is.EqualTo(new[] { b }).AsCollection);
    }
  }

  [Test, Category("EdgeCase")]
  public void AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTrips() {
    var data = new byte[20 * 1024];
    Array.Fill(data, (byte)0x61);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x5F);
    var data = new byte[200 * 1024];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Given text whose commonest symbol occurs more than <see cref="ushort.MaxValue"/>
  /// times, when it is compressed and decompressed, then it must come back
  /// unchanged. The frequency table is rescaled to fit uint16 at that point, and
  /// deriving the encoder's codes from the raw counts instead of the rescaled
  /// table silently produced output of the right length but the wrong contents.
  /// </summary>
  [TestCase(ScalingPoint * 5 - 1, TestName = "BelowScalingPoint")]
  [TestCase(ScalingPoint * 5, TestName = "AtScalingPoint")]
  [TestCase(ScalingPoint * 5 + 1, TestName = "AboveScalingPoint")]
  [TestCase(400 * 1024, TestName = "FourHundredKilobytes")]
  [TestCase(1024 * 1024, TestName = "OneMegabyte")]
  [Category("Boundary"), Category("RoundTrip")]
  public void RescaledFrequencyTable_RoundTrips(int length) {
    var data = Pangrams(length);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Frequencies halving from symbol to symbol drive the split search to peel one
  /// symbol at a time, producing the deepest codes the scheme can emit.
  /// </summary>
  [Test, Category("Boundary"), Category("RoundTrip")]
  public void DeeplySkewedDistribution_RoundTrips() {
    var parts = new List<byte>();
    var count = 1 << 16;
    for (var symbol = 0; symbol < 256; symbol++) {
      for (var i = 0; i < Math.Max(1, count); i++)
        parts.Add((byte)symbol);
      count /= 2;
    }

    var data = parts.ToArray();
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void EnglishText_Compresses() {
    var data = Pangrams(90 * 1024);
    var compressed = Bb.Compress(data);
    Assert.That(compressed, Has.Length.LessThan(data.Length));
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_ShannonFano"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Shannon-Fano"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
    });
  }
}
