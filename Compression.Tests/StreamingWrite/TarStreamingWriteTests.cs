using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Tar;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="TarFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips, stays readable by the TAR reader,
/// handles multi-MB / multi-entry / directory inputs with bounded memory, and
/// produces output byte-identical to the classic <c>Create</c>.
/// </summary>
[TestFixture]
public class TarStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params ArchiveInputInfo[] inputs) {
    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, Options);
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.bin", data));

    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "hello.bin", null);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(1, 2);     // boundary: 1 byte (511 bytes of padding)
    var c = Pattern(512, 3);   // boundary: exact block, no padding
    var bytes = CreateFromStreams(File("a", a), File("b", b), File("c", c));

    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "a", "b", "c" }));

    foreach (var (name, expected) in new[] { ("a", a), ("b", b), ("c", c) }) {
      ms.Position = 0;
      Assert.That(d.ExtractEntryToMemory(ms, name, null), Is.EqualTo(expected), name);
    }
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // A few MB through the chunked-copy path proves the bounded-memory copy
    // route without allocating gigabytes.
    var big = Pattern(5 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "big.dat", null);
    Assert.That(got, Is.EqualTo(big));
  }

  [Test]
  public void DirectoryEntry_IsPreserved() {
    var bytes = CreateFromStreams(Dir("subdir/"), File("subdir/file.txt", Pattern(64)));

    var d = new TarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null);
    Assert.That(list.Any(e => e.IsDirectory && e.Name.StartsWith("subdir")), Is.True);
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(3 * 1024 * 1024, 2);
    var streamed = CreateFromStreams(Dir("d/"), File("a.bin", a), File("d/b.bin", b));
    var classic = CreateClassic(
      new ArchiveInputInfo("d/", "d/", IsDirectory: true),
      ArchiveInputInfo.InMemory("a.bin", a),
      ArchiveInputInfo.InMemory("d/b.bin", b));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
