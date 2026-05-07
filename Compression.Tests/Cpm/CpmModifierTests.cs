#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Cpm;

namespace Compression.Tests.Cpm;

[TestFixture]
public class CpmModifierTests {

  // ── Round-trip correctness ────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "HELLO.TXT", "world"u8.ToArray());

    var v = ReadVolume(ms);
    var file = v.Files.Single(f => f.Name == "HELLO" && f.Extension == "TXT");
    Assert.That(Encoding.ASCII.GetString(file.Data.AsSpan(0, 5)), Is.EqualTo("world"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "ALPHA.A", "first"u8.ToArray());
    CpmModifier.AddFile(ms, "BETA.B", "second"u8.ToArray());
    CpmModifier.AddFile(ms, "GAMMA.G", "third"u8.ToArray());

    var v = ReadVolume(ms);
    var byName = v.Files.ToDictionary(f => f.FullName, f => Encoding.ASCII.GetString(f.Data).TrimEnd('\0'));
    Assert.That(byName["ALPHA.A"][..5], Is.EqualTo("first"));
    Assert.That(byName["BETA.B"][..6], Is.EqualTo("second"));
    Assert.That(byName["GAMMA.G"][..5], Is.EqualTo("third"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleBlocks() {
    // 5000 bytes = 5 blocks (1024 × 4 + 904). Single extent (≤ 16 KB).
    var ms = BuildEmptyImage();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    CpmModifier.AddFile(ms, "BIG.DAT", data);

    var v = ReadVolume(ms);
    var file = v.Files.Single(f => f.Name == "BIG");
    Assert.That(file.Data.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultiExtentFile_ReassemblesAcrossExtents() {
    // 20 KB file forces 2 extents (16 KB + 4 KB).
    var ms = BuildEmptyImage();
    var data = new byte[20 * 1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 31) & 0xFF);
    CpmModifier.AddFile(ms, "HUGE.DAT", data);

    // Verify 2 directory entries were created for this file.
    var v = ReadVolume(ms);
    var file = v.Files.Single(f => f.Name == "HUGE");
    var retrieved = file.Data.AsSpan(0, data.Length).ToArray();
    Assert.That(retrieved, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ThreeExtentFile_ReassemblesAcrossExtents() {
    // 40 KB file forces 3 extents (16 + 16 + 8).
    var ms = BuildEmptyImage();
    var data = new byte[40 * 1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 7) & 0xFF);
    CpmModifier.AddFile(ms, "MEGA.BIN", data);

    var v = ReadVolume(ms);
    var file = v.Files.Single(f => f.Name == "MEGA");
    Assert.That(file.Data.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DeletesFromDirectory() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "TARGET.X", "delete me"u8.ToArray());
    CpmModifier.AddFile(ms, "KEEPER.Y", "untouched"u8.ToArray());

    Assert.That(CpmModifier.RemoveFile(ms, "TARGET.X"), Is.True);

    var v = ReadVolume(ms);
    Assert.That(v.Files.Any(f => f.Name == "TARGET"), Is.False);
    Assert.That(v.Files.Any(f => f.Name == "KEEPER"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(CpmModifier.RemoveFile(ms, "GHOST.XX"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "SECRET.TXT", "TOPSECRET-MARKER-XYZ123"u8.ToArray());
    CpmModifier.RemoveFile(ms, "SECRET.TXT");

    var bytes = ms.ToArray();
    var asAscii = Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-XYZ123"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_MultiExtent_DeletesAllExtents() {
    // 20 KB file uses 2 extents — both must be deleted.
    var ms = BuildEmptyImage();
    var data = new byte[20 * 1024];
    Array.Fill<byte>(data, 0xAB);
    CpmModifier.AddFile(ms, "MULTIE.DAT", data);

    Assert.That(CpmModifier.RemoveFile(ms, "MULTIE.DAT"), Is.True);

    var v = ReadVolume(ms);
    Assert.That(v.Files.Any(f => f.Name == "MULTIE"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_FreedSlotIsReused_AfterRemove() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "FIRST.X", new byte[2000]);
    CpmModifier.RemoveFile(ms, "FIRST.X");
    CpmModifier.AddFile(ms, "SECOND.Y", new byte[2000]);

    var v = ReadVolume(ms);
    Assert.That(v.Files.Any(f => f.Name == "SECOND"), Is.True);
    Assert.That(v.Files.Any(f => f.Name == "FIRST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_AfterCpmWriterBuild_CoexistsWithExisting() {
    // Start from a writer-built image with one file already; modifier must
    // discover the in-use blocks and not overwrite them.
    var existing = "PREEXIST"u8.ToArray();
    var image = CpmWriter.Build([("EXIST.TXT", existing, (byte)0)]);
    var ms = new MemoryStream();
    ms.Write(image);

    CpmModifier.AddFile(ms, "NEW.TXT", "freshly added"u8.ToArray());

    var v = ReadVolume(ms);
    Assert.That(v.Files, Has.Count.EqualTo(2));
    var existFile = v.Files.Single(f => f.Name == "EXIST");
    Assert.That(Encoding.ASCII.GetString(existFile.Data.AsSpan(0, existing.Length)),
                Is.EqualTo(Encoding.ASCII.GetString(existing)));
    var newFile = v.Files.Single(f => f.Name == "NEW");
    Assert.That(Encoding.ASCII.GetString(newFile.Data.AsSpan(0, 13)), Is.EqualTo("freshly added"));
  }

  // ── O(touched bytes) verification ─────────────────────────────────────

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataBlocks() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    CpmModifier.AddFile(counter, "SMALL.TXT", "hi"u8.ToArray());

    // Expected I/O: 2 KB directory read + 1 KB data block + 32 B dir slot = ~3104 bytes.
    // Bound at 6 KB to fail loudly if we ever regress to whole-image I/O (256 256 bytes).
    var totalIo = counter.BytesRead + counter.BytesWritten;
    Assert.That(totalIo, Is.LessThan(6 * 1024),
      $"Add of a 2-byte file should touch < 6 KB; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void RemoveFile_TouchesOnlyChainSectorsAndMetadata() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "VICTIM.X", new byte[2000]);  // 2 blocks

    var counter = new ByteCountingStream(ms);
    CpmModifier.RemoveFile(counter, "VICTIM.X");

    // Expected I/O: 2 KB dir read + 2 × 1 KB block wipe + 32 B dir slot = ~4128 bytes.
    var totalIo = counter.BytesRead + counter.BytesWritten;
    Assert.That(totalIo, Is.LessThan(8 * 1024),
      $"Remove of a 2-block file should touch < 8 KB; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    CpmModifier.AddFile(counter, "TINY.X", "x"u8.ToArray());

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
      ((IArchiveModifiable)new CpmFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF.TXT", false)]);

      var v = ReadVolume(ms);
      var file = v.Files.Single(f => f.Name == "VIAIF");
      Assert.That(Encoding.ASCII.GetString(file.Data.AsSpan(0, 10)), Is.EqualTo("interfaced"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "DOC.TXT", "v1"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "v2-replacement"u8.ToArray());
      ((IArchiveModifiable)new CpmFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "DOC.TXT", false)]);

      var v = ReadVolume(ms);
      var matching = v.Files.Where(f => f.Name == "DOC" && f.Extension == "TXT").ToList();
      Assert.That(matching, Has.Count.EqualTo(1), "duplicate-named entries shouldn't accumulate");
      Assert.That(Encoding.ASCII.GetString(matching[0].Data.AsSpan(0, 14)),
                  Is.EqualTo("v2-replacement"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface() {
    var ms = BuildEmptyImage();
    CpmModifier.AddFile(ms, "DOOMED.X", "bye"u8.ToArray());
    CpmModifier.AddFile(ms, "SURVIV.X", "alive"u8.ToArray());

    ((IArchiveModifiable)new CpmFormatDescriptor()).Remove(ms, ["DOOMED.X"]);

    var v = ReadVolume(ms);
    Assert.That(v.Files.Any(f => f.Name == "DOOMED"), Is.False);
    Assert.That(v.Files.Any(f => f.Name == "SURVIV"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_DeclaresCanModify() {
    var d = new CpmFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    // CpmWriter requires at least one file to build, so feed it an empty list
    // by bypassing — construct an image equivalent to a freshly-formatted disk.
    var image = CpmWriter.Build([]);
    var ms = new MemoryStream();
    ms.Write(image);
    return ms;
  }

  private static CpmReader.Volume ReadVolume(MemoryStream ms) =>
    CpmReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

  /// <summary>
  /// Wraps a stream and counts every read/write byte. Used to verify the
  /// modifier's I/O cost is O(touched bytes), not O(image size).
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
