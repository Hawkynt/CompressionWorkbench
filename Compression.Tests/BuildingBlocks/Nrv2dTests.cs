using System.Text;
using Compression.Core.Dictionary.Nrv2d;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class Nrv2dTests {

  private static readonly Nrv2dBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    Assert.That(Bb.Decompress(Bb.Compress([])), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("NRV2D length encoding sample!");
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingPattern_CompressesAndRoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31) & 0x7);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ConstantByte_RoundTrips() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0x5A);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RandomData_RoundTrips() {
    var rng = new Random(0xBEEF);
    var data = new byte[4096];
    rng.NextBytes(data);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Metadata_Correct() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Nrv2d"));
    Assert.That(Bb.DisplayName, Is.EqualTo("NRV2D"));
  }

  // ── LE16 width variant (UPX method 5) ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQ");
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2dBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_ConstantByte_RoundTrips() {
    var data = new byte[1024];
    Array.Fill<byte>(data, 0xC3);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2dBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(bare.Length, Is.LessThan(data.Length / 2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RepeatingPattern_RoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 17) & 0x1F);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2dBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RandomData_RoundTrips() {
    var rng = new Random(0xCAFE);
    var data = new byte[3000];
    rng.NextBytes(data);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2dBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  // ── 8-bit width variant (UPX method 7) ──────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("ABCDEFGHI");
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2dBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_ConstantByte_RoundTrips() {
    var data = new byte[512];
    Array.Fill<byte>(data, 0x33);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2dBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RepeatingPattern_RoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 13);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2dBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RandomData_RoundTrips() {
    var rng = new Random(0xC0DE);
    var data = new byte[1500];
    rng.NextBytes(data);
    var bare = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2dBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Le16AndByte_StreamsAreNotIdenticalToLe32() {
    var data = Encoding.ASCII.GetBytes("NRV2D width sentinel — Lorem ipsum dolor sit amet, consectetur");
    var le32 = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 4);
    var le16 = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var by = Nrv2dBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    Assert.That(le16, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le16).AsCollection);
  }
}
