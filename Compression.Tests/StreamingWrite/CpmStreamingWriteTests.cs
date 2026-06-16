using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Cpm;
using static Compression.Tests.StreamingWrite.StreamingWriteTestHelpers;

namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Given-when-then coverage for <see cref="CpmFormatDescriptor.CreateFromStreams"/>:
/// the streaming write path round-trips through the CP/M reader, handles
/// multi-entry and large (chunked-copy) inputs with bounded memory, and
/// produces output byte-identical to the classic <c>Create</c>.
/// </summary>
/// <remarks>
/// CP/M's canonical 8" SSSD geometry caps the volume at 241 × 1024-byte data
/// blocks (~241 KiB), so the "large file" case uses ~200 KiB — comfortably
/// inside the fixed geometry while still exercising several 64 KiB copy chunks.
/// Names are uppercase 8.3 ASCII so they survive the CP/M directory round-trip.
/// </remarks>
[TestFixture]
public class CpmStreamingWriteTests {
  private static readonly FormatCreateOptions Options = new();

  private static byte[] CreateFromStreams(params StreamingArchiveInput[] inputs) {
    var d = new CpmFormatDescriptor();
    using var ms = new MemoryStream();
    d.CreateFromStreams(ms, inputs, Options);
    return ms.ToArray();
  }

  private static byte[] CreateClassic(params (string Name, byte[] Data)[] inputs) {
    var d = new CpmFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), Options);
    return ms.ToArray();
  }

  private static byte[] ReadBack(byte[] image, string fullName) {
    var v = CpmReader.Read(image);
    var file = v.Files.Single(f => string.Equals(f.FullName, fullName, StringComparison.OrdinalIgnoreCase));
    return file.Data;
  }

  [Test]
  public void SingleEntry_RoundTrips() {
    var data = Pattern(4096);
    var bytes = CreateFromStreams(File("AB.BIN", data));

    // CP/M stores whole 1024-byte blocks; the reader returns the record-aligned
    // payload, which for a block-multiple size equals the original.
    Assert.That(ReadBack(bytes, "AB.BIN"), Is.EqualTo(data));
  }

  [Test]
  public void MultiEntry_AllReadBack() {
    var a = Pattern(1024, 1);     // exactly one 1024-byte block
    var b = Pattern(2048, 2);     // exactly two blocks (two extents' worth of records)
    var c = Pattern(512, 3);      // 4 × 128-byte records, under one block
    var bytes = CreateFromStreams(File("A.BIN", a), File("B.BIN", b), File("C.BIN", c));

    Assert.That(ReadBack(bytes, "A.BIN"), Is.EqualTo(a));
    Assert.That(ReadBack(bytes, "B.BIN"), Is.EqualTo(b));
    Assert.That(ReadBack(bytes, "C.BIN"), Is.EqualTo(c));
  }

  [Test]
  public void LargeEntry_StreamsAndRoundTrips() {
    // ~200 KiB exercises several 64 KiB copy chunks; capped well under the CP/M
    // 8" SSSD ceiling (241 × 1024-byte data blocks ≈ 241 KiB) so it fits the
    // fixed geometry. Size is a block multiple so the record-granular reader
    // returns the exact bytes.
    var big = Pattern(200 * 1024, 42);
    var bytes = CreateFromStreams(File("BIG.DAT", big));

    Assert.That(ReadBack(bytes, "BIG.DAT"), Is.EqualTo(big));
  }

  [Test]
  public void StreamingOutput_EqualsClassicCreate() {
    var a = Pattern(1024, 1);
    var b = Pattern(3 * 1024, 2);
    var streamed = CreateFromStreams(File("A.BIN", a), File("B.BIN", b));
    var classic = CreateClassic(("A.BIN", a), ("B.BIN", b));

    Assert.That(streamed, Is.EqualTo(classic));
  }
}
