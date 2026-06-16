using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Xfs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="XfsFormatDescriptor.CreateFromStreams"/>:
/// the two-pass streaming write round-trips through the XFS reader, handles
/// multi-MB files via the bounded chunked-copy path, lets small and large files
/// coexist, and produces output byte-identical to the classic <c>WriteTo</c>.
/// <para>
/// XFS stores every regular file as a data extent (there is no inline file
/// form), and file data carries no CRC (only metadata/dir blocks are
/// checksummed). The writer embeds a fixed volume UUID and writes no wall-clock
/// timestamps, so its output is fully deterministic — byte-identity is asserted
/// as a literal full-image comparison between the streamed and classic builds.
/// All content must fit a single allocation group (≈16 MiB: 4096 blocks ×
/// 4096 B minus per-AG metadata and the 64-block log), so the multi-MB case
/// stays at 3 MiB.
/// </para>
/// </summary>
[TestFixture]
public class XfsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new XfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  // Classic baseline straight through the writer (the descriptor's Create reads
  // from disk paths; AddFile + WriteTo produces the same deterministic bytes).
  private static byte[] CreateClassic(params (string Name, byte[] Data)[] files) {
    var w = new XfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ExtractOne(byte[] image, string name) {
    var d = new XfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    return d.ExtractEntryToMemory(ms, name, null);
  }

  [Test, Category("RoundTrip")]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(8 * 1024);
    var bytes = CreateFromStreams(File("hello.bin", data));

    Assert.That(ExtractOne(bytes, "hello.bin"), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);            // < one block
    var b = Pattern(4096, 2);           // exactly one block, no tail
    var c = Pattern(9000, 3);           // spans blocks, non-block-multiple tail
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new XfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(list, Is.EquivalentTo(new[] { "a.bin", "b.bin", "c.bin" }));

    Assert.That(ExtractOne(bytes, "a.bin"), Is.EqualTo(a));
    Assert.That(ExtractOne(bytes, "b.bin"), Is.EqualTo(b));
    Assert.That(ExtractOne(bytes, "c.bin"), Is.EqualTo(c));
  }

  [Test, Category("RoundTrip")]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // 3 MiB through the 64 KiB chunked-copy route; well under the one-AG ceiling.
    var big = Pattern(3 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    Assert.That(ExtractOne(bytes, "big.dat"), Is.EqualTo(big));
  }

  [Test, Category("RoundTrip")]
  public void TinyAndLarge_Coexist() {
    var tiny = Pattern(50, 7);                  // sub-block file
    var large = Pattern(2 * 1024 * 1024, 8);    // multi-block streamed extent
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));

    Assert.That(ExtractOne(bytes, "tiny.txt"), Is.EqualTo(tiny));
    Assert.That(ExtractOne(bytes, "large.bin"), Is.EqualTo(large));
  }

  [Test, Category("RoundTrip")]
  public void StreamingOutput_EqualsClassicCreate() {
    // XFS output is fully deterministic (fixed UUID, zero timestamps), so the
    // streamed image must be byte-for-byte identical to the classic WriteTo image.
    var small = Pattern(777, 1);
    var big = Pattern(3 * 1024 * 1024, 2);
    var streamed = CreateFromStreams(File("a.bin", small), File("b.bin", big));
    var classic = CreateClassic(("a.bin", small), ("b.bin", big));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
