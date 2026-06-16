using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.MinixFs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="MinixFsFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the Minix v3 reader, handles
/// multi-entry and multi-zone inputs with bounded memory, and produces output
/// byte-identical to the classic writer's <c>Finish</c>. Minix has no data
/// checksums, so post-streaming the data zones is byte-safe.
/// <para>
/// Note: the Minix v3 writer caps a regular file at 7 direct zones
/// (7168 bytes), so the "large file" case uses a multi-zone-but-bounded file
/// (7000 bytes) instead of multiple megabytes — the streaming copy route is
/// identical, only the ceiling differs.
/// </para>
/// </summary>
[TestFixture]
public class MinixFsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new MinixFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  // Classic baseline built directly via the writer's buffered AddFile path.
  private static byte[] BuildClassic(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new MinixFsWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(1024);
    var bytes = CreateFromStreams(File("hello.bin", data));

    var d = new MinixFsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "hello.bin", null);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(1025, 2);    // boundary: spills into a second 1 KiB zone
    var c = Pattern(1024, 3);    // boundary: exact zone
    var bytes = CreateFromStreams(File("a", a), File("b", b), File("c", c));

    var d = new MinixFsFormatDescriptor();
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
    // Minix v3 caps a file at 7 direct zones (7168 bytes). A 7000-byte file
    // spans 7 zones and exercises the multi-zone chunked-copy streaming route.
    var big = Pattern(7000, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new MinixFsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "big.dat", null);
    Assert.That(got, Is.EqualTo(big));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(7000, 2);
    var streamed = CreateFromStreams(File("a.bin", a), File("d/b.bin", b));
    var classic = BuildClassic(("a.bin", a), ("d/b.bin", b));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
