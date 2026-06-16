using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Ar;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="ArFormatDescriptor.CreateFromStreams"/>.
/// </summary>
[TestFixture]
public class ArStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params ArchiveInputInfo[] inputs) {
    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, Options);
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.o", data));

    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "hello.o", null), Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(3, 2);   // odd length → padding byte
    var c = Pattern(64, 3);
    var bytes = CreateFromStreams(File("a.o", a), File("b.o", b), File("c.o", c));

    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EquivalentTo(new[] { "a.o", "b.o", "c.o" }));

    foreach (var (name, expected) in new[] { ("a.o", a), ("b.o", b), ("c.o", c) }) {
      ms.Position = 0;
      Assert.That(d.ExtractEntryToMemory(ms, name, null), Is.EqualTo(expected), name);
    }
  }

  [Test]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    var big = Pattern(5 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "big.dat", null), Is.EqualTo(big));
  }

  [Test]
  public void LongNameEntry_UsesGnuStringTable() {
    var longName = new string('x', 40) + ".o"; // exceeds the 16-char inline limit
    var data = Pattern(128);
    var bytes = CreateFromStreams(File(longName, data));

    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, longName, null), Is.EqualTo(data));
  }

  [Test]
  public void DirectoryInput_IsSkipped_LikeClassic() {
    // AR has no directory concept; a directory input must be dropped exactly
    // as Create does, leaving only the file entry.
    var data = Pattern(64);
    var bytes = CreateFromStreams(Dir("ignored/"), File("real.o", data));

    var d = new ArFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "real.o" }));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(777, 1);
    var b = Pattern(3 * 1024 * 1024, 2);
    var longName = new string('y', 30) + ".o";
    var streamed = CreateFromStreams(
      Dir("skip/"), File("a.o", a), File(longName, b));
    var classic = CreateClassic(
      new ArchiveInputInfo("skip/", "skip/", IsDirectory: true),
      ArchiveInputInfo.InMemory("a.o", a),
      ArchiveInputInfo.InMemory(longName, b));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
