using Compression.Core.Streams;
using FileFormat.Gzip;
using SysGzip = System.IO.Compression.GZipStream;
using SysCompressionMode = System.IO.Compression.CompressionMode;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Compression.Tests.Gzip;

[TestFixture]
public class GzipInteropTests {
  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemDecompresses_OurOutput() {
    byte[] data = "Hello, World! Interop test data."u8.ToArray();
    byte[] compressed = CompressWithOurs(data);

    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeDecompress_SystemOutput() {
    byte[] data = "Hello, World! Interop test data."u8.ToArray();
    byte[] compressed = CompressWithSystem(data);

    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemDecompresses_OurOutput_LargeData() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 200];
    for (int i = 0; i < 200; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeDecompress_SystemOutput_LargeData() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 200];
    for (int i = 0; i < 200; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = CompressWithSystem(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemDecompresses_OurOutput_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[4096];
    rng.NextBytes(data);

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeDecompress_SystemOutput_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[4096];
    rng.NextBytes(data);

    byte[] compressed = CompressWithSystem(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeDecompress_SystemOutput_Empty() {
    byte[] data = [];
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  private static byte[] CompressWithOurs(byte[] data) {
    using var ms = new MemoryStream();
    using (var gz = new GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true)) {
      gz.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressWithOurs(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var gz = new GzipStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] CompressWithSystem(byte[] data) {
    using var ms = new MemoryStream();
    using (var gz = new SysGzip(ms, SysCompressionLevel.Optimal, leaveOpen: true)) {
      gz.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressWithSystem(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var gz = new SysGzip(ms, SysCompressionMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
  }
}
