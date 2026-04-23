using System.Text;
using Compression.Core.Dictionary.Nrv2e;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class Nrv2eTests {

  private static readonly Nrv2eBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    Assert.That(Bb.Decompress(Bb.Compress([])), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("NRV2E pure-varint length encoding sample!");
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingPattern_CompressesAndRoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 32);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ConstantByte_RoundTrips() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0xA5);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RandomData_RoundTrips() {
    var rng = new Random(0x1234);
    var data = new byte[4096];
    rng.NextBytes(data);
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Metadata_Correct() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Nrv2e"));
    Assert.That(Bb.DisplayName, Is.EqualTo("NRV2E"));
  }

  // ── LE16 width variant (UPX method 9) ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQ");
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2eBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_ConstantByte_RoundTrips() {
    var data = new byte[1024];
    Array.Fill<byte>(data, 0xC3);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2eBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(bare.Length, Is.LessThan(data.Length / 2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RepeatingPattern_RoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 23);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2eBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RandomData_RoundTrips() {
    var rng = new Random(0xCAFE);
    var data = new byte[3000];
    rng.NextBytes(data);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2eBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  // ── 8-bit width variant (UPX method 10) ─────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_LiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("ABCDEFGHI");
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2eBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_ConstantByte_RoundTrips() {
    var data = new byte[512];
    Array.Fill<byte>(data, 0x33);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2eBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RepeatingPattern_RoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 11);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2eBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RandomData_RoundTrips() {
    var rng = new Random(0xC0DE);
    var data = new byte[1500];
    rng.NextBytes(data);
    var bare = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2eBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Le16AndByte_StreamsAreNotIdenticalToLe32() {
    var data = Encoding.ASCII.GetBytes("NRV2E width sentinel — quick brown fox jumps over the lazy dog");
    var le32 = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 4);
    var le16 = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var by = Nrv2eBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    Assert.That(le16, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le16).AsCollection);
  }
}
