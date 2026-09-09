namespace Compression.Tests.Lzg;

[TestFixture]
public class LzgTests {

  private static readonly byte[] LiblzgAbRepeatVector = [
    0x4C, 0x5A, 0x47,             // "LZG"
    0x00, 0x00, 0x00, 0x0A,       // decoded size = 10
    0x00, 0x00, 0x00, 0x08,       // encoded size = 8
    0x02, 0x20, 0x00, 0xB3,       // liblzg checksum of payload
    0x01,                         // LZG1
    0x00, 0x01, 0x02, 0x03,       // marker symbols
    0x41, 0x42,                   // "AB"
    0x03, 0x26,                   // M4: offset 2, length 8
  ];

  private static byte[] Compress(byte[] data) {
    using var compressed = new MemoryStream();
    using var input = new MemoryStream(data, writable: false);
    FileFormat.Lzg.LzgStream.Compress(input, compressed);
    return compressed.ToArray();
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var decompressed = new MemoryStream();
    FileFormat.Lzg.LzgStream.Decompress(input, decompressed);
    return decompressed.ToArray();
  }

  private static byte[] RoundTrip(byte[] data) => Decompress(Compress(data));

  [Test, Category("Spec")]
  public void Decode_LiblzgLzg1Vector() {
    Assert.That(Decompress(LiblzgAbRepeatVector), Is.EqualTo("ABABABABAB"u8.ToArray()));
  }

  [Test, Category("Spec")]
  public void Encode_LiblzgLzg1Vector_IsByteExact() {
    Assert.That(Compress("ABABABABAB"u8.ToArray()), Is.EqualTo(LiblzgAbRepeatVector));
  }

  [Test, Category("Spec")]
  public void Decode_LiblzgCopyVector() {
    byte[] vector = [
      0x4C, 0x5A, 0x47,
      0x00, 0x00, 0x00, 0x03,
      0x00, 0x00, 0x00, 0x03,
      0x01, 0x8D, 0x00, 0xC7,
      0x00,
      0x41, 0x42, 0x43,
    ];

    Assert.That(Decompress(vector), Is.EqualTo("ABC"u8.ToArray()));
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

    Assert.That(Compress(data).Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsLzg() {
    var compressed = Compress("test data for magic check"u8.ToArray());
    Assert.That(compressed.AsSpan(0, 3).ToArray(), Is.EqualTo("LZG"u8.ToArray()));
  }

  [Test, Category("Malformed")]
  public void Decompress_ChecksumMismatch_Throws() {
    var corrupted = LiblzgAbRepeatVector.ToArray();
    corrupted[^1] ^= 0x01;

    Assert.That(
      () => Decompress(corrupted),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("checksum"));
  }

  [Test, Category("Malformed")]
  public void Decompress_TruncatedMatch_Throws() {
    byte[] vector = [
      0x4C, 0x5A, 0x47,
      0x00, 0x00, 0x00, 0x01,
      0x00, 0x00, 0x00, 0x06,
      0x00, 0x00, 0x00, 0x00, // deliberately fixed below
      0x01,
      0x00, 0x01, 0x02, 0x03,
      0x00, 0x01,
    ];

    // Checksum over 00 01 02 03 00 01 = 0x001D0008.
    vector[11] = 0x00;
    vector[12] = 0x1D;
    vector[13] = 0x00;
    vector[14] = 0x08;

    Assert.That(() => Decompress(vector), Throws.TypeOf<InvalidDataException>());
  }
}
