#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

[TestFixture]
public class ProDosModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    ProDosModifier.AddFile(ms, "GREETING", "hello-prodos"u8.ToArray());
    ms.Position = 0;
    var reader = new ProDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "GREETING");
    var extracted = reader.Extract(entry);
    Assert.That(System.Text.Encoding.ASCII.GetString(extracted), Is.EqualTo("hello-prodos"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_UsesSapling() {
    // 5000 bytes > 512 → sapling tier with index block + 10 data blocks.
    var ms = BuildEmptyImage();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 17) & 0xFF);
    ProDosModifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new ProDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    Assert.That(entry.StorageType, Is.EqualTo(2));
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    ProDosModifier.AddFile(ms, "OLD", new byte[2000]);
    Assert.That(ProDosModifier.RemoveFile(ms, "OLD"), Is.True);
    ProDosModifier.AddFile(ms, "NEW", new byte[2000]);

    ms.Position = 0;
    var reader = new ProDosReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(ProDosModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    ProDosModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-PRODOS"u8.ToArray());
    ProDosModifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-PRODOS"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataBlocks() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    ProDosModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Expected: vol header read+write, bitmap read+write, dir read+write, data write,
    // plus 4-byte magic sniff = ~7 blocks. Bound at 14 blocks.
    Assert.That(totalIo, Is.LessThan(14 * 512),
      $"Add of a 1-byte file should touch < 14 blocks; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    ProDosModifier.AddFile(counter, "TINY", "x"u8.ToArray());

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
      ((IArchiveModifiable)new ProDosFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF", false)]);

      ms.Position = 0;
      var reader = new ProDosReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIAIF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new ProDosWriter().Build());
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
