#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Atari8;

namespace Compression.Tests.Atari8;

[TestFixture]
public class Atari8ModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    Atari8Modifier.AddFile(ms, "HELLO.TXT", "world-atari"u8.ToArray());
    ms.Position = 0;
    var reader = new Atari8Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO.TXT");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world-atari"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    // 1000 bytes = 8 sectors at 125 bytes each (128 - 3 trailer).
    var ms = BuildEmptyImage();
    var data = new byte[1000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 19 + 1) & 0xFF);
    Atari8Modifier.AddFile(ms, "BIG.DAT", data);

    ms.Position = 0;
    var reader = new Atari8Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG.DAT");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    Atari8Modifier.AddFile(ms, "OLD.DAT", new byte[500]);
    Assert.That(Atari8Modifier.RemoveFile(ms, "OLD.DAT"), Is.True);
    Atari8Modifier.AddFile(ms, "NEW.DAT", new byte[500]);

    ms.Position = 0;
    var reader = new Atari8Reader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD.DAT"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW.DAT"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(Atari8Modifier.RemoveFile(ms, "GHOST.DAT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    Atari8Modifier.AddFile(ms, "SECRET.TXT", "TOPSECRET-MARKER-ATARI"u8.ToArray());
    Atari8Modifier.RemoveFile(ms, "SECRET.TXT");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-ATARI"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    Atari8Modifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Header sniff + VTOC read + 8 dir-sector scan reads + 1 data write + 1 dir write + 1 VTOC write.
    // Bound at 24 sectors × 128 = 3072 bytes (still < 5% of image).
    Assert.That(totalIo, Is.LessThan(24 * 128),
      $"Add of a 1-byte file should touch < 24 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    Atari8Modifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new Atari8FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF.TXT", false)]);

      ms.Position = 0;
      var reader = new Atari8Reader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIAIF.TXT"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new Atari8Writer().Build());
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
