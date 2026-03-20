using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Gzip;

namespace Compression.Tests.Gzip;

[TestFixture]
public class GzipStreamTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallText() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 200];
    for (int i = 0; i < 200; ++i)
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
    byte[] data = new byte[8192];
    rng.NextBytes(data);

    byte[] compressed = CompressWithOurs(data);
    byte[] result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void CrcVerification_CorruptedData_Throws() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = CompressWithOurs(data);

    // Corrupt the CRC in the trailer (last 8 bytes: CRC32 + ISIZE)
    compressed[^8] ^= 0xFF; // Flip a CRC byte

    Assert.Throws<InvalidDataException>(() => DecompressWithOurs(compressed));
  }

  [Category("Exception")]
  [Test]
  public void SizeVerification_CorruptedSize_Throws() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = CompressWithOurs(data);

    // Corrupt the ISIZE in the trailer
    compressed[^1] ^= 0xFF;

    Assert.Throws<InvalidDataException>(() => DecompressWithOurs(compressed));
  }

  [Category("HappyPath")]
  [Test]
  public void LeaveOpen_True_InnerStreamRemains() {
    byte[] data = "test"u8.ToArray();
    using var ms = new MemoryStream();

    using (var gz = new GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true)) {
      gz.Write(data, 0, data.Length);
    }

    // Stream should still be accessible
    Assert.That(ms.Length, Is.GreaterThan(0));
    Assert.DoesNotThrow(() => _ = ms.Position);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithHeader() {
    byte[] data = "Hello!"u8.ToArray();
    using var ms = new MemoryStream();

    using (var gz = new GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true)) {
      gz.Header = new GzipHeader {
        FileName = "hello.txt",
        Comment = "test file"
      };
      gz.Write(data, 0, data.Length);
    }

    ms.Position = 0;
    using var gz2 = new GzipStream(ms, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    gz2.CopyTo(output);

    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  // ── Multi-member tests ───────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MultiMember_TwoMembers_DecompressesAll() {
    byte[] part1 = "Hello, "u8.ToArray();
    byte[] part2 = "World!"u8.ToArray();

    byte[] member1 = CompressWithOurs(part1);
    byte[] member2 = CompressWithOurs(part2);

    // Concatenate two gzip members
    byte[] combined = new byte[member1.Length + member2.Length];
    member1.CopyTo(combined, 0);
    member2.CopyTo(combined, member1.Length);

    byte[] result = DecompressWithOurs(combined);
    byte[] expected = new byte[part1.Length + part2.Length];
    part1.CopyTo(expected, 0);
    part2.CopyTo(expected, part1.Length);

    Assert.That(result, Is.EqualTo(expected));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MultiMember_ThreeMembers_DecompressesAll() {
    byte[] p1 = "A"u8.ToArray();
    byte[] p2 = "B"u8.ToArray();
    byte[] p3 = "C"u8.ToArray();

    using var ms = new MemoryStream();
    ms.Write(CompressWithOurs(p1));
    ms.Write(CompressWithOurs(p2));
    ms.Write(CompressWithOurs(p3));

    byte[] result = DecompressWithOurs(ms.ToArray());
    Assert.That(result, Is.EqualTo("ABC"u8.ToArray()));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void MultiMember_EmptyMember_IsHandled() {
    byte[] empty = CompressWithOurs([]);
    byte[] data = CompressWithOurs("data"u8.ToArray());

    using var ms = new MemoryStream();
    ms.Write(data);
    ms.Write(empty);

    // The second member is empty — total result should be "data"
    byte[] result = DecompressWithOurs(ms.ToArray());
    Assert.That(result, Is.EqualTo("data"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SingleMember_StillWorks() {
    // Ensure single-member gzip still works
    byte[] data = "single member"u8.ToArray();
    byte[] compressed = CompressWithOurs(data);
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
}
