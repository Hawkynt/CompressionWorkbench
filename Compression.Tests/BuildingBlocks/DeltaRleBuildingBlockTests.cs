using System.Text;
using Compression.Core.Transforms;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class DeltaRleBuildingBlockTests {

  private static readonly DeltaRleBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(compressed, Is.Empty);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x41];
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(compressed, Is.EqualTo(data).AsCollection);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTripsAndCompresses() {
    var data = new byte[256];
    Array.Fill(data, (byte)0x61);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    // First byte is copied verbatim by the delta stage, the remaining 255 zero-deltas
    // collapse to a single (marker, count, value) run triplet.
    Assert.That(compressed, Is.EqualTo(new byte[] { 0x61, 0xFF, 0xFF, 0x00 }).AsCollection);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[513];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[2048];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "the quick brown fox jumps over the lazy dog. ", 4)));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    // Delta of a 0..255 ramp is a constant +1 run: first byte verbatim, then a single
    // 255-long run of delta value 1.
    Assert.That(compressed, Is.EqualTo(new byte[] { 0x00, 0xFF, 0xFF, 0x01 }).AsCollection);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void LiteralMarkerByteInDeltaStream_RoundTrips() {
    // Chosen so the delta stream itself is [0, 255, 1, 10, 10, 10]: an isolated marker
    // byte (255) that must be escaped as (0xFF, 1, 0xFF), immediately followed by a run.
    // This is exactly where a marker-based RLE breaks if the escape is wrong.
    byte[] data = [0, 255, 0, 10, 20, 30];
    var compressed = Bb.Compress(data);
    Assert.That(compressed, Is.EqualTo(new byte[] { 0x00, 0xFF, 0x01, 0xFF, 0x01, 0xFF, 0x03, 0x0A }).AsCollection);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void KnownVector_MatchesReferenceEncoding() {
    // Cross-checked against the reference "Delta + RLE" implementation.
    byte[] data = [10, 12, 14, 16];
    var compressed = Bb.Compress(data);
    Assert.That(compressed, Is.EqualTo(new byte[] { 10, 255, 3, 2 }).AsCollection);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_DeltaRle"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Delta + RLE"));
      Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Transform));
    });
  }
}
