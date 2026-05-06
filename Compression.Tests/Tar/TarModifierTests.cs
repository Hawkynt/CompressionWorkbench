#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedTar();
    TarModifier.AddFile(ms, "added.txt", "hello-tar"u8.ToArray());
    ms.Position = 0;
    var reader = new TarReader(ms);
    var entries = ReadAll(reader);
    Assert.That(entries["added.txt"], Is.EqualTo("hello-tar"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedTar();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    TarModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new TarReader(ms);
    while (reader.GetNextEntry() is { } e) {
      if (e.Name == "big.bin") {
        using var es = reader.GetEntryStream();
        var buf = new byte[e.Size];
        es.ReadExactly(buf);
        Assert.That(buf, Is.EqualTo(data));
        return;
      }
      reader.Skip();
    }
    Assert.Fail("big.bin not found");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedTar();
    TarModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    TarModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(TarModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new TarReader(ms);
    var entries = ReadAll(reader);
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries.ContainsKey("keeper.txt"), Is.True);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedTar();
    Assert.That(TarModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedTar();
    TarModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    TarModifier.RemoveFile(ms, "doc.txt");
    TarModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new TarReader(ms);
    var entries = ReadAll(reader);
    Assert.That(entries["doc.txt"], Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("Performance")]
  public void AddTinyFile_TouchesOnlyHeadersAndNewBytes() {
    // Build a 4MB seed with a single huge entry; Add should walk only the
    // (single) header and the trailing terminator, then write our new bytes.
    var ms = BuildLargeSeedTar(megabytes: 4);
    var counter = new ByteCountingStream(ms);
    TarModifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P1} of a 4MB archive; should walk headers only.");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedTar();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new TarFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new TarReader(ms);
      var entries = ReadAll(reader);
      Assert.That(entries["via-if.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedTar() {
    var ms = new MemoryStream();
    var w = new TarWriter(ms);
    w.AddEntry(new TarEntry { Name = "seed.txt", Size = 12 }, "seed-content"u8.ToArray());
    w.Finish();
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }

  private static MemoryStream BuildLargeSeedTar(int megabytes) {
    var ms = new MemoryStream();
    var w = new TarWriter(ms);
    var bigBuf = new byte[megabytes * 1024 * 1024];
    Random.Shared.NextBytes(bigBuf);
    w.AddEntry(new TarEntry { Name = "seed.bin", Size = bigBuf.Length }, bigBuf);
    w.Finish();
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }

  private static Dictionary<string, string> ReadAll(TarReader r) {
    var result = new Dictionary<string, string>();
    while (r.GetNextEntry() is { } e) {
      if (e.IsDirectory) { r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var buf = new byte[e.Size];
      es.ReadExactly(buf);
      result[e.Name] = System.Text.Encoding.ASCII.GetString(buf);
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
