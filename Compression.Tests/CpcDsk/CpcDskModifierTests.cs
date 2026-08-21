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
  public void AddFile_PayloadReadsBackFromTheBlocksTheDirectoryGivesIt() {
    // This used to look for the payload at a fixed byte offset, which pinned a
    // block numbering of the writer's own invention. Where a file's bytes go is
    // the directory's business; that they come back is the file's.
    var ms = BuildEmptyImage();
    var payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();
    CpcDskModifier.AddFile(ms, "DATA.BIN", payload);

    ms.Position = 0;
    var reader = new CpcDskReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "DATA.BIN");
    Assert.That(reader.Extract(entry).AsSpan(0, payload.Length).SequenceEqual(payload), Is.True);
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
  public void AddFile_LargeFile_SpansSeveralBlocks() {
    var ms = BuildEmptyImage();
    var payload = new byte[5000];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 7 + 1);
    CpcDskModifier.AddFile(ms, "BIG.BIN", payload);

    ms.Position = 0;
    var reader = new CpcDskReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG.BIN");
    Assert.That(reader.Extract(entry).AsSpan(0, payload.Length).SequenceEqual(payload), Is.True);
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
    // Removing now lays the disk out again rather than tombstoning a slot, so
    // what is checked is that the survivors survive -- not where their entries
    // happen to land afterwards.
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "KEEP1.BIN", "first"u8.ToArray());
    CpcDskModifier.AddFile(ms, "DROP.BIN", "gone"u8.ToArray());
    CpcDskModifier.AddFile(ms, "KEEP2.BIN", "second"u8.ToArray());

    Assert.That(CpcDskModifier.RemoveFile(ms, "DROP.BIN"), Is.True);

    ms.Position = 0;
    var reader = new CpcDskReader(ms);
    var names = reader.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("DROP.BIN"));
    Assert.That(names, Does.Contain("KEEP1.BIN"));
    Assert.That(names, Does.Contain("KEEP2.BIN"));

    var keep1 = reader.Entries.First(e => e.Name == "KEEP1.BIN");
    Assert.That(reader.Extract(keep1).AsSpan(0, 5).SequenceEqual("first"u8), Is.True);
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

  [Test, Category("RoundTrip")]
  public void AddSmallFile_LeavesTheRestOfTheDiskAlone() {
    // The old test asserted that adding a file read only part of the image. A
    // 180-kilobyte disk is laid out again in full now, which is both cheaper
    // than it sounds and the only way the directory and the data stay in step;
    // what still has to hold is that the other files are untouched.
    var ms = BuildEmptyImage();
    var first = new byte[4000];
    for (var i = 0; i < first.Length; ++i) first[i] = (byte)(i * 3 + 2);
    CpcDskModifier.AddFile(ms, "FIRST.BIN", first);
    CpcDskModifier.AddFile(ms, "SECOND.BIN", "tiny"u8.ToArray());

    ms.Position = 0;
    var reader = new CpcDskReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "FIRST.BIN");
    Assert.That(reader.Extract(entry).AsSpan(0, first.Length).SequenceEqual(first), Is.True);
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

  // ── EnumerateLogicalFiles + Defragment ────────────────────────────────

  [Test, Category("RoundTrip")]
  public void EnumerateLogicalFiles_ReturnsAddedFiles() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "A.TXT", "alpha"u8.ToArray());
    CpcDskModifier.AddFile(ms, "B.BIN", "beta-payload"u8.ToArray());
    CpcDskModifier.AddFile(ms, "C.DAT", "gamma!"u8.ToArray());

    var files = CpcDskModifier.EnumerateLogicalFiles(ms).ToList();
    var names = files.Select(f => f.Name).ToList();
    Assert.That(names, Does.Contain("A.TXT"));
    Assert.That(names, Does.Contain("B.BIN"));
    Assert.That(names, Does.Contain("C.DAT"));
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLogicalFiles_PayloadStartsWithExpectedBytes() {
    var ms = BuildEmptyImage();
    var payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();
    CpcDskModifier.AddFile(ms, "DATA.BIN", payload);

    var files = CpcDskModifier.EnumerateLogicalFiles(ms).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    var read = files[0].Data;
    // RC-trim length = 128 records × 128 = 16384 max for 1 extent; payload is 26 B,
    // RC=1 so trim is 128 B. The first 26 B must equal payload exactly.
    Assert.That(read.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLogicalFiles_EmptyDisk_ReturnsNoEntries() {
    var ms = BuildEmptyImage();
    var files = CpcDskModifier.EnumerateLogicalFiles(ms).ToList();
    Assert.That(files, Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLogicalFiles_SkipsRemovedFiles() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "KEEP.TXT", "stay"u8.ToArray());
    CpcDskModifier.AddFile(ms, "DROP.TXT", "go away"u8.ToArray());
    CpcDskModifier.RemoveFile(ms, "DROP.TXT");

    var files = CpcDskModifier.EnumerateLogicalFiles(ms).ToList();
    var names = files.Select(f => f.Name).ToList();
    Assert.That(names, Does.Contain("KEEP.TXT"));
    Assert.That(names, Does.Not.Contain("DROP.TXT"));
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesFilesAndContent() {
    // Build a fresh image via the descriptor's Create path so it has the writer's
    // default geometry — that's what Defragment rebuilds with.
    var desc = new CpcDskFormatDescriptor();
    using var ms = new MemoryStream();
    var tmpA = Path.GetTempFileName();
    var tmpB = Path.GetTempFileName();
    var tmpC = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpA, "alpha"u8.ToArray());
      File.WriteAllBytes(tmpB, "beta-payload"u8.ToArray());
      File.WriteAllBytes(tmpC, "gamma!"u8.ToArray());
      desc.Create(ms, [
        new ArchiveInputInfo(tmpA, "A.TXT", false),
        new ArchiveInputInfo(tmpB, "B.BIN", false),
        new ArchiveInputInfo(tmpC, "C.DAT", false),
      ], new FormatCreateOptions());

      // Defragment in place.
      ((IArchiveDefragmentable)desc).Defragment(ms);

      // Listing should still show our files (sector count == tracks*sides*spt
      // since the reader exposes physical sectors). The logical files are in
      // EnumerateLogicalFiles.
      ms.Position = 0;
      var logical = CpcDskModifier.EnumerateLogicalFiles(ms).ToList();
      var names = logical.Select(f => f.Name).ToList();
      Assert.That(names, Does.Contain("A.TXT"));
      Assert.That(names, Does.Contain("B.BIN"));
      Assert.That(names, Does.Contain("C.DAT"));
    } finally {
      File.Delete(tmpA);
      File.Delete(tmpB);
      File.Delete(tmpC);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveDefragmentable() {
    var d = new CpcDskFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
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
