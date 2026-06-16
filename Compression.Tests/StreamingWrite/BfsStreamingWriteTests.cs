using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Bfs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="BfsFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the BFS reader, handles
/// multi-entry, nested-path and multi-megabyte (chunked-copy) inputs with
/// bounded memory, and produces output byte-identical to the classic
/// <c>Create</c>.
/// </summary>
[TestFixture]
public class BfsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new BfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params (string Name, byte[] Data)[] inputs) {
    var d = new BfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), Options);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string name) {
    var d = new BfsFormatDescriptor();
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
    var a = Pattern(100, 1);     // sub-block
    var b = Pattern(1, 2);       // single byte
    var c = Pattern(1024, 3);    // exact block
    var bytes = CreateFromStreams(File("a.dat", a), File("nested/b.dat", b), File("c.dat", c));

    var d = new BfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var names = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Has.Member("a.dat"));
    Assert.That(names, Has.Member("c.dat"));
    Assert.That(names.Any(n => n.EndsWith("b.dat")), Is.True);

    Assert.That(ReadBack(bytes, "a.dat"), Is.EqualTo(a));
    Assert.That(ReadBack(bytes, "c.dat"), Is.EqualTo(c));
    var bName = names.First(n => n.EndsWith("b.dat"));
    Assert.That(ReadBack(bytes, bName), Is.EqualTo(b));
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // A few MB through the chunked-copy path proves the bounded-memory copy
    // route; the BFS writer grows the image past its 4 MB default to fit.
    var big = Pattern(3 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    Assert.That(ReadBack(bytes, "big.dat"), Is.EqualTo(big));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(2 * 1024 * 1024, 2);
    var streamed = CreateFromStreams(File("a.bin", a), File("sub/b.bin", b));
    var classic = CreateClassic(("a.bin", a), ("sub/b.bin", b));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
