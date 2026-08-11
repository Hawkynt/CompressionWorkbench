namespace Compression.Tests.Rzip;

[TestFixture]
public class RzipTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallData() {
    var data = "Hello, RZIP! Long-distance redundancy elimination."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    // Data with long-distance repetitions (ideal for rzip)
    var block = new byte[1024];
    Random.Shared.NextBytes(block);
    var data = new byte[block.Length * 4];
    for (var i = 0; i < 4; i++)
      Buffer.BlockCopy(block, 0, data, i * block.Length, block.Length);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsRZIP() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x52)); // 'R'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x5A)); // 'Z'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x49)); // 'I'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x50)); // 'P'
  }

  [Test, Category("HappyPath")]
  public void LongRangeDuplicate_IsFound_BeyondAnyLz77Window() {
    // The whole point of rzip: a block repeated 128 KB later must still be encoded as a
    // reference, not re-sent. Indexing only across chunk boundaries finds nothing here,
    // because the input is far smaller than one chunk.
    var block = new byte[64 * 1024];
    var filler = new byte[128 * 1024];
    new Random(0x2C11).NextBytes(block);
    new Random(0x5E77).NextBytes(filler);
    var data = new byte[block.Length * 2 + filler.Length];
    block.CopyTo(data, 0);
    filler.CopyTo(data, block.Length);
    block.CopyTo(data, block.Length + filler.Length);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    // The repeat is a quarter of the input, so finding it has to show up as a saving
    // well past anything the entropy stage alone could deliver on random bytes.
    Assert.That(compressed.Length, Is.LessThan(data.Length * 4 / 5));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_Empty() {
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream([]))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void ShortInput_DoesNotInflateByTheCodeTable() {
    // A 256-byte code table must never be spent on a literal run too small to repay it.
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    Assert.That(compressed.Length, Is.LessThan(data.Length + 32));
  }

  [Test, Category("Exception")]
  public void Decompress_ForeignData_Throws() {
    using var input = new MemoryStream("not an rzip stream at all"u8.ToArray());
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => FileFormat.Rzip.RzipStream.Decompress(input, output));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}
