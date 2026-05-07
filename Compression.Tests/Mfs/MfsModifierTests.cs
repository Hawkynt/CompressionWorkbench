#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Mfs;

namespace Compression.Tests.Mfs;

[TestFixture]
public class MfsModifierTests {

  // ── Round-trip correctness ────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "HELLO", "world"u8.ToArray());

    ms.Position = 0;
    var reader = new MfsReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "ALPHA", "first"u8.ToArray());
    MfsModifier.AddFile(ms, "BETA", "second"u8.ToArray());
    MfsModifier.AddFile(ms, "GAMMA", "third"u8.ToArray());

    ms.Position = 0;
    var reader = new MfsReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.Name,
      e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["ALPHA"], Is.EqualTo("first"));
    Assert.That(byName["BETA"], Is.EqualTo("second"));
    Assert.That(byName["GAMMA"], Is.EqualTo("third"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleBlocks() {
    // 3000 bytes / 1024 per block = 3 blocks. All blocks must round-trip.
    var ms = BuildEmptyImage();
    var data = new byte[3000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    MfsModifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new MfsReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "FIRST", new byte[2000]);
    Assert.That(MfsModifier.RemoveFile(ms, "FIRST"), Is.True);

    // Re-add a file at the same nominal block range; must succeed.
    MfsModifier.AddFile(ms, "SECOND", new byte[2000]);

    ms.Position = 0;
    var reader = new MfsReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "SECOND"), Is.True);
    Assert.That(reader.Entries.Any(e => e.Name == "FIRST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(MfsModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-XYZ123"u8.ToArray());
    MfsModifier.RemoveFile(ms, "SECRET");

    var bytes = ms.ToArray();
    var asAscii = System.Text.Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-XYZ123"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_PreservesOtherEntries() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "KEEP1", "k1"u8.ToArray());
    MfsModifier.AddFile(ms, "DROP", "d"u8.ToArray());
    MfsModifier.AddFile(ms, "KEEP2", "k2"u8.ToArray());

    Assert.That(MfsModifier.RemoveFile(ms, "DROP"), Is.True);

    ms.Position = 0;
    var reader = new MfsReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.Name,
      e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName.ContainsKey("KEEP1"), Is.True);
    Assert.That(byName.ContainsKey("KEEP2"), Is.True);
    Assert.That(byName.ContainsKey("DROP"), Is.False);
    Assert.That(byName["KEEP1"], Is.EqualTo("k1"));
    Assert.That(byName["KEEP2"], Is.EqualTo("k2"));
  }

  // ── O(touched bytes) verification ─────────────────────────────────────

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataBlocks() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    MfsModifier.AddFile(counter, "SMALL", new byte[] { 0x42 });

    // Expected I/O: 1 MDB sector read (512) + 1 dir slot probe (~40) +
    // 1 dir entry write (~50) + 1 data block write (1024).
    // For an image of 800*512 = 409600 bytes, ratio must be < 5%.
    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  // ── Integration via descriptor (same path through IArchiveModifiable) ─

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "interfaced"u8.ToArray());
      ((IArchiveModifiable)new MfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new MfsReader(ms);
      var entry = reader.Entries.Single(e => e.Name == "VIA-IF");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)),
                  Is.EqualTo("interfaced"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "DOC", "v1"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "v2-replacement"u8.ToArray());
      ((IArchiveModifiable)new MfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "DOC", false)]);

      ms.Position = 0;
      var reader = new MfsReader(ms);
      var matching = reader.Entries.Where(e => e.Name == "DOC").ToList();
      Assert.That(matching, Has.Count.EqualTo(1), "duplicate-named entries shouldn't accumulate");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(matching[0])),
                  Is.EqualTo("v2-replacement"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_ClearsEntry() {
    var ms = BuildEmptyImage();
    MfsModifier.AddFile(ms, "TARGET", "bye"u8.ToArray());

    ((IArchiveModifiable)new MfsFormatDescriptor()).Remove(ms, ["TARGET"]);

    ms.Position = 0;
    var reader = new MfsReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "TARGET"), Is.False);
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var w = new MfsWriter();
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
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
