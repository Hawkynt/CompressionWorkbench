#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

[TestFixture]
public class CpioModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedCpio();
    CpioModifier.AddFile(ms, "added.txt", "hello-cpio"u8.ToArray());
    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["added.txt"], Is.EqualTo("hello-cpio"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedCpio();
    CpioModifier.AddFile(ms, "added.txt", "new-data"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.Count, Is.EqualTo(2));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(entries["added.txt"], Is.EqualTo("new-data"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedCpio();
    CpioModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    CpioModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(CpioModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries.ContainsKey("keeper.txt"), Is.True);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedCpio();
    Assert.That(CpioModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedCpio();
    CpioModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    CpioModifier.RemoveFile(ms, "doc.txt");
    CpioModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["doc.txt"], Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedCpio();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new CpioFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(ms);
      Assert.That(entries["via-if.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("Performance")]
  public void AddTinyFile_TouchesOnlyHeadersAndNewBytes() {
    // Build a 4MB seed with a single huge entry; Add should walk only the
    // (single) header chain to the trailer, then write the new entry + new trailer.
    var ms = BuildLargeSeedCpio(megabytes: 4);
    var counter = new ByteCountingStream(ms);
    CpioModifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of a 4MB archive; should walk headers only.");
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedCpio() {
    var ms = new MemoryStream();
    var w = new CpioWriter(ms, leaveOpen: true);
    w.AddFile("seed.txt", "seed-content"u8.ToArray());
    w.Finish();
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static MemoryStream BuildLargeSeedCpio(int megabytes) {
    var ms = new MemoryStream();
    var w = new CpioWriter(ms, leaveOpen: true);
    var bigBuf = new byte[megabytes * 1024 * 1024];
    Random.Shared.NextBytes(bigBuf);
    w.AddFile("seed.bin", bigBuf);
    w.Finish();
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static Dictionary<string, string> ReadAll(Stream s) {
    var r = new CpioReader(s, leaveOpen: true);
    var all = r.ReadAll();
    var result = new Dictionary<string, string>();
    foreach (var (entry, data) in all) {
      if (entry.IsDirectory) continue;
      result[entry.Name] = System.Text.Encoding.ASCII.GetString(data);
    }
    return result;
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
    public override int Read(byte[] buffer, int offset, int count) {
      var n = _inner.Read(buffer, offset, count); BytesRead += n; return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count); BytesWritten += count;
    }
  }
}
