#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Bbc;

namespace Compression.Tests.Bbc;

[TestFixture]
public class BbcModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    BbcModifier.AddFile(ms, "GREET", "hello-bbc"u8.ToArray());
    ms.Position = 0;
    var reader = new BbcReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "GREET");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("hello-bbc"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 8 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 11) & 0xFF);
    BbcModifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new BbcReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    BbcModifier.AddFile(ms, "OLD", new byte[1000]);
    Assert.That(BbcModifier.RemoveFile(ms, "OLD"), Is.True);
    BbcModifier.AddFile(ms, "NEW", new byte[1000]);

    ms.Position = 0;
    var reader = new BbcReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(BbcModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    BbcModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-BBC"u8.ToArray());
    BbcModifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-BBC"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    BbcModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Header sniff (8 bytes) + 2 catalog reads + 1 data write (1 sector + tail pad) + 2 catalog writes.
    Assert.That(totalIo, Is.LessThan(8 * 256),
      $"Add of a 1-byte file should touch < 8 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    BbcModifier.AddFile(counter, "TINY", "x"u8.ToArray());

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
      ((IArchiveModifiable)new BbcFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF", false)]);

      ms.Position = 0;
      var reader = new BbcReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIAIF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new BbcWriter().Build());
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
