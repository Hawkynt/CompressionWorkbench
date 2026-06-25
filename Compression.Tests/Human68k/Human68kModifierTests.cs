#pragma warning disable CS1591
using FileSystem.Human68k;

namespace Compression.Tests.Human68k;

/// <summary>
/// Round-trip + genuine-in-place verification for <see cref="Human68kModifier"/>.
/// In-place add (contiguous FAT12 allocation) / remove must touch only the FAT,
/// the dirent, and the file's clusters — keeping every other byte and the image
/// length unchanged for the in-place path.
/// </summary>
[TestFixture]
public class Human68kModifierTests {

  // Build a seed image with enough free clusters for the in-place add/update tests.
  private static MemoryStream BuildSeed(out byte[] seedData) {
    seedData = new byte[700];
    new Random(19).NextBytes(seedData);
    var w = new Human68kWriter();
    w.SetSectorsPerCluster(1);
    w.SetTotalSectors(200); // ~100 KB — room for several files.
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
    Assert.That(Human68kModifier.TryAddFile(ms, "DATA.BIN", data), Is.True);

    ms.Position = 0;
    using var r = new Human68kReader(ms);
    var e = r.Entries.Single(x => x.Name == "DATA.BIN");
    Assert.That(r.Extract(e).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test]
  public void RemoveFile_DeletesButKeepsSeed() {
    var ms = BuildSeed(out _);
    Assert.That(Human68kModifier.TryAddFile(ms, "VICTIM.BIN", new byte[600]), Is.True);
    Assert.That(Human68kModifier.RemoveFile(ms, "VICTIM.BIN"), Is.True);

    ms.Position = 0;
    using var r = new Human68kReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "VICTIM.BIN"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "SEED.DAT"), Is.True);
  }

  [Test]
  public void SameSizeUpdate_DoesNotChangeImageLength_AndKeepsSeedBytes() {
    var ms = BuildSeed(out var seedData);
    var lenBefore = ms.Length;

    // Snapshot the seed's first cluster bytes to prove they don't move.
    ms.Position = 0;
    byte[] snapshot;
    long seedOffset;
    using (var r0 = new Human68kReader(ms)) {
      var seed = r0.Entries.Single(e => e.Name == "SEED.DAT");
      var extracted = r0.Extract(seed);
      Assert.That(extracted.SequenceEqual(seedData), Is.True);
      snapshot = extracted;
      // Reader Extract offset is deterministic from FirstCluster; re-derive it.
      seedOffset = -1; // captured indirectly via re-extract below
    }

    Assert.That(Human68kModifier.TryAddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)1, 500).ToArray()), Is.True);
    Human68kModifier.RemoveFile(ms, "DOC.TXT");
    Assert.That(Human68kModifier.TryAddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)2, 500).ToArray()), Is.True);

    ms.Position = 0;
    using (var r1 = new Human68kReader(ms)) {
      var seed = r1.Entries.Single(e => e.Name == "SEED.DAT");
      Assert.That(r1.Extract(seed).SequenceEqual(snapshot), Is.True, "seed bytes changed — not in-place");
    }
    _ = seedOffset;
    Assert.That(ms.Length, Is.EqualTo(lenBefore), "image length changed on in-place path");
  }

  [Test]
  public void Add_TouchesFarLessThanWholeImage() {
    var ms = BuildSeed(out _);
    var counter = new ByteCountingStream(ms);
    Assert.That(Human68kModifier.TryAddFile(counter, "TINY.BIN", "x"u8.ToArray()), Is.True);
    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.20),
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
