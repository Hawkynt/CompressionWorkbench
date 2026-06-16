using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.ReiserFs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="ReiserFsFormatDescriptor.CreateFromStreams"/>.
/// ReiserFS v3.6 has NO block checksums by design, so bodies above the 1 KiB
/// DIRECT/tail threshold stream into dedicated INDIRECT data blocks while small
/// bodies stay tail-packed in shared leaves. The streaming write is therefore
/// byte-identical to the classic <c>WriteTo</c> on every structural byte (the
/// random journal magic + UUID and the stat-data timestamps are the only
/// format-mandated nondeterministic ranges).
/// </summary>
[TestFixture]
public class ReiserFsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  // Above the 1 KiB DIRECT threshold → INDIRECT block-extent streaming path.
  private const int BigSize = 3 * 1024 * 1024;

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new ReiserFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params (string Name, byte[] Data)[] files) {
    var w = new ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string name) {
    var d = new ReiserFsFormatDescriptor();
    using var ms = new MemoryStream(image);
    return d.ExtractEntryToMemory(ms, name, null);
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(64 * 1024);  // INDIRECT (well above the 1 KiB tail cap)
    var bytes = CreateFromStreams(File("hello.bin", data));
    Assert.That(ReadBack(bytes, "hello.bin"), Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(64, 1);          // DIRECT (tail-packed, read up front)
    var b = Pattern(4096, 2);        // INDIRECT (one block)
    var c = Pattern(9000, 3);        // INDIRECT (three blocks, partial tail)
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new ReiserFsFormatDescriptor();
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
    var tiny = Pattern(40, 11);       // tail-packed DIRECT (inline in a leaf)
    var large = Pattern(BigSize, 12); // INDIRECT, dedicated data blocks
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));
    Assert.That(ReadBack(bytes, "tiny.txt"), Is.EqualTo(tiny));
    Assert.That(ReadBack(bytes, "large.bin"), Is.EqualTo(large));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var tiny = Pattern(40, 1);
    var big = Pattern(BigSize, 2);
    (string, byte[])[] files = [("tiny.txt", tiny), ("dir/big.bin", big)];

    StreamingByteIdentity.AssertMatchesClassic(
      () => CreateClassic(files),
      () => CreateFromStreams(File("tiny.txt", tiny), File("dir/big.bin", big)));
  }
}
