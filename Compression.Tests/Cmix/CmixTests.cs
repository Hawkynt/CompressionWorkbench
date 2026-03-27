using FileFormat.Cmix;
namespace Compression.Tests.Cmix;

[TestFixture]
public class CmixTests {

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(256)]
  [TestCase(4096)]
  [TestCase(65536)]
  public void RoundTrip(int size) {
    var data = new byte[size];
    Random.Shared.NextBytes(data);
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CmixStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CmixStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CmixStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CmixStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CmixStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CmixStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Header_Is5Bytes_NoVocab_SmallData() {
    // Small data (< 10000) → only 5-byte header
    var data = new byte[100];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CmixStream.Compress(input, compressed);
    // Header is 5 bytes; no vocab bitmap for size < 10000
    compressed.Position = 0;
    var b0 = compressed.ReadByte(); // upper7 bits of size
    Assert.That(b0 & 0x80, Is.EqualTo(0)); // dict flag must be 0
    var b1 = compressed.ReadByte();
    var b2 = compressed.ReadByte();
    var b3 = compressed.ReadByte();
    var b4 = compressed.ReadByte();
    var size = ((long)(b0 & 0x7F) << 32) | ((long)(uint)((b1 << 24) | (b2 << 16) | (b3 << 8) | b4));
    Assert.That(size, Is.EqualTo(100));
  }

  [Test, Category("HappyPath")]
  public void Header_HasVocabBitmap_LargeData() {
    // size >= 10000 → 5-byte header + 32-byte vocab bitmap
    var data = new byte[10000];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CmixStream.Compress(input, compressed);
    // Total header = 5 + 32 = 37 bytes, plus encoded data
    Assert.That(compressed.Length, Is.GreaterThan(37));
  }
}
