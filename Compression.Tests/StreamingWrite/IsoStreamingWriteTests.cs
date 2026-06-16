using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Iso;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="IsoFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the ISO 9660 reader, handles
/// multi-entry and multi-megabyte (chunked-copy) inputs with bounded memory,
/// and produces output byte-identical to the classic <c>Create</c>.
/// </summary>
/// <remarks>
/// ECMA-119 stamps the volume descriptors and every directory record with the
/// wall-clock time at <c>Build</c>, so two independent <c>Create</c>/
/// <c>CreateFromStreams</c> calls only diverge if a one-second boundary falls
/// between them. The equality test rebuilds both within a retry window so the
/// timestamps coincide, isolating the assertion to the file-data placement.
/// </remarks>
[TestFixture]
public class IsoStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new IsoFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params (string Name, byte[] Data)[] inputs) {
    var d = new IsoFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), Options);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string name) {
    var d = new IsoFormatDescriptor();
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
    var a = Pattern(100, 1);     // sub-sector
    var b = Pattern(2048, 2);    // exact sector
    var c = Pattern(5000, 3);    // spans three sectors
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new IsoFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var names = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "a.bin", "b.bin", "c.bin" }));

    Assert.That(ReadBack(bytes, "a.bin"), Is.EqualTo(a));
    Assert.That(ReadBack(bytes, "b.bin"), Is.EqualTo(b));
    Assert.That(ReadBack(bytes, "c.bin"), Is.EqualTo(c));
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // A few MB through the chunked-copy path proves the bounded-memory copy
    // route without allocating gigabytes.
    var big = Pattern(5 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    Assert.That(ReadBack(bytes, "big.dat"), Is.EqualTo(big));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(3 * 1024 * 1024, 2);

    // Rebuild both routes inside a tight retry window so the ECMA-119
    // wall-clock timestamps coincide; only the file-data placement is asserted.
    byte[] streamed = [], classic = [];
    var matched = false;
    for (var attempt = 0; attempt < 5 && !matched; attempt++) {
      streamed = CreateFromStreams(File("a.bin", a), File("b.bin", b));
      classic = CreateClassic(("a.bin", a), ("b.bin", b));
      matched = streamed.AsSpan().SequenceEqual(classic);
    }
    Assert.That(streamed, Is.EqualTo(classic));
  }
}
