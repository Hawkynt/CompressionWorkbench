#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Adf;

namespace Compression.Tests.Adf;

[TestFixture]
public class AdfModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    AdfModifier.AddFile(ms, "GREETING", "hello-amiga"u8.ToArray());
    ms.Position = 0;
    var reader = new AdfReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "GREETING");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("hello-amiga"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[5000]; // 10 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 1) & 0xFF);
    AdfModifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new AdfReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    AdfModifier.AddFile(ms, "OLD", new byte[2000]);
    Assert.That(AdfModifier.RemoveFile(ms, "OLD"), Is.True);
    AdfModifier.AddFile(ms, "NEW", new byte[2000]);

    ms.Position = 0;
    var reader = new AdfReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(AdfModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    AdfModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-ADF"u8.ToArray());
    AdfModifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-ADF"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_HashChainsCorrectly() {
    // 'AA' and 'BB' might land in different hash buckets, but adding many short
    // names lets the chain logic exercise both first-in-bucket and chain-append paths.
    var ms = BuildEmptyImage();
    AdfModifier.AddFile(ms, "ALPHA", "first"u8.ToArray());
    AdfModifier.AddFile(ms, "BETA", "second"u8.ToArray());
    AdfModifier.AddFile(ms, "GAMMA", "third"u8.ToArray());

    ms.Position = 0;
    var reader = new AdfReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.Name, e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["ALPHA"], Is.EqualTo("first"));
    Assert.That(byName["BETA"], Is.EqualTo("second"));
    Assert.That(byName["GAMMA"], Is.EqualTo("third"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataBlocks() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    AdfModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // FFS sniff (4 bytes) + bitmap read + 1 data write + header write + root read+write + bitmap write
    // ≈ 6 sectors. Bound at 14 to give slack.
    Assert.That(totalIo, Is.LessThan(14 * 512),
      $"Add of a 1-byte file should touch < 14 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    AdfModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.02),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new AdfFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new AdfReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIA-IF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new AdfWriter().Build());
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
