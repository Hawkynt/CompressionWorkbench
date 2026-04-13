namespace Compression.Tests.Ans;

[TestFixture]
public class RansTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello, rANS entropy coding!"u8.ToArray();
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllBytes() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Empty() {
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    var compressed = bb.Compress([]);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 42 };
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0xAA);
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_Properties() {
    var bb = new Compression.Core.Entropy.Ans.RansBuildingBlock();
    Assert.That(bb.Id, Is.EqualTo("BB_rANS"));
    Assert.That(bb.DisplayName, Is.EqualTo("rANS"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
  }
}
