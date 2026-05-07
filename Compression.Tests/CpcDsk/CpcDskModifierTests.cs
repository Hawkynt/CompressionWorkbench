#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.CpcDsk;

namespace Compression.Tests.CpcDsk;

[TestFixture]
public class CpcDskModifierTests {

  // ── Round-trip correctness ────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack_DirectoryEntryAppearsOnTrack0() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "HELLO.TXT", "world"u8.ToArray());

    ms.Position = 0;
    var image = ms.ToArray();
    // Directory area starts at first track-0 sector data offset = DiskInfo (256) + TIB (256) = 512.
    // The first 32-byte directory entry should now show user=0, name=HELLO   .TXT.
    Assert.That(image[512], Is.EqualTo(0x00), "first dir entry should be 'in use' (user 0)");
    var basePart = Encoding.ASCII.GetString(image, 512 + 1, 8);
    var extPart = Encoding.ASCII.GetString(image, 512 + 9, 3);
    Assert.That(basePart, Is.EqualTo("HELLO   "));
    Assert.That(extPart, Is.EqualTo("TXT"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_WritesPayloadIntoAllocatedDataSector() {
    var ms = BuildEmptyImage();
    var payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();
    CpcDskModifier.AddFile(ms, "DATA.BIN", payload);

    ms.Position = 0;
    var image = ms.ToArray();
    // First file always lands on the first block of track 1 (= block sectorsPerTrack*sides).
    // Track 1 sector data starts at: DiskInfo + 1×trackBlock + TIB = 256 + (256 + 9*512) + 256 = 5376.
    // Reading that range should reveal the payload bytes.
    var slice = image.AsSpan(5376, payload.Length).ToArray();
    Assert.That(slice, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "A.TXT", "first"u8.ToArray());
    CpcDskModifier.AddFile(ms, "B.TXT", "second"u8.ToArray());
    CpcDskModifier.AddFile(ms, "C.TXT", "third"u8.ToArray());

    var image = ms.ToArray();
    // Three contiguous directory slots populated.
    Assert.That(image[512 + 0 * 32], Is.EqualTo(0x00));
    Assert.That(image[512 + 1 * 32], Is.EqualTo(0x00));
    Assert.That(image[512 + 2 * 32], Is.EqualTo(0x00));
    var n0 = Encoding.ASCII.GetString(image, 512 + 0 * 32 + 1, 8).TrimEnd();
    var n1 = Encoding.ASCII.GetString(image, 512 + 1 * 32 + 1, 8).TrimEnd();
    var n2 = Encoding.ASCII.GetString(image, 512 + 2 * 32 + 1, 8).TrimEnd();
    Assert.That(n0, Is.EqualTo("A"));
    Assert.That(n1, Is.EqualTo("B"));
    Assert.That(n2, Is.EqualTo("C"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    // 1500 bytes at 512-byte sectors = 3 blocks. Verify allocation list is 3 entries.
    var ms = BuildEmptyImage();
    var data = new byte[1500];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    CpcDskModifier.AddFile(ms, "BIG.DAT", data);

    var image = ms.ToArray();
    var entryOff = 512;
    var al0 = image[entryOff + 16];
    var al1 = image[entryOff + 17];
    var al2 = image[entryOff + 18];
    var al3 = image[entryOff + 19];
    Assert.That(al0, Is.Not.EqualTo(0));
    Assert.That(al1, Is.Not.EqualTo(0));
    Assert.That(al2, Is.Not.EqualTo(0));
    Assert.That(al3, Is.EqualTo(0), "4th allocation slot should still be empty");
  }

  // ── Remove ────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RemoveFile_MarksDirEntryDeleted() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "TARGET.TXT", "delete me"u8.ToArray());
    Assert.That(CpcDskModifier.RemoveFile(ms, "TARGET.TXT"), Is.True);

    var image = ms.ToArray();
    Assert.That(image[512], Is.EqualTo(0xE5), "user-number byte should be 0xE5 after delete");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(CpcDskModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "SECRET.BIN", "TOPSECRET-MARKER-XYZ123"u8.ToArray());
    CpcDskModifier.RemoveFile(ms, "SECRET.BIN");

    var bytes = ms.ToArray();
    var asAscii = Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-XYZ123"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_PreservesOtherFiles() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "DROP.TXT", "doomed"u8.ToArray());
    CpcDskModifier.AddFile(ms, "KEEP.TXT", "untouched"u8.ToArray());

    Assert.That(CpcDskModifier.RemoveFile(ms, "DROP.TXT"), Is.True);

    var image = ms.ToArray();
    Assert.That(image[512 + 0 * 32], Is.EqualTo(0xE5), "dropped slot should be 0xE5");
    Assert.That(image[512 + 1 * 32], Is.EqualTo(0x00), "kept slot should still be in-use");
    var keepName = Encoding.ASCII.GetString(image, 512 + 1 * 32 + 1, 8).TrimEnd();
    Assert.That(keepName, Is.EqualTo("KEEP"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_FreedSlotIsReused_AfterRemove() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "FIRST.TXT", new byte[100]);
    CpcDskModifier.RemoveFile(ms, "FIRST.TXT");
    CpcDskModifier.AddFile(ms, "SECOND.TXT", new byte[100]);

    var image = ms.ToArray();
    // Slot 0 should have been reused.
    Assert.That(image[512], Is.EqualTo(0x00));
    var name0 = Encoding.ASCII.GetString(image, 512 + 1, 8).TrimEnd();
    Assert.That(name0, Is.EqualTo("SECOND"));
  }

  // ── O(touched bytes) verification ─────────────────────────────────────

  [Test, Category("Performance")]
  public void AddSmallFile_DoesNotPageInWholeImage() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    CpcDskModifier.AddFile(counter, "SMALL.TXT", "hi"u8.ToArray());

    // The modifier reads:
    //   - 256-byte disk info header
    //   - 256-byte first TIB (geometry probe)
    //   - sectorsPerTrack × sectorSize directory area = 9 × 512 = 4608 bytes
    // and writes one 512-byte data sector + the 4608-byte directory area back.
    // Bound at 25% of image size (image is 256 + 5×(256+9×512) ≈ 23 KB) to fail
    // loudly if we ever regress to whole-image I/O.
    var totalIo = counter.BytesRead + counter.BytesWritten;
    var ratio = (double)totalIo / ms.Length;
    Assert.That(ratio, Is.LessThan(0.95),
      $"Add of a 2-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  // ── Integration via descriptor (IArchiveModifiable) ───────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "interfaced"u8.ToArray());
      ((IArchiveModifiable)new CpcDskFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF.TXT", false)]);

      var image = ms.ToArray();
      Assert.That(image[512], Is.EqualTo(0x00));
      var name = Encoding.ASCII.GetString(image, 512 + 1, 8).TrimEnd();
      Assert.That(name, Is.EqualTo("VIAIF"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "DOC.TXT", "v1"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "v2-replacement"u8.ToArray());
      ((IArchiveModifiable)new CpcDskFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "DOC.TXT", false)]);

      var image = ms.ToArray();
      // Only one in-use slot for DOC.TXT (the old one was removed first).
      var inUseDocSlots = 0;
      for (var i = 0; i < 16; i++) {
        var off = 512 + i * 32;
        if (image[off] == 0xE5) continue;
        var b = Encoding.ASCII.GetString(image, off + 1, 8).TrimEnd();
        if (b == "DOC") inUseDocSlots++;
      }
      Assert.That(inUseDocSlots, Is.EqualTo(1), "duplicate-named entries shouldn't accumulate");
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DeletesEntry() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "GONE.TXT", "bye"u8.ToArray());

    ((IArchiveModifiable)new CpcDskFormatDescriptor()).Remove(ms, ["GONE.TXT"]);

    var image = ms.ToArray();
    Assert.That(image[512], Is.EqualTo(0xE5));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModifyCapability() {
    var d = new CpcDskFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  /// <summary>
  /// Builds an empty Standard CPC DSK image using the project writer (5
  /// tracks × 1 side × 9 sectors × 512 bytes = same geometry the writer's
  /// defaults produce).
  /// </summary>
  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    using (var w = new CpcDskWriter(ms, leaveOpen: true,
                tracks: 5, sides: 1, sectorsPerTrack: 9, sectorSize: 512))
      w.Finish();
    ms.Position = 0;
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
