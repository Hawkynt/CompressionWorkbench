#pragma warning disable CS1591
using FileSystem.Pc98;

namespace Compression.Tests.Pc98;

/// <summary>
/// Round-trip + genuine-in-place verification for <see cref="Pc98Modifier"/>.
/// In-place add (contiguous FAT12 allocation, IPL-shifted offsets) / remove
/// must touch only the FAT, the dirent, and the file's clusters — keeping
/// every other byte and the image length unchanged for the in-place path.
/// </summary>
[TestFixture]
public class Pc98ModifierTests {

  private static MemoryStream BuildSeed(out byte[] seedData) {
    seedData = new byte[700];
    new Random(23).NextBytes(seedData);
    var w = new Pc98Writer();
    w.SetSectorsPerCluster(1);
    w.SetTotalSectors(200);
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
    Assert.That(Pc98Modifier.TryAddFile(ms, "DATA.BIN", data), Is.True);

    ms.Position = 0;
    using var r = new Pc98Reader(ms);
    var e = r.Entries.Single(x => x.Name == "DATA.BIN");
    Assert.That(r.Extract(e).AsSpan(0, data.Length).SequenceEqual(data), Is.True);
  }

  [Test]
  public void RemoveFile_DeletesButKeepsSeed() {
    var ms = BuildSeed(out _);
    Assert.That(Pc98Modifier.TryAddFile(ms, "VICTIM.BIN", new byte[600]), Is.True);
    Assert.That(Pc98Modifier.RemoveFile(ms, "VICTIM.BIN"), Is.True);

    ms.Position = 0;
    using var r = new Pc98Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "VICTIM.BIN"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "SEED.DAT"), Is.True);
  }

  [Test]
  public void SameSizeUpdate_DoesNotChangeImageLength_AndKeepsSeedBytes() {
    var ms = BuildSeed(out var seedData);
    var lenBefore = ms.Length;

    ms.Position = 0;
    byte[] snapshot;
    using (var r0 = new Pc98Reader(ms)) {
      var seed = r0.Entries.Single(e => e.Name == "SEED.DAT");
      snapshot = r0.Extract(seed);
      Assert.That(snapshot.SequenceEqual(seedData), Is.True);
    }

    Assert.That(Pc98Modifier.TryAddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)1, 500).ToArray()), Is.True);
    Pc98Modifier.RemoveFile(ms, "DOC.TXT");
    Assert.That(Pc98Modifier.TryAddFile(ms, "DOC.TXT", Enumerable.Repeat((byte)2, 500).ToArray()), Is.True);

    ms.Position = 0;
    using (var r1 = new Pc98Reader(ms)) {
      var seed = r1.Entries.Single(e => e.Name == "SEED.DAT");
      Assert.That(r1.Extract(seed).SequenceEqual(snapshot), Is.True, "seed bytes changed — not in-place");
    }
    Assert.That(ms.Length, Is.EqualTo(lenBefore), "image length changed on in-place path");
  }

  [Test]
  public void Add_TouchesFarLessThanWholeImage() {
    var ms = BuildSeed(out _);
    var counter = new ByteCountingStream(ms);
    Assert.That(Pc98Modifier.TryAddFile(counter, "TINY.BIN", "x"u8.ToArray()), Is.True);
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
