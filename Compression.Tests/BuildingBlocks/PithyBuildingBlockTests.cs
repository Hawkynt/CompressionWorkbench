using System.Text;
using Compression.Core.Dictionary.Pithy;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class PithyBuildingBlockTests {

  private static readonly PithyBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x50];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x66);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(unchecked((int)0xB16B00B5));
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(
      "Pithy is John Engelhart's Snappy-shaped compressor with a 3-byte offset tier. " +
      "Pithy is John Engelhart's Snappy-shaped compressor with a 3-byte offset tier.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void LongLiteralRun_ExercisesVarLengthEscape() {
    var rng = new Random(0x51);
    var data = new byte[400];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FarOffsetMatch_ExercisesCopy3Tier() {
    // Repeats a large block after >64 KiB so the match offset exceeds the
    // 16-bit copy-2 tier and must use the 3-byte copy-3 offset field.
    var rng = new Random(0x03);
    var block = new byte[80_000];
    rng.NextBytes(block);
    var data = new byte[block.Length + 5000];
    block.CopyTo(data, 0);
    Array.Copy(block, 0, data, block.Length, 5000);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void ShortNearMatch_ExercisesCopy1Tier() {
    // A short match (4-11 bytes) close behind (offset < 2048) must use the
    // compact copy-1 tag.
    var rng = new Random(0x201);
    var block = new byte[50];
    rng.NextBytes(block);
    var data = new byte[block.Length + 20 + 8];
    block.CopyTo(data, 0);
    rng.NextBytes(data.AsSpan(block.Length, 20));
    Array.Copy(block, 0, data, block.Length + 20, 8);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void MidLengthMatch_ExercisesLengthEscape62() {
    // A match of 63-318 bytes at an offset >= 2048 must use the copy-2 tag's
    // 62 length-escape value (one extra byte holding length - 63).
    var rng = new Random(0x202);
    var block = new byte[3000];
    rng.NextBytes(block);
    var data = new byte[block.Length + 200];
    block.CopyTo(data, 0);
    Array.Copy(block, 0, data, block.Length, 200);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void LongMatch_ExercisesLengthEscape63() {
    // A match of 319+ bytes at an offset >= 2048 must use the copy-2 tag's
    // 63 length-escape value (two extra bytes holding the raw 16-bit length).
    var rng = new Random(0x203);
    var block = new byte[3000];
    rng.NextBytes(block);
    var data = new byte[block.Length + 400];
    block.CopyTo(data, 0);
    Array.Copy(block, 0, data, block.Length, 400);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AlternatingPattern_RoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xA5 : 0x5A);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Pithy"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Pithy"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
