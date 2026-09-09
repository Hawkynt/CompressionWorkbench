namespace Compression.Tests.Squeeze;

[TestFixture]
public class SqueezeTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, Squeeze! This is a test of Huffman compression."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_Is0xFF76() {
    var data = "test"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x76));
    Assert.That(compressed.ReadByte(), Is.EqualTo(0xFF));
  }

  [Test, Category("Spec")]
  public void StandaloneHeader_WritesChecksumBeforeFilename() {
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream("A"u8.ToArray()))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed, "A.TXT");

    Assert.That(compressed.ToArray()[..10], Is.EqualTo(new byte[] {
      0x76, 0xFF,       // magic
      0x41, 0x00,       // checksum of the expanded data
      (byte)'A', (byte)'.', (byte)'T', (byte)'X', (byte)'T', 0x00,
    }));
  }

  [Test, Category("Spec")]
  public void Decompress_KnownStandaloneVector_WithRle() {
    // Independent wire-format vector for raw "AAA". Its Huffman payload decodes to
    // [ 'A', 0x90, 3, EOF ], which the SQ RLE stage expands back to three A bytes.
    byte[] squeezed = [
      0x76, 0xFF,             // magic
      0xC3, 0x00,             // checksum: 3 * 'A'
      0x00,                   // empty filename
      0x03, 0x00,             // three internal nodes
      0x01, 0x00, 0x02, 0x00,
      0xBE, 0xFF, 0x6F, 0xFF, // leaves: 'A', 0x90
      0xFC, 0xFF, 0xFF, 0xFE, // leaves: 3, EOF(256)
      0xD8,                   // LSB-first codes: 00, 01, 10, 11
    ];

    using var input = new MemoryStream(squeezed, writable: false);
    using var output = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo("AAA"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RleDelimiterAndRunBoundaries() {
    var data = new List<byte>();
    data.AddRange(Enumerable.Repeat((byte)'A', 2));
    data.AddRange(Enumerable.Repeat((byte)'B', 3));
    data.Add(0x90);
    data.AddRange(Enumerable.Repeat((byte)'C', 255));
    data.AddRange(Enumerable.Repeat((byte)'D', 256));

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data.ToArray(), writable: false))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void EmptyStream_UsesZeroNodeTree() {
    using var compressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Compress(Stream.Null, compressed);

    Assert.That(compressed.ToArray(), Is.EqualTo(new byte[] {
      0x76, 0xFF,
      0x00, 0x00, // checksum
      0x00,       // filename
      0x00, 0x00, // zero-node tree
    }));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.Length, Is.Zero);
  }

  [Test, Category("Malformed")]
  public void Decompress_RejectsIncompleteRleEscape() {
    byte[] squeezed = [
      0x76, 0xFF,
      0x00, 0x00,
      0x00,
      0x01, 0x00,
      0x6F, 0xFF, // left = 0x90
      0xFF, 0xFE, // right = EOF
      0x02,       // bits: left, then right
    ];

    using var input = new MemoryStream(squeezed, writable: false);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => FileFormat.Squeeze.SqueezeStream.Decompress(input, output));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    Assert.That(compressed.Length, Is.LessThan(data.Length));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}
