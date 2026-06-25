#pragma warning disable CS1591
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

/// <summary>
/// Round-trip + genuine-in-place verification for <see cref="Ti99Modifier"/>:
/// add / remove must mutate only the touched structures (VIB bitmap, FDIR,
/// FDR sector, file data run) and keep every other byte — and the image
/// length — unchanged.
/// </summary>
[TestFixture]
public class Ti99ModifierTests {

  private static MemoryStream BuildSeed(out byte[] seedData) {
    seedData = new byte[700];
    new Random(11).NextBytes(seedData);
    var w = new Ti99Writer();
    w.AddFile("SEED", seedData);
    var ms = new MemoryStream();
    ms.Write(w.BuildSectorDump());
    ms.Position = 0;
    return ms;
  }

  [Test]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed(out _);
    var data = new byte[3000];
    new Random(2).NextBytes(data);
    Ti99Modifier.AddFile(ms, "DATA", data);

    ms.Position = 0;
    using var r = new Ti99Reader(ms);
    var e = r.Entries.Single(x => x.Name == "DATA");
    Assert.That(r.Extract(e).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test]
  public void RemoveFile_DeletesButKeepsSeed() {
    var ms = BuildSeed(out _);
    Ti99Modifier.AddFile(ms, "VICTIM", new byte[600]);
    Assert.That(Ti99Modifier.RemoveFile(ms, "VICTIM"), Is.True);

    ms.Position = 0;
    using var r = new Ti99Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "VICTIM"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "SEED"), Is.True);
  }

  [Test]
  public void SameSizeUpdate_DoesNotChangeImageLength_AndKeepsSeedBytes() {
    var ms = BuildSeed(out var seedData);
    var lenBefore = ms.Length;

    // Locate the seed's data bytes on disk to prove they stay put.
    ms.Position = 0;
    using (var r0 = new Ti99Reader(ms)) {
      var seed = r0.Entries.Single(e => e.Name == "SEED");
      var seedOffset = seed.FirstSector * 256L;
      var snapshot = new byte[seedData.Length];
      ms.Position = seedOffset;
      ms.ReadExactly(snapshot);
      Assert.That(snapshot.SequenceEqual(seedData), Is.True, "seed bytes wrong before mutation");

      // Add then same-size replace.
      Ti99Modifier.AddFile(ms, "DOC", Enumerable.Repeat((byte)1, 500).ToArray());
      Ti99Modifier.RemoveFile(ms, "DOC");
      Ti99Modifier.AddFile(ms, "DOC", Enumerable.Repeat((byte)2, 500).ToArray());

      // Seed data must be byte-identical at the same offset.
      var after = new byte[seedData.Length];
      ms.Position = seedOffset;
      ms.ReadExactly(after);
      Assert.That(after.SequenceEqual(seedData), Is.True, "seed bytes moved/changed — not in-place");
    }

    Assert.That(ms.Length, Is.EqualTo(lenBefore), "image length changed — not a fixed-geometry in-place edit");
  }

  [Test]
  public void Add_TouchesFarLessThanWholeImage() {
    var ms = BuildSeed(out _);
    var counter = new ByteCountingStream(ms);
    Ti99Modifier.AddFile(counter, "TINY", "x"u8.ToArray());
    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; must be O(touched bytes).");
  }

  private sealed class ByteCountingStream : Stream {
    private readonly Stream _inner;
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public ByteCountingStream(Stream inner) { _inner = inner; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); BytesRead += n; return n; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); BytesWritten += count; }
  }
}
