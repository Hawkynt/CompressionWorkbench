using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Jfs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="JfsFormatDescriptor.CreateFromStreams"/>.
/// JFS carries no data checksum and stores file bodies in dedicated xtree
/// extents, so the streaming write path is byte-identical to the classic
/// <c>WriteTo</c> on every structural byte (the volume/log UUIDs and the
/// write timestamp are the only format-mandated nondeterministic ranges).
/// </summary>
[TestFixture]
public class JfsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  // Above the multi-MB streaming threshold but inside the writer's single-IAG /
  // two-dmap ceiling (≤ 64 MB image). 3 MB sits comfortably under that.
  private const int BigSize = 3 * 1024 * 1024;

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new JfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  /// <summary>Classic build via the writer over buffered AddFile inputs.</summary>
  private static byte[] CreateClassic(params (string Name, byte[] Data)[] files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string name) {
    var d = new JfsFormatDescriptor();
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
    var b = Pattern(4096, 2);   // exactly one block
    var c = Pattern(9000, 3);   // spans three blocks
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new JfsFormatDescriptor();
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
    var tiny = Pattern(7, 11);            // single-block tiny file
    var large = Pattern(BigSize, 12);     // multi-block extent-backed file
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));
    Assert.That(ReadBack(bytes, "tiny.txt"), Is.EqualTo(tiny));
    Assert.That(ReadBack(bytes, "large.bin"), Is.EqualTo(large));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(BigSize, 2);
    (string, byte[])[] files = [("a.bin", a), ("dir/b.bin", b)];

    // The volume/log UUIDs (random) and the write timestamp (wall clock) are
    // the only format-mandated nondeterministic bytes. Diff two classic builds
    // to find them, then assert the streaming build matches a classic build on
    // EVERY OTHER byte — proving all structure + data placement is identical.
    StreamingByteIdentity.AssertMatchesClassic(
      () => CreateClassic(files),
      () => CreateFromStreams(File("a.bin", a), File("dir/b.bin", b)));
  }
}
