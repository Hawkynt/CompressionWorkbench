#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.D81;

namespace Compression.Tests.D81;

[TestFixture]
public class D81ModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    D81Modifier.AddFile(ms, "HELLO81", "world-1581"u8.ToArray());
    ms.Position = 0;
    var reader = new D81Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO81");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world-1581"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    // 819200 bytes total minus reserved ≈ ~3160 sectors usable (256 bytes each, but data is 254/sector).
    // 50KB ≈ 202 sectors — exercises long chain walks across multiple tracks.
    var ms = BuildEmptyImage();
    var data = new byte[50_000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 7) & 0xFF);
    D81Modifier.AddFile(ms, "BIG81", data);

    ms.Position = 0;
    var reader = new D81Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG81");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    D81Modifier.AddFile(ms, "OLD81", new byte[10_000]);
    Assert.That(D81Modifier.RemoveFile(ms, "OLD81"), Is.True);
    D81Modifier.AddFile(ms, "NEW81", new byte[10_000]);

    ms.Position = 0;
    var reader = new D81Reader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD81"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW81"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(D81Modifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    D81Modifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-D81"u8.ToArray());
    D81Modifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-D81"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D81Modifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Touch budget: ~2 BAM reads + 1 dir read + 1 data write + 1 dir write + 2 BAM writes ≈ 7 sectors.
    // Bound at 16 sectors to flag any regression to whole-image I/O.
    Assert.That(totalIo, Is.LessThan(16 * 256),
      $"Add of a 1-byte file should touch < 16 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D81Modifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    // 819 200 bytes total; 16 sectors ≈ 4096 bytes ≈ 0.5%. Bound at 1%.
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-d81"u8.ToArray());
      ((IArchiveModifiable)new D81FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new D81Reader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIA-IF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new D81Writer().Build());
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
      var n = _inner.Read(buffer, offset, count);
      BytesRead += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count);
      BytesWritten += count;
    }
  }
}
