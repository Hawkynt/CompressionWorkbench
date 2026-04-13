namespace Compression.Tests.ExpGolomb;

[TestFixture]
public class ExpGolombTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello, Exp-Golomb!"u8.ToArray();
    var bb = new Compression.Core.Entropy.ExpGolomb.ExpGolombBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllBytes() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    var bb = new Compression.Core.Entropy.ExpGolomb.ExpGolombBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Empty() {
    var bb = new Compression.Core.Entropy.ExpGolomb.ExpGolombBuildingBlock();
    var compressed = bb.Compress([]);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallValues() {
    var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
    var bb = new Compression.Core.Entropy.ExpGolomb.ExpGolombBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_Properties() {
    var bb = new Compression.Core.Entropy.ExpGolomb.ExpGolombBuildingBlock();
    Assert.That(bb.Id, Is.EqualTo("BB_ExpGolomb"));
    Assert.That(bb.DisplayName, Is.EqualTo("Exp-Golomb"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
  }
}
