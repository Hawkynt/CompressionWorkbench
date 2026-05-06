#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedZip();
    ZipModifier.AddFile(ms, "greet.txt", "hello-zip"u8.ToArray());
    ms.Position = 0;
    var reader = new ZipReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "greet.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("hello-zip"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedZip();
    ZipModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());
    ms.Position = 0;
    var reader = new ZipReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.FileName, e =>
      System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_DeflateCompresses() {
    var ms = BuildSeedZip();
    var data = new byte[8000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i / 100) & 0xFF); // very compressible
    ZipModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new ZipReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "big.bin");
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(data));
    Assert.That(entry.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate));
    Assert.That(entry.CompressedSize, Is.LessThan(entry.UncompressedSize));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedZip();
    ZipModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    ZipModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());

    Assert.That(ZipModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new ZipReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "victim.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.FileName == "keeper.txt"), Is.True);
    Assert.That(reader.Entries.Any(e => e.FileName == "seed.txt"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedZip();
    Assert.That(ZipModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesOrphanBytes() {
    var ms = BuildSeedZip();
    ZipModifier.AddFile(ms, "secret.txt", "TOPSECRET-MARKER-ZIP-ABC"u8.ToArray());
    // Use Store path to ensure marker remains visible in raw bytes (deflate would obscure it).
    ms.Position = 0;
    Assert.That(System.Text.Encoding.ASCII.GetString(ms.ToArray()),
      Does.Contain("TOPSECRET-MARKER-ZIP-ABC"));

    ZipModifier.RemoveFile(ms, "secret.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(ms.ToArray()),
      Does.Not.Contain("TOPSECRET-MARKER-ZIP-ABC"));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedZip();
    ZipModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    ZipModifier.RemoveFile(ms, "doc.txt");
    ZipModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new ZipReader(ms);
    var matching = reader.Entries.Where(e => e.FileName == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(matching[0])),
      Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithArchiveSize() {
    // 8MB seed: dominates the 65 KB EOCD-scan window so the touched ratio
    // reflects actual scaling rather than the constant EOCD-search cost.
    var ms = BuildSeedZipWithBytes(megabytes: 8);
    var counter = new ByteCountingStream(ms);
    ZipModifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.02),
      $"Add of a 1-byte file touched {ratio:P1} of an 8MB archive; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedZip();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new ZipFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new ZipReader(ms);
      var entry = reader.Entries.Single(e => e.FileName == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedZip() {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    w.AddEntry("seed.txt", "seed-content"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();
    return ms;
  }

  private static MemoryStream BuildSeedZipWithBytes(int megabytes) {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    var bigBuf = new byte[megabytes * 1024 * 1024];
    Random.Shared.NextBytes(bigBuf); // incompressible, forces real bytes on disk
    w.AddEntry("seed.bin", bigBuf, ZipCompressionMethod.Store);
    w.Finish();
    return ms;
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
