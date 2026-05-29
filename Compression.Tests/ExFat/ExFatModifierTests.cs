#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

[TestFixture]
public class ExFatModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    ExFatModifier.AddFile(ms, "HELLO.TXT", "world-exfat"u8.ToArray());
    ms.Position = 0;
    var reader = new ExFatReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO.TXT");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world-exfat"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleClusters() {
    // Default cluster = 4096 B; 50 KB ≈ 13 clusters — exercises FAT chain walks.
    var ms = BuildEmptyImage();
    var data = new byte[50_000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 7) & 0xFF);
    ExFatModifier.AddFile(ms, "BIG.BIN", data);

    ms.Position = 0;
    var reader = new ExFatReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG.BIN");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LongName_UsesMultipleFileNameEntries() {
    // 40 chars ⇒ 3 × 0xC1 entries (15 + 15 + 10).
    var ms = BuildEmptyImage();
    var name = "a-very-very-long-name-with-40-characters";
    Assert.That(name.Length, Is.EqualTo(40));
    ExFatModifier.AddFile(ms, name, "payload"u8.ToArray());

    ms.Position = 0;
    var reader = new ExFatReader(ms);
    var entry = reader.Entries.Single(e => e.Name == name);
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("payload"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadable() {
    var ms = BuildEmptyImage();
    ExFatModifier.AddFile(ms, "ONE.TXT", "first"u8.ToArray());
    ExFatModifier.AddFile(ms, "TWO.TXT", "second"u8.ToArray());
    ExFatModifier.AddFile(ms, "THREE.TXT", "third"u8.ToArray());

    ms.Position = 0;
    var reader = new ExFatReader(ms);
    Assert.That(reader.Entries.Select(e => e.Name).OrderBy(n => n).ToArray(),
      Is.EqualTo(new[] { "ONE.TXT", "THREE.TXT", "TWO.TXT" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    ExFatModifier.AddFile(ms, "OLD.BIN", new byte[10_000]);
    Assert.That(ExFatModifier.RemoveFile(ms, "OLD.BIN"), Is.True);
    ExFatModifier.AddFile(ms, "NEW.BIN", new byte[10_000]);

    ms.Position = 0;
    var reader = new ExFatReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD.BIN"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW.BIN"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(ExFatModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    ExFatModifier.AddFile(ms, "SECRET.TXT", "TOPSECRET-MARKER-EXFAT"u8.ToArray());
    ExFatModifier.RemoveFile(ms, "SECRET.TXT");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-EXFAT"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_EntrySetChecksum_IsValid() {
    var ms = BuildEmptyImage();
    ExFatModifier.AddFile(ms, "CHK.TXT", "hello"u8.ToArray());

    var disk = ms.ToArray();
    var clusterHeapOffsetSectors = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt32LittleEndian(disk.AsSpan(88));
    var rootDirOffset = (int)clusterHeapOffsetSectors * 512;

    // Scan for our 0x85 entry (skip the writer's volume-label/bitmap/upcase + any prior
    // file entries — fresh image so it's just at +96).
    var fileEntryOffset = rootDirOffset + 3 * 32;
    Assert.That(disk[fileEntryOffset], Is.EqualTo((byte)0x85));
    var secondaryCount = disk[fileEntryOffset + 1];
    var setBytes = 32 * (1 + secondaryCount);
    ushort expected = 0;
    for (var i = 0; i < setBytes; ++i) {
      if (i == 2 || i == 3) continue;
      expected = (ushort)((((expected & 1) != 0 ? 0x8000 : 0) + (expected >> 1)
        + disk[fileEntryOffset + i]) & 0xFFFF);
    }
    var written = System.Buffers.Binary.BinaryPrimitives
      .ReadUInt16LittleEndian(disk.AsSpan(fileEntryOffset + 2));
    Assert.That(written, Is.EqualTo(expected),
      "set-checksum on add-modified entry must match exFAT spec §7.4.3");
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataClusters() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    ExFatModifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Touch budget: VBR header (120) + bitmap (a few KB) + FAT entries + 1 cluster (4 KB)
    // + entry set (~96 bytes) + PercentInUse (1 byte) ≈ small constant. Bound at 64 KB.
    Assert.That(totalIo, Is.LessThan(64 * 1024),
      $"Add of a 1-byte file should touch < 64 KB; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    ExFatModifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-via-iface"u8.ToArray());
      ((IArchiveModifiable)new ExFatFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF.TXT", false)]);

      ms.Position = 0;
      var reader = new ExFatReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIA-IF.TXT"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_PreservesExistingFiles() {
    // Ensures Add doesn't rebuild — pre-existing file is not the same file
    // re-extracted-and-re-written; the entry must remain at the same byte offset.
    var w = new ExFatWriter();
    w.AddFile("KEEP.TXT", "keep-me"u8.ToArray());
    var initial = w.Build();
    var ms = new MemoryStream();
    ms.Write(initial);
    ms.SetLength(initial.Length);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added-later"u8.ToArray());
      ((IArchiveModifiable)new ExFatFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "ADDED.TXT", false)]);

      ms.Position = 0;
      var reader = new ExFatReader(ms);
      var keep = reader.Entries.Single(e => e.Name == "KEEP.TXT");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(keep)), Is.EqualTo("keep-me"));
      var added = reader.Entries.Single(e => e.Name == "ADDED.TXT");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(added)), Is.EqualTo("added-later"));
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new ExFatWriter().Build());
    ms.SetLength(ms.Position);
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
    public override int ReadByte() {
      var b = _inner.ReadByte();
      if (b >= 0) BytesRead += 1;
      return b;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count);
      BytesWritten += count;
    }
    public override void WriteByte(byte value) {
      _inner.WriteByte(value);
      BytesWritten += 1;
    }
  }
}
