namespace Compression.Tests.Density;

[TestFixture]
public class DensityTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_Chameleon() {
    var data = MakeRepetitiveData(10000);
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream(data), compressed, FileFormat.Density.DensityStream.Algorithm.Chameleon);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Density.DensityStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_Cheetah() {
    var data = MakeRepetitiveData(10000);
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream(data), compressed, FileFormat.Density.DensityStream.Algorithm.Cheetah);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Density.DensityStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_Lion() {
    var data = MakeRepetitiveData(10000);
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream(data), compressed, FileFormat.Density.DensityStream.Algorithm.Lion);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Density.DensityStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsDens() {
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream("test"u8.ToArray()), compressed);
    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo((byte)'D'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((byte)'E'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((byte)'N'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((byte)'S'));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_SmallData() {
    var data = "Hi"u8.ToArray();
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream(data), compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Density.DensityStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_EmptyData() {
    var data = Array.Empty<byte>();
    using var compressed = new MemoryStream();
    FileFormat.Density.DensityStream.Compress(new MemoryStream(data), compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Density.DensityStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  private static byte[] MakeRepetitiveData(int size) {
    var data = new byte[size];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < size; i++)
      data[i] = pattern[i % pattern.Length];
    return data;
  }
}
