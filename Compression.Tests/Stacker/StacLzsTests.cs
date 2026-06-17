#pragma warning disable CS1591
using System.Text;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Exercises the Stac LZS codec (RFC 1967 / RFC 2395) used for compressed
/// STACVOL clusters: compress -> decompress must reproduce the input exactly,
/// and compressible data must actually shrink.
/// </summary>
[TestFixture]
public class StacLzsTests {

  private static void RoundTrip(byte[] data) {
    var packed = StacLzs.Compress(data);
    var back = StacLzs.Decompress(packed, data.Length);
    Assert.That(back, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() => RoundTrip([]);

  [Test, Category("HappyPath")]
  public void SingleByte_RoundTrips() => RoundTrip([0x42]);

  [Test, Category("HappyPath")]
  public void Literals_RoundTrip() =>
    RoundTrip(Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz0123456789"));

  [Test, Category("HappyPath")]
  public void Repetitive_RoundTrips_AndShrinks() {
    var data = Encoding.ASCII.GetBytes(new string('Z', 1000));
    var packed = StacLzs.Compress(data);
    Assert.That(packed.Length, Is.LessThan(data.Length), "RLE-like data must compress.");
    Assert.That(StacLzs.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void MixedText_RoundTrips() {
    var sb = new StringBuilder();
    for (var i = 0; i < 200; i++) sb.Append("the cat sat on the mat. ");
    RoundTrip(Encoding.ASCII.GetBytes(sb.ToString()));
  }

  [Test, Category("HappyPath")]
  public void LongMatch_AcrossWindow_RoundTrips() {
    var rng = new Random(99);
    var block = new byte[300];
    rng.NextBytes(block);
    var data = new byte[block.Length * 6];
    for (var i = 0; i < 6; i++) Array.Copy(block, 0, data, i * block.Length, block.Length);
    RoundTrip(data);
  }

  [Test, Category("HappyPath")]
  public void Random_RoundTrips() {
    var rng = new Random(2024);
    var data = new byte[4096];
    rng.NextBytes(data);
    RoundTrip(data);
  }

  [Test, Category("EdgeCase")]
  public void NearMaxOffset_RoundTrips() {
    // Force matches near the 2047-byte window boundary.
    var data = new byte[6000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
    RoundTrip(data);
  }
}
