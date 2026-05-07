#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext1;

namespace Compression.Tests.Ext1;

/// <summary>
/// Unit tests for <see cref="Ext1Modifier"/> — verifies true random-access I/O
/// over an existing ext1 image: only the superblock, BGD entry, block + inode
/// bitmaps, the new inode slot, the root dir block, and the file's data
/// blocks should be touched. Round-trip + ByteCountingStream perf budget.
/// </summary>
[TestFixture]
public class Ext1ModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "hello.txt", "world-ext1"u8.ToArray());

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("world-ext1"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "a.txt", "A-data"u8.ToArray());
    Ext1Modifier.AddFile(ms, "b.txt", "B-data-longer"u8.ToArray());
    Ext1Modifier.AddFile(ms, "c.txt", "C-data-longest-of-all-three"u8.ToArray());

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries.Keys, Is.EquivalentTo(new[] { "a.txt", "b.txt", "c.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["a.txt"])), Is.EqualTo("A-data"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["b.txt"])), Is.EqualTo("B-data-longer"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["c.txt"])), Is.EqualTo("C-data-longest-of-all-three"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesPriorEntries() {
    // First write a file via the writer, then mutate the image with the modifier.
    var w = new Ext1Writer();
    w.AddFile("seed.txt", "SEED"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    Ext1Modifier.AddFile(ms, "added.txt", "ADDED"u8.ToArray());

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var byName = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(byName.Keys, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(byName["seed.txt"])), Is.EqualTo("SEED"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(byName["added.txt"])), Is.EqualTo("ADDED"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFileSpanningMultipleBlocks() {
    using var ms = BuildEmptyImage();
    var data = new byte[5 * 1024]; // 5 blocks at 1 KiB
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)((i * 13) & 0xFF);
    Ext1Modifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "big.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "old.txt", new byte[3 * 1024]);

    Assert.That(Ext1Modifier.RemoveFile(ms, "old.txt"), Is.True);

    Ext1Modifier.AddFile(ms, "new.txt", new byte[3 * 1024]);

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("old.txt"));
    Assert.That(names, Does.Contain("new.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    using var ms = BuildEmptyImage();
    Assert.That(Ext1Modifier.RemoveFile(ms, "ghost"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "secret.txt", "TOPSECRET-MARKER-EXT1"u8.ToArray());
    Ext1Modifier.RemoveFile(ms, "secret.txt", wipeData: true);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-EXT1"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NoWipe_LeavesDataBytes() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "secret.txt", "LINGER-MARKER-EXT1"u8.ToArray());
    Ext1Modifier.RemoveFile(ms, "secret.txt", wipeData: false);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Contain("LINGER-MARKER-EXT1"));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_DuplicateName_Throws() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "dup.txt", "first"u8.ToArray());
    Assert.Throws<IOException>(() => Ext1Modifier.AddFile(ms, "dup.txt", "second"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_Oversize_Throws() {
    using var ms = BuildEmptyImage();
    var huge = new byte[13 * 1024]; // 13 KiB > 12 direct blocks × 1 KiB
    Assert.Throws<IOException>(() => Ext1Modifier.AddFile(ms, "huge.bin", huge));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataBlocks() {
    using var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    Ext1Modifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Touch budget: superblock (264 read + 8 read+write), BGD (12 read, 4 read+write),
    // block bitmap (1 KiB read+write), inode bitmap (1 KiB read+write),
    // inode table 1 read (root) + 1 write (new inode) (128 each),
    // dir block read+write (1 KiB), data block write (1 KiB) ≈ 8 KiB.
    // Bound at 16 KiB to flag any regression to whole-image I/O.
    Assert.That(totalIo, Is.LessThan(16 * 1024),
      $"Add of a 1-byte file should touch < 16 KiB; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    using var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    Ext1Modifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    // 4 MiB image; ≤ 16 KiB budget is ~0.4%. Bound at 1%.
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P2} of the image; should be O(touched bytes).");
  }

  [Test, Category("Performance")]
  public void RemoveSmallFile_DoesNotScaleWithImageSize() {
    using var ms = BuildEmptyImage();
    Ext1Modifier.AddFile(ms, "tiny.txt", "x"u8.ToArray());
    var counter = new ByteCountingStream(ms);
    Assert.That(Ext1Modifier.RemoveFile(counter, "tiny.txt"), Is.True);

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.01),
      $"Remove of a 1-byte file touched {ratio:P2} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    using var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-iface"u8.ToArray());
      ((IArchiveModifiable)new Ext1FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);

      ms.Position = 0;
      using var reader = new Ext1Reader(ms);
      var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("via.txt"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    // Seed an image, then re-add the same name via the descriptor — old data
    // should be removed and replaced with the new payload.
    var w = new Ext1Writer();
    w.AddFile("dup.txt", "OLD"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "NEW"u8.ToArray());
      ((IArchiveModifiable)new Ext1FormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "dup.txt", false)]);

      ms.Position = 0;
      using var reader = new Ext1Reader(ms);
      var entry = reader.Entries.Single(e => e.Name == "dup.txt");
      Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("NEW"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface() {
    var w = new Ext1Writer();
    w.AddFile("remove-me.txt", "DOOMED"u8.ToArray());
    w.AddFile("keep-me.txt", "ALIVE"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    ((IArchiveModifiable)new Ext1FormatDescriptor()).Remove(ms, ["remove-me.txt"]);

    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("remove-me.txt"));
    Assert.That(names, Does.Contain("keep-me.txt"));
  }

  [Test, Category("HappyPath")]
  public void AddFile_UpdatesFreeCounts() {
    using var ms = BuildEmptyImage();
    var beforeFreeBlocks = ReadSuperblockUInt32(ms, 12);
    var beforeFreeInodes = ReadSuperblockUInt32(ms, 16);

    Ext1Modifier.AddFile(ms, "x.txt", new byte[2 * 1024]); // 2 blocks

    var afterFreeBlocks = ReadSuperblockUInt32(ms, 12);
    var afterFreeInodes = ReadSuperblockUInt32(ms, 16);
    Assert.That(beforeFreeBlocks - afterFreeBlocks, Is.EqualTo(2u),
      "free_blocks_count should drop by 2 after a 2-block file is added");
    Assert.That(beforeFreeInodes - afterFreeInodes, Is.EqualTo(1u),
      "free_inodes_count should drop by 1 after a file is added");
  }

  [Test, Category("HappyPath")]
  public void RemoveFile_RestoresFreeCounts() {
    using var ms = BuildEmptyImage();
    var beforeFreeBlocks = ReadSuperblockUInt32(ms, 12);
    var beforeFreeInodes = ReadSuperblockUInt32(ms, 16);

    Ext1Modifier.AddFile(ms, "x.txt", new byte[2 * 1024]);
    Assert.That(Ext1Modifier.RemoveFile(ms, "x.txt"), Is.True);

    Assert.That(ReadSuperblockUInt32(ms, 12), Is.EqualTo(beforeFreeBlocks));
    Assert.That(ReadSuperblockUInt32(ms, 16), Is.EqualTo(beforeFreeInodes));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    // Default writer geometry: 4 MiB image, 1 KiB blocks, single block group.
    // We seed a single throwaway entry so the writer materialises a populated
    // root dir block; the modifier will operate on top of it.
    var ms = new MemoryStream();
    ms.Write(new Ext1Writer().Build());
    return ms;
  }

  private static uint ReadSuperblockUInt32(Stream image, int superblockOffset) {
    var savedPos = image.Position;
    try {
      image.Position = 1024 + superblockOffset;
      var buf = new byte[4];
      image.ReadExactly(buf);
      return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf);
    } finally {
      image.Position = savedPos;
    }
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
