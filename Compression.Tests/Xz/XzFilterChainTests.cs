using Compression.Core.Streams;
using FileFormat.Xz;

namespace Compression.Tests.Xz;

[TestFixture]
public class XzFilterChainTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithBcjX86Filter() {
    // Simulate x86-ish data with E8/E9 bytes
    byte[] data = new byte[1024];
    var rng = new Random(42);
    rng.NextBytes(data);
    // Sprinkle in some CALL/JMP instructions
    for (int i = 0; i < data.Length - 5; i += 20) {
      data[i] = 0xE8;
      data[i + 1] = (byte)(i & 0xFF);
      data[i + 2] = (byte)((i >> 8) & 0xFF);
      data[i + 3] = 0;
      data[i + 4] = 0;
    }

    var preFilters = new List<(ulong, byte[])> {
      (XzConstants.FilterBcjX86, [])
    };

    byte[] compressed = CompressWithFilters(data, preFilters);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithDeltaFilter() {
    // Slowly varying data — ideal for delta filter
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i / 3);

    var preFilters = new List<(ulong, byte[])> {
      (XzConstants.FilterDelta, [0]) // distance = 0 + 1 = 1
    };

    byte[] compressed = CompressWithFilters(data, preFilters);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_NoFilters_StillWorks() {
    byte[] data = "Hello, XZ filter chain!"u8.ToArray();
    byte[] compressed = CompressWithFilters(data, []);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  private static byte[] CompressWithFilters(byte[] data,
    List<(ulong FilterId, byte[] Properties)> preFilters) {
    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, XzConstants.CheckCrc64, preFilters, leaveOpen: true)) {
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
