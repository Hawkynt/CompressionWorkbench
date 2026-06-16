using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Zip;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="ZipFormatDescriptor.CreateFromStreams"/>.
/// STORE entries take the real streaming path (header up front, CRC patched
/// after a chunked copy); compressing methods fall back to buffering and are
/// only checked for correctness, not bounded memory.
/// </summary>
[TestFixture]
public class ZipStreamingWriteTests {
  private static FormatCreateOptions StoreOptions => new() { MethodName = "store" };

  private static byte[] CreateFromStreams(FormatCreateOptions options, params StreamingArchiveInput[] inputs) {
    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(FormatCreateOptions options, params ArchiveInputInfo[] inputs) {
    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, options);
    return ms.ToArray();
  }

  [Test]
  public void Store_SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(StoreOptions, File("hello.bin", data));

    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "hello.bin", null), Is.EqualTo(data));
  }

  [Test]
  public void Store_MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(0, 2);   // empty entry
    var c = Pattern(513, 3);
    var bytes = CreateFromStreams(StoreOptions, File("a", a), File("b", b), File("c", c));

    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EquivalentTo(new[] { "a", "b", "c" }));

    foreach (var (name, expected) in new[] { ("a", a), ("b", b), ("c", c) }) {
      ms.Position = 0;
      Assert.That(d.ExtractEntryToMemory(ms, name, null), Is.EqualTo(expected), name);
    }
  }

  [Test]
  public void Store_MultiMegabyteEntry_StreamsAndRoundTrips() {
    var big = Pattern(5 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(StoreOptions, File("big.dat", big));

    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "big.dat", null), Is.EqualTo(big));
  }

  [Test]
  public void Store_DirectoryEntry_IsPreserved() {
    var bytes = CreateFromStreams(StoreOptions, Dir("subdir/"), File("subdir/file.txt", Pattern(64)));

    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Any(e => e.IsDirectory && e.Name.StartsWith("subdir")), Is.True);
  }

  [Test]
  public void Store_StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(3 * 1024 * 1024, 2);
    var streamed = CreateFromStreams(StoreOptions, Dir("d/"), File("a.bin", a), File("d/b.bin", b));
    var classic = CreateClassic(StoreOptions,
      new ArchiveInputInfo("d/", "d/", IsDirectory: true),
      ArchiveInputInfo.InMemory("a.bin", a),
      ArchiveInputInfo.InMemory("d/b.bin", b));

    Assert.That(streamed, Is.EqualTo(classic));
  }

  [Test]
  public void Deflate_FallsBackToBuffering_ButStillRoundTrips() {
    // DEFLATE keeps the buffering default; correctness must still hold.
    var data = Pattern(64 * 1024, 7);
    var bytes = CreateFromStreams(new FormatCreateOptions { MethodName = "deflate" },
      File("payload.bin", data));

    var d = new ZipFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "payload.bin", null), Is.EqualTo(data));
  }
}
