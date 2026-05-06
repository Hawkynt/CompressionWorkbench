#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.D64;

namespace Compression.Tests.D64;

[TestFixture]
public class D64ModifierTests {

  // ── Round-trip correctness ────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "HELLO", "world"u8.ToArray());

    ms.Position = 0;
    var reader = new D64Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "ALPHA", "first"u8.ToArray());
    D64Modifier.AddFile(ms, "BETA", "second"u8.ToArray());
    D64Modifier.AddFile(ms, "GAMMA", "third"u8.ToArray());

    ms.Position = 0;
    var reader = new D64Reader(ms);
    var byName = reader.Entries.ToDictionary(e => e.Name, e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["ALPHA"], Is.EqualTo("first"));
    Assert.That(byName["BETA"], Is.EqualTo("second"));
    Assert.That(byName["GAMMA"], Is.EqualTo("third"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    // 1000 bytes = 4 sectors (254 + 254 + 254 + 238). All writes should chain correctly.
    var ms = BuildEmptyImage();
    var data = new byte[1000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    D64Modifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new D64Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DeletesFromDirectory() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "TARGET", "delete me"u8.ToArray());
    D64Modifier.AddFile(ms, "KEEPER", "untouched"u8.ToArray());

    Assert.That(D64Modifier.RemoveFile(ms, "TARGET"), Is.True);

    ms.Position = 0;
    var reader = new D64Reader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "TARGET"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "KEEPER"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(D64Modifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-XYZ123"u8.ToArray());
    D64Modifier.RemoveFile(ms, "SECRET");

    var bytes = ms.ToArray();
    var asAscii = System.Text.Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-XYZ123"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_FreedSlotIsReused_AfterRemove() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "FIRST", new byte[100]);
    D64Modifier.RemoveFile(ms, "FIRST");
    // Free space should now be reusable.
    D64Modifier.AddFile(ms, "SECOND", new byte[100]);

    ms.Position = 0;
    var reader = new D64Reader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "SECOND"), Is.True);
    Assert.That(reader.Entries.Any(e => e.Name == "FIRST"), Is.False);
  }

  // ── O(touched bytes) verification ─────────────────────────────────────

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D64Modifier.AddFile(counter, "SMALL", "hi"u8.ToArray());

    // Expected I/O: 1 BAM sector + 1 dir sector + 1 data sector = 3 reads + 3 writes = ~1536 bytes.
    // Allow generous slack for any internal helper sector reads (e.g. directory walk
    // before locating a free slot). Bound at 8 sectors total to fail loudly if we
    // ever regress to whole-image I/O (174 848 bytes).
    var totalIo = counter.BytesRead + counter.BytesWritten;
    Assert.That(totalIo, Is.LessThan(8 * 256),
      $"Add of a 2-byte file should touch < 8 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void RemoveFile_TouchesOnlyChainSectorsAndMetadata() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "VICTIM", new byte[508]);  // 2 data sectors

    var counter = new ByteCountingStream(ms);
    D64Modifier.RemoveFile(counter, "VICTIM");

    // Expected I/O during remove: walk dir to find entry (≤1 dir sector read) +
    // walk file chain (2 data sector reads + 2 wipe writes) +
    // BAM read + dir sector write + BAM write.
    // Total: ~3 reads + ~4 writes = 7 sectors = 1792 bytes; bound at 16 sectors.
    var totalIo = counter.BytesRead + counter.BytesWritten;
    Assert.That(totalIo, Is.LessThan(16 * 256),
      $"Remove of a 2-sector file should touch < 16 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    // The whole point of this design: doubling the image size mustn't
    // increase the I/O cost of adding a tiny file. (Pretend D64 had a
    // larger geometry; the modifier doesn't know — it just writes the
    // sectors it needs.) Real D64 is fixed-size 174 848, but we compare
    // I/O against image size to guard the contract.

    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    D64Modifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes), not O(image size).");
  }

  // ── Integration via descriptor (same path through IArchiveModifiable) ─

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "interfaced"u8.ToArray());
      ((IArchiveModifiable)new D64FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new D64Reader(ms);
      var entry = reader.Entries.Single(e => e.Name.Contains("VIA-IF"));
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)),
                  Is.EqualTo("interfaced"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    var ms = BuildEmptyImage();
    D64Modifier.AddFile(ms, "DOC", "v1"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "v2-replacement"u8.ToArray());
      ((IArchiveModifiable)new D64FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "DOC", false)]);

      ms.Position = 0;
      var reader = new D64Reader(ms);
      var matching = reader.Entries.Where(e => e.Name == "DOC").ToList();
      Assert.That(matching, Has.Count.EqualTo(1), "duplicate-named entries shouldn't accumulate");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(matching[0])),
                  Is.EqualTo("v2-replacement"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var w = new D64Writer();
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    return ms;
  }

  /// <summary>
  /// Wraps a stream and counts every read/write byte. Used to verify that
  /// the modifier's I/O cost is O(touched bytes), not O(image size).
  /// </summary>
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
