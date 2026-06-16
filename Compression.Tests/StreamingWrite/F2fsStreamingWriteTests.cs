using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.F2fs;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="F2fsFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the F2FS reader, handles
/// multi-entry and multi-megabyte (data-block) inputs with bounded memory, and
/// produces output byte-identical to the classic <c>Create</c>. F2FS never
/// stores file contents inline (only directory dentries are inline), so every
/// file — tiny or large — flows through the WARM_DATA data-block streaming path.
/// </summary>
[TestFixture]
public class F2fsStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new F2fsFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params ArchiveInputInfo[] inputs) {
    var d = new F2fsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, Options);
    return ms.ToArray();
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("hello.bin", data));

    var d = new F2fsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "hello.bin", null);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(100, 1);
    var b = Pattern(4097, 2);    // boundary: spills into a second 4 KiB block
    var c = Pattern(4096, 3);    // boundary: exact block
    var bytes = CreateFromStreams(File("a", a), File("b", b), File("c", c));

    var d = new F2fsFormatDescriptor();
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
    // 3 MB through the chunked-copy path proves the bounded-memory data-block
    // streaming route (F2FS direct-pointer ceiling is ~3.6 MB per file).
    var big = Pattern(3 * 1024 * 1024, 42);
    var bytes = CreateFromStreams(File("big.dat", big));

    var d = new F2fsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var got = d.ExtractEntryToMemory(ms, "big.dat", null);
    Assert.That(got, Is.EqualTo(big));
  }

  [Test]
  public void TinyAndLarge_CoexistAndRoundTrip() {
    // F2FS stores all file contents in data blocks (never inline), so a tiny
    // file and a large file both exercise the same data-block streaming path.
    var tiny = Pattern(12, 7);
    var large = Pattern(2 * 1024 * 1024, 8);
    var bytes = CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large));

    var d = new F2fsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    Assert.That(d.ExtractEntryToMemory(ms, "tiny.txt", null), Is.EqualTo(tiny));
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "large.bin", null), Is.EqualTo(large));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var tiny = Pattern(12, 7);
    var large = Pattern(2 * 1024 * 1024, 8);

    // The F2FS superblock embeds a random UUID and the image carries wall-clock
    // timestamps, so two independent builds differ only in those fields. Diffing
    // two classic builds isolates exactly those nondeterministic bytes; the
    // streaming output must match a classic build on EVERY other byte — proving
    // all structural metadata and all file-data placement are byte-identical.
    AssertEqualOutsideNondeterministicFields(
      () => CreateFromStreams(File("tiny.txt", tiny), File("large.bin", large)),
      () => CreateClassic(ArchiveInputInfo.InMemory("tiny.txt", tiny), ArchiveInputInfo.InMemory("large.bin", large)));
  }

  // Asserts the streaming output equals a classic build on every byte index
  // where two classic builds agree — i.e. everywhere except the format-mandated
  // random UUID + wall-clock timestamp fields. A short retry window absorbs the
  // rare case where the streaming and classic builds straddle a one-second
  // boundary (which would flip a timestamp byte the two classic builds shared).
  internal static void AssertEqualOutsideNondeterministicFields(Func<byte[]> buildStreamed, Func<byte[]> buildClassic) {
    var lastMismatches = -1;
    var lastLenA = 0;
    var lastLenStreamed = 0;
    for (var attempt = 0; attempt < 6; attempt++) {
      var classicA = buildClassic();
      var classicB = buildClassic();
      var streamed = buildStreamed();
      lastLenA = classicA.Length;
      lastLenStreamed = streamed.Length;
      if (streamed.Length != classicA.Length || classicB.Length != classicA.Length) {
        lastMismatches = int.MaxValue;
        continue;
      }
      var mismatches = 0;
      for (var i = 0; i < classicA.Length; i++) {
        if (classicA[i] != classicB[i]) continue; // nondeterministic byte (UUID/timestamp) — skip
        if (streamed[i] != classicA[i]) mismatches++;
      }
      lastMismatches = mismatches;
      if (mismatches == 0) return;
    }
    Assert.That(lastLenStreamed, Is.EqualTo(lastLenA), "streamed vs classic image length");
    Assert.That(lastMismatches, Is.Zero,
      $"streaming output differs from classic on {lastMismatches} deterministic byte(s) across 6 attempts (data placement / structural metadata must be byte-identical).");
  }
}
