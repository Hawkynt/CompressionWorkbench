using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Ufs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="UfsFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the UFS1 reader, handles
/// multi-entry and multi-megabyte (direct + single-indirect) inputs with
/// bounded memory, and produces output byte-identical to the classic
/// writer's <c>WriteTo</c>. The UFS <c>cs</c> records are cylinder-group
/// free-space summaries, not content checksums, so post-streaming the data
/// fragments is byte-safe.
/// </summary>
[TestFixture]
public class UfsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new UfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  // Classic baseline built directly via the writer's buffered AddFile path
  // (the descriptor's Create reads from disk paths, so the writer is the
  // apples-to-apples buffered equivalent of the streaming inputs).
  private static byte[] BuildClassic(params (string Name, byte[] Data)[] files) {
    var w = new UfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.bin", data));

    var d = new UfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "hello.bin", null);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(8193, 2);    // boundary: spills into a second 8 KiB block
    var c = Pattern(8192, 3);    // boundary: exact block
    var bytes = CreateFromStreams(File("a", a), File("b", b), File("c", c));

    var d = new UfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(list, Is.EquivalentTo(new[] { "a", "b", "c" }));

    foreach (var (name, expected) in new[] { ("a", a), ("b", b), ("c", c) }) {
      ms.Position = 0;
      Assert.That(d.ExtractEntryToMemory(ms, name, null), Is.EqualTo(expected), name);
    }
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // 4 MB exceeds the 12 direct blocks (96 KB) and so exercises the
    // single-indirect block path through the chunked-copy route.
    var big = Pattern(4 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new UfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "big.dat", null);
    Assert.That(got, Is.EqualTo(big));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(3 * 1024 * 1024, 2);

    // The UFS superblock embeds a random volume UUID and the image carries
    // wall-clock timestamps, so two independent builds differ only in those
    // fields. Diffing two classic builds isolates exactly those bytes; the
    // streaming output must match a classic build on EVERY other byte — proving
    // all structural metadata and all file-data placement are byte-identical.
    F2fsStreamingWriteTests.AssertEqualOutsideNondeterministicFields(
      () => CreateFromStreams(File("a.bin", a), File("d/b.bin", b)),
      () => BuildClassic(("a.bin", a), ("d/b.bin", b)));
  }
}
