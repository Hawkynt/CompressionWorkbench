using System.Text;
using Compression.Core.Dictionary.GbaLz77;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class GbaLz77BuildingBlockTests {

  private static readonly GbaLz77BuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    Assert.That(compressed, Is.Empty);
    Assert.That(Bb.Decompress(compressed), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x41];
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 4)));
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[256];
    Array.Fill(data, (byte)0x61);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EveryByteValue_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)i;
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x10);
    var data = new byte[8192];
    rng.NextBytes(data);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void OneMegabyte_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 23874)));
    Assert.That(data.Length, Is.GreaterThan(1 << 20));
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// A run of 48 zero bytes is the smallest stream exercising the overlapping (distance 1)
  /// copy path together with a maximal 18-byte match, so its encoding pins the whole layout.
  /// </summary>
  [Test, Category("HappyPath")]
  public void ZeroRun_MatchesKnownEncoding() {
    var data = new byte[48];
    byte[] expected = [0x10, 48, 0, 0, 0x70, 0x00, 0xF0, 0x00, 0xF0, 0x12, 0x80, 0x24];
    Assert.That(Bb.Compress(data), Is.EqualTo(expected).AsCollection);
    Assert.That(Bb.Decompress(expected), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void ForeignTypeByte_Throws() {
    byte[] bogus = [0x11, 1, 0, 0, 0x00, 0x41];
    Assert.Throws<InvalidDataException>(() => Bb.Decompress(bogus));
  }

  [Test, Category("EdgeCase")]
  public void TruncatedStream_Throws() {
    byte[] truncated = [0x10, 8, 0, 0, 0x00, 0x41];
    Assert.Throws<InvalidDataException>(() => Bb.Decompress(truncated));
  }

  [Test, Category("EdgeCase")]
  public void BackReferenceBeforeStart_Throws() {
    byte[] bogus = [0x10, 8, 0, 0, 0x80, 0x00, 0x00];
    Assert.Throws<InvalidDataException>(() => Bb.Decompress(bogus));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_GbaLz77"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Nintendo GBA/NDS LZ77 (type 0x10)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
