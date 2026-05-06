#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.D71;

namespace Compression.Tests.D71;

[TestFixture]
public class D71ModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    D71Modifier.AddFile(ms, "DOUBLE", "side-test"u8.ToArray());
    ms.Position = 0;
    var reader = new D71Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "DOUBLE");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("side-test"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_BeyondSide1Capacity_LandsOnSide2() {
    var ms = BuildEmptyImage();
    // Fill side 1 with files until allocation forces side 2.
    for (var i = 0; i < 50; i++)
      D71Modifier.AddFile(ms, $"F{i:D3}", new byte[3000]); // 3000 bytes ≈ 12 sectors each

    ms.Position = 0;
    var reader = new D71Reader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(50));
    foreach (var entry in reader.Entries)
      Assert.That(reader.Extract(entry).Length, Is.EqualTo(3000));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    D71Modifier.AddFile(ms, "OLD", new byte[5000]);
    Assert.That(D71Modifier.RemoveFile(ms, "OLD"), Is.True);
    D71Modifier.AddFile(ms, "NEW", new byte[5000]);

    ms.Position = 0;
    var reader = new D71Reader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D71Modifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // 2 BAM reads + 1 dir read + 1 data write + 1 dir write + 2 BAM writes ≈ 7 sectors.
    Assert.That(totalIo, Is.LessThan(12 * 256),
      $"Add of a 1-byte file should touch < 12 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D71Modifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.02),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    D71Modifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-D71"u8.ToArray());
    D71Modifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-D71"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-d71"u8.ToArray());
      ((IArchiveModifiable)new D71FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new D71Reader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIA-IF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new D71Writer().Build());
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
