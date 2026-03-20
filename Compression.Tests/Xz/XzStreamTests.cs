using Compression.Core.Streams;
using FileFormat.Xz;

namespace Compression.Tests.Xz;

[TestFixture]
public class XzStreamTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    byte[] data = "Hello, XZ World! Testing LZMA2-based compression."u8.ToArray();
    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    byte[] pattern = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 100];
    for (int i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(123);
    byte[] data = new byte[51200]; // 50KB
    for (int i = 0; i < data.Length; ++i) {
      if (i % 100 < 50)
        data[i] = (byte)(i % 26 + 'a');
      else
        data[i] = (byte)rng.Next(256);
    }

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void StreamHeader_MagicBytes() {
    byte[] data = [1, 2, 3];
    byte[] compressed = CompressWithOurs(data);

    // First 6 bytes should be the XZ magic
    Assert.That(compressed[0], Is.EqualTo(0xFD));
    Assert.That(compressed[1], Is.EqualTo(0x37));
    Assert.That(compressed[2], Is.EqualTo(0x7A));
    Assert.That(compressed[3], Is.EqualTo(0x58));
    Assert.That(compressed[4], Is.EqualTo(0x5A));
    Assert.That(compressed[5], Is.EqualTo(0x00));
  }

  [Category("ThemVsUs")]
  [Test]
  public void StreamFooter_Format() {
    byte[] data = [1, 2, 3];
    byte[] compressed = CompressWithOurs(data);

    // Last 2 bytes should be "YZ"
    Assert.That(compressed[^2], Is.EqualTo((byte)'Y'));
    Assert.That(compressed[^1], Is.EqualTo((byte)'Z'));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Index_RoundTrips() {
    // Test XzIndex directly
    using var ms = new MemoryStream();
    var index = new XzIndex();
    index.Records.Add((100, 200));
    index.Records.Add((150, 300));
    index.Write(ms);

    ms.Position = 0;
    var decoded = XzIndex.Read(ms);
    Assert.That(decoded.Records, Has.Count.EqualTo(2));
    Assert.That(decoded.Records[0].UnpaddedSize, Is.EqualTo(100));
    Assert.That(decoded.Records[0].UncompressedSize, Is.EqualTo(200));
    Assert.That(decoded.Records[1].UnpaddedSize, Is.EqualTo(150));
    Assert.That(decoded.Records[1].UncompressedSize, Is.EqualTo(300));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CheckType_Crc32_Works() {
    byte[] data = "CRC-32 check test"u8.ToArray();

    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, XzConstants.CheckCrc32, leaveOpen: true)) {
      xz.Write(data, 0, data.Length);
    }

    ms.Position = 0;
    using var xz2 = new XzStream(ms, CompressionStreamMode.Decompress,
      1 << 20, XzConstants.CheckCrc32);
    using var output = new MemoryStream();
    xz2.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CheckType_Crc64_Works() {
    byte[] data = "CRC-64 check test"u8.ToArray();
    byte[] compressed = CompressWithOurs(data); // Default is CRC-64
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    byte[] data = new byte[4096];
    Array.Fill(data, (byte)'A');

    byte[] compressed = CompressWithOurs(data);
    double ratio = (double)compressed.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.1), $"Compression ratio {ratio:P} too high");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Varint_EncodeDecode_RoundTrips() {
    ulong[] testValues = [0, 1, 127, 128, 255, 256, 65535, 0xFFFFFFFF, 0x7FFFFFFFFFFFFFFF];

    foreach (ulong value in testValues) {
      using var ms = new MemoryStream();
      XzVarint.Write(ms, value);

      int expectedSize = XzVarint.EncodedSize(value);
      Assert.That(ms.Length, Is.EqualTo(expectedSize),
        $"Encoded size mismatch for value {value}");

      ms.Position = 0;
      ulong decoded = XzVarint.Read(ms);
      Assert.That(decoded, Is.EqualTo(value), $"Round-trip mismatch for value {value}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Sha256Check() {
    byte[] data = "XZ with SHA-256 check type test data."u8.ToArray();
    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, XzConstants.CheckSha256, leaveOpen: true))
      xz.Write(data, 0, data.Length);

    ms.Position = 0;
    using var xzDec = new XzStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xzDec.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Crc32Check() {
    byte[] data = "XZ with CRC-32 check type test data."u8.ToArray();
    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, XzConstants.CheckCrc32, leaveOpen: true))
      xz.Write(data, 0, data.Length);

    ms.Position = 0;
    using var xzDec = new XzStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xzDec.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  private static byte[] CompressWithOurs(byte[] data) {
    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, leaveOpen: true)) {
      xz.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressWithOurs(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var xz = new XzStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }
}
