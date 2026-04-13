namespace Compression.Tests.Lzham;

[TestFixture]
public class LzhamTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello, LZHAM compression! This is a test of LZ77 with Huffman coding."u8.ToArray();
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[1024];
    // Repetitive pattern to trigger LZ77 matches
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 16);
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Empty() {
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    var compressed = bb.Compress([]);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ShortData() {
    var data = "AB"u8.ToArray();
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    var compressed = bb.Compress(data);
    var decompressed = bb.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Compression_Ratio() {
    // Repetitive data should compress well
    var data = new byte[4096];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 8);
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    var compressed = bb.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length), "Repetitive data should compress");
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_Properties() {
    var bb = new Compression.Core.Dictionary.Lzham.LzhamBuildingBlock();
    Assert.That(bb.Id, Is.EqualTo("BB_LZHAM"));
    Assert.That(bb.DisplayName, Is.EqualTo("LZHAM"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }
}
