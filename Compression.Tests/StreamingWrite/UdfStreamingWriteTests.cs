using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Udf;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="UdfFormatDescriptor.CreateFromStreams"/>.
/// UDF descriptor CRCs cover only the descriptor tag bytes (FID / File Entry /
/// VDS), never file data, and the writer emits sectors strictly forward in LBN
/// order — so file bodies stream straight into the sequential output. The
/// writer embeds no random UUID and no wall-clock timestamp, so the streaming
/// output is byte-for-byte identical to the classic <c>WriteTo</c>.
/// </summary>
[TestFixture]
public class UdfStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private const int BigSize = 3 * 1024 * 1024;

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new UdfFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params (string Name, byte[] Data)[] files) {
    var w = new UdfWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string name) {
    var d = new UdfFormatDescriptor();
    using var ms = new MemoryStream(image);
    return d.ExtractEntryToMemory(ms, name, null);
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.bin", data));
    Assert.That(ReadBack(bytes, "hello.bin"), Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(2048, 2);   // exactly one sector
    var c = Pattern(5000, 3);   // spans three sectors
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new UdfFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(list, Is.EquivalentTo(new[] { "a.bin", "b.bin", "c.bin" }));

    Assert.That(ReadBack(bytes, "a.bin"), Is.EqualTo(a));
    Assert.That(ReadBack(bytes, "b.bin"), Is.EqualTo(b));
    Assert.That(ReadBack(bytes, "c.bin"), Is.EqualTo(c));
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    var big = Pattern(BigSize, 42);
    var bytes = CreateFromStreams(File("big.dat", big));
    Assert.That(ReadBack(bytes, "big.dat"), Is.EqualTo(big));
  }

  [Test]
  public void TinyAndLargeCoexist_RoundTrip() {
    var tiny = Pattern(9, 11);
    var large = Pattern(BigSize, 12);
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));
    Assert.That(ReadBack(bytes, "tiny.txt"), Is.EqualTo(tiny));
    Assert.That(ReadBack(bytes, "large.bin"), Is.EqualTo(large));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(BigSize, 2);
    // UDF is fully deterministic — no UUID, no timestamp — so the streaming
    // output must equal the classic build byte-for-byte.
    var classic = CreateClassic(("a.bin", a), ("dir/b.bin", b));
    var streamed = CreateFromStreams(File("a.bin", a), File("dir/b.bin", b));
    Assert.That(streamed, Is.EqualTo(classic));
  }
}
