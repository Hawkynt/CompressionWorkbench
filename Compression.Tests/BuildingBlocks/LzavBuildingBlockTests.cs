using System.Text;
using Compression.Core.Dictionary.Lzav;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzavBuildingBlockTests {

  private static readonly LzavBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x5C];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x44);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xACE1);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(
      "LZAV is Aleksey Vaneev's fast in-memory LZ77 codec. LZAV is Aleksey Vaneev's fast in-memory LZ77 codec.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void LongLiteralRun_RoundTrips() {
    // Exceeds the 6-bit length field (63) to exercise the continuation-byte path.
    var rng = new Random(3);
    var data = new byte[400];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FarOffsetMatch_RoundTrips() {
    var rng = new Random(0x77);
    var block = new byte[5000];
    rng.NextBytes(block);
    var data = new byte[block.Length * 4];
    for (var i = 0; i < 4; ++i)
      block.CopyTo(data, i * block.Length);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void OffsetTier1_10Bit_RoundTrips() {
    // Repeat block ~200 bytes back, well within the 10-bit (<=1023) offset tier.
    var rng = new Random(0x101);
    var block = new byte[200];
    rng.NextBytes(block);
    var filler = new byte[40];
    rng.NextBytes(filler);
    var data = new byte[block.Length + filler.Length + block.Length];
    block.CopyTo(data, 0);
    filler.CopyTo(data, block.Length);
    block.CopyTo(data, block.Length + filler.Length);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void OffsetTier2_15Bit_RoundTrips() {
    // Repeat block ~10000 bytes back, in the 15-bit (1024..32767) offset tier.
    var rng = new Random(0x102);
    var block = new byte[10_000];
    rng.NextBytes(block);
    var data = new byte[block.Length + 5000];
    block.CopyTo(data, 0);
    Array.Copy(block, 0, data, block.Length, 5000);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void OffsetTier3_21Bit_RoundTrips() {
    // Repeat block ~100000 bytes back, past the 15-bit tier and into the
    // 21-bit (up to 2097151) offset tier.
    var rng = new Random(0x103);
    var block = new byte[100_000];
    rng.NextBytes(block);
    var data = new byte[block.Length + 5000];
    block.CopyTo(data, 0);
    Array.Copy(block, 0, data, block.Length, 5000);
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
      Assert.That(Bb.Id, Is.EqualTo("BB_Lzav"));
      Assert.That(Bb.DisplayName, Is.EqualTo("LZAV"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
