#pragma warning disable CS1591
using FileSystem.Trsdos;

namespace Compression.Tests.Trsdos;

/// <summary>
/// Round-trip + genuine-in-place verification for <see cref="TrsdosModifier"/>.
/// Add / remove must touch only the GAT, the affected directory sector, and
/// the file's granule run — keeping all other bytes and the image length
/// unchanged.
/// </summary>
[TestFixture]
public class TrsdosModifierTests {

  private static MemoryStream BuildSeed(out byte[] seedData) {
    seedData = new byte[700];
    new Random(13).NextBytes(seedData);
    var w = new TrsdosWriter();
    w.SetGeometry(40, 18);
    w.AddFile("SEED.DAT", seedData);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;
    return ms;
  }

  [Test]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed(out _);
    var data = new byte[3000];
    new Random(2).NextBytes(data);
    TrsdosModifier.AddFile(ms, "DATA.BIN", data);

    ms.Position = 0;
    using var r = new TrsdosReader(ms);
    var e = r.Entries.Single(x => x.Name == "DATA.BIN");
    Assert.That(r.Extract(e).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test]
  public void RemoveFile_DeletesButKeepsSeed() {
    var ms = BuildSeed(out _);
    TrsdosModifier.AddFile(ms, "VICTIM.BIN", new byte[600]);
    Assert.That(TrsdosModifier.RemoveFile(ms, "VICTIM.BIN"), Is.True);

    ms.Position = 0;
    using var r = new TrsdosReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "VICTIM.BIN"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "SEED.DAT"), Is.True);
  }

  [Test]
  public void SameSizeUpdate_DoesNotChangeImageLength_AndKeepsSeedBytes() {
    var ms = BuildSeed(out var seedData);
    var lenBefore = ms.Length;

    ms.Position = 0;
    int seedFirstSector;
    using (var r0 = new TrsdosReader(ms)) {
      var seed = r0.Entries.Single(e => e.Name == "SEED.DAT");
      seedFirstSector = seed.FirstSector;
    }
    var seedOffset = seedFirstSector * 256L;
    var snapshot = new byte[seedData.Length];
    ms.Position = seedOffset;
    ms.ReadExactly(snapshot);
    Assert.That(snapshot.SequenceEqual(seedData), Is.True, "seed bytes wrong before mutation");

    TrsdosModifier.AddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)1, 500).ToArray());
    TrsdosModifier.RemoveFile(ms, "DOC.TXT");
    TrsdosModifier.AddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)2, 500).ToArray());

    var after = new byte[seedData.Length];
    ms.Position = seedOffset;
    ms.ReadExactly(after);
    Assert.That(after.SequenceEqual(seedData), Is.True, "seed bytes moved/changed — not in-place");
    Assert.That(ms.Length, Is.EqualTo(lenBefore), "image length changed — not a fixed-geometry in-place edit");
  }

  [Test]
  public void Add_TouchesFarLessThanWholeImage() {
    var ms = BuildSeed(out _);
    var counter = new ByteCountingStream(ms);
    TrsdosModifier.AddFile(counter, "TINY.BIN", "x"u8.ToArray());
    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.10),
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
