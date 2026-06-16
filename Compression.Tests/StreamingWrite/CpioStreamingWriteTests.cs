using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Cpio;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="CpioFormatDescriptor.CreateFromStreams"/>.
/// </summary>
[TestFixture]
public class CpioStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new CpioFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.bin", data));

    var d = new CpioFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "hello.bin", null), Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(3, 2);   // boundary: non-multiple of 4 → padding
    var c = Pattern(256, 3); // boundary: multiple of 4 → no data padding
    var bytes = CreateFromStreams(File("a", a), File("b", b), File("c", c));

    var d = new CpioFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EquivalentTo(new[] { "a", "b", "c" }));

    foreach (var (name, expected) in new[] { ("a", a), ("b", b), ("c", c) }) {
      ms.Position = 0;
      Assert.That(d.ExtractEntryToMemory(ms, name, null), Is.EqualTo(expected), name);
    }
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    var big = Pattern(5 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new CpioFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "big.dat", null), Is.EqualTo(big));
  }

  [Test]
  public void DirectoryEntry_IsPreserved() {
    var bytes = CreateFromStreams(Dir("subdir"), File("subdir/file.txt", Pattern(64)));

    var d = new CpioFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Any(e => e.IsDirectory), Is.True);
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    // CpioFormatDescriptor.Create reads file payloads from disk, so stage the
    // inputs as real files and compare the on-disk classic output to the
    // streamed output byte-for-byte.
    var dir = Path.Combine(Path.GetTempPath(), "cpio_stream_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var a = Pattern(777, 1);
      var b = Pattern(2 * 1024 * 1024, 2);
      var pa = Path.Combine(dir, "a.bin");
      var pb = Path.Combine(dir, "b.bin");
      System.IO.File.WriteAllBytes(pa, a);
      System.IO.File.WriteAllBytes(pb, b);

      var d = new CpioFormatDescriptor();
      using var classicMs = new MemoryStream();
      d.Create(classicMs, [
        new ArchiveInputInfo("dir", "dir", IsDirectory: true),
        new ArchiveInputInfo(pa, "a.bin", IsDirectory: false),
        new ArchiveInputInfo(pb, "b.bin", IsDirectory: false),
      ], Options);

      var streamed = CreateFromStreams(Dir("dir"), File("a.bin", a), File("b.bin", b));
      Assert.That(streamed, Is.EqualTo(classicMs.ToArray()));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
