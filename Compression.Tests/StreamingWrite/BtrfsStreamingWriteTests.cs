using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Btrfs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="BtrfsFormatDescriptor.CreateFromStreams"/>:
/// the two-pass streaming write round-trips through the Btrfs reader, handles
/// multi-MB regular-extent files via the bounded chunked-copy path, lets a tiny
/// (inline) and a large (regular-extent) file coexist, and produces output
/// byte-identical to the classic <c>WriteTo</c> for the same inputs.
/// <para>
/// Byte-identity is asserted as full-image equality: the Btrfs writer embeds a
/// fixed FS-UUID and uses hard-coded generations/transids with no wall-clock
/// timestamps, so its output is fully deterministic and a literal comparison
/// holds. Files smaller than one sector (<c>MaxInlineDataSize</c> = 4096) are
/// stored inline in the FS-tree metadata leaf; files at or above the threshold
/// are streamed into their DATA-chunk extent (which carries no checksum — the
/// inode is NODATASUM), so streaming cannot diverge from the classic bytes.
/// </para>
/// </summary>
[TestFixture]
public class BtrfsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  // Sector size = inline threshold. Files below this go inline; at/above stream.
  private const int InlineThreshold = 4096;

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new BtrfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  // Classic baseline built straight through the writer (the descriptor's
  // Create reads from disk paths; the writer's AddFile + WriteTo produces the
  // same deterministic bytes the streaming path must match).
  private static byte[] CreateClassic(params (string Name, byte[] Data)[] files) {
    var w = new BtrfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ExtractOne(byte[] image, string name) {
    var d = new BtrfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    return d.ExtractEntryToMemory(ms, name, null);
  }

  [Test, Category("RoundTrip")]
  public void SingleEntry_RoundTrips() {
    // 8 KiB > inline threshold => exercises the streamed regular-extent path.
    var data = Pattern(8 * 1024);
    var bytes = CreateFromStreams(File("hello.bin", data));

    Assert.That(ExtractOne(bytes, "hello.bin"), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(8 * 1024, 1);      // streamed extent
    var b = Pattern(16 * 1024 + 7, 2); // streamed extent, non-sector-multiple tail
    var c = Pattern(4096, 3);          // exactly threshold => streamed extent
    var bytes = CreateFromStreams(File("a.bin", a), File("b.bin", b), File("c.bin", c));

    var d = new BtrfsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(list, Is.EquivalentTo(new[] { "a.bin", "b.bin", "c.bin" }));

    Assert.That(ExtractOne(bytes, "a.bin"), Is.EqualTo(a));
    Assert.That(ExtractOne(bytes, "b.bin"), Is.EqualTo(b));
    Assert.That(ExtractOne(bytes, "c.bin"), Is.EqualTo(c));
  }

  [Test, Category("RoundTrip")]
  public void MultiMegabyteEntry_StreamsAndRoundTrips() {
    // 3 MiB through the 64 KiB chunked-copy route — far above the inline
    // threshold, so it lands in the DATA chunk as a real streamed extent.
    var big = Pattern(3 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    Assert.That(ExtractOne(bytes, "big.dat"), Is.EqualTo(big));
  }

  [Test, Category("RoundTrip")]
  public void TinyInline_AndLargeExtent_Coexist() {
    var tiny = Pattern(100, 7);                 // < threshold => inline metadata leaf
    var large = Pattern(32 * 1024, 8);          // >= threshold => streamed extent
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));

    Assert.That(tiny.Length, Is.LessThan(InlineThreshold));
    Assert.That(large.Length, Is.GreaterThanOrEqualTo(InlineThreshold));
    Assert.That(ExtractOne(bytes, "tiny.txt"), Is.EqualTo(tiny));
    Assert.That(ExtractOne(bytes, "large.bin"), Is.EqualTo(large));
  }

  [Test, Category("RoundTrip")]
  public void StreamingOutput_EqualsClassicCreate() {
    // Mix an inline file with a multi-MB streamed extent. Btrfs output is fully
    // deterministic (fixed UUID, no timestamps), so the streamed image must be
    // byte-for-byte identical to the classic WriteTo image.
    var inline = Pattern(200, 1);
    var extent = Pattern(3 * 1024 * 1024, 2);
    var streamed = CreateFromStreams(File("a.txt", inline), File("b.bin", extent));
    var classic = CreateClassic(("a.txt", inline), ("b.bin", extent));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
