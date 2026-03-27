namespace Compression.Tests.Lzg;

[TestFixture]
public class LzgTests {

  private static byte[] RoundTrip(byte[] data) {
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Lzg.LzgStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Lzg.LzgStream.Decompress(compressed, decompressed);

    return decompressed.ToArray();
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, LZG! This is a test of LZ77-style compression."u8.ToArray();
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;

    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 0x42 };
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepeatingPattern() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 4);

    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeRandomData() {
    var data = new byte[100 * 1024];
    var rng = new Random(54321);
    rng.NextBytes(data);

    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Compress_RepetitiveData_SmallerThanOriginal() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Lzg.LzgStream.Compress(input, compressed);

    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsLzg() {
    var data = "test data for magic check"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Lzg.LzgStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'L'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'Z'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'G'));
  }
}
