#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// Unit tests for <see cref="UfsModifier"/> — verifies true random-access I/O
/// over an existing UFS1 image: only the superblock, CG header (bitmaps), the
/// new inode slot, the root dir block, and the file's data blocks should be
/// touched. Round-trip + ByteCountingStream perf budget.
/// </summary>
[TestFixture]
public class UfsModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "hello.txt", "world-ufs"u8.ToArray());

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "hello.txt");
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("world-ufs"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "a.txt", "A-data"u8.ToArray());
    UfsModifier.AddFile(ms, "b.txt", "B-data-longer"u8.ToArray());
    UfsModifier.AddFile(ms, "c.txt", "C-data-longest-of-all-three"u8.ToArray());

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries.Keys, Is.EquivalentTo(new[] { "a.txt", "b.txt", "c.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["a.txt"])), Is.EqualTo("A-data"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["b.txt"])), Is.EqualTo("B-data-longer"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entries["c.txt"])), Is.EqualTo("C-data-longest-of-all-three"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesPriorEntries() {
    // Seed an image via the writer, then mutate it with the modifier.
    var w = new UfsWriter();
    w.AddFile("seed.txt", "SEED"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    UfsModifier.AddFile(ms, "added.txt", "ADDED"u8.ToArray());

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var byName = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(byName.Keys, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(byName["seed.txt"])), Is.EqualTo("SEED"));
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(byName["added.txt"])), Is.EqualTo("ADDED"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFileSpanningMultipleBlocks() {
    using var ms = BuildEmptyImage();
    var data = new byte[24 * 1024]; // 3 blocks at 8 KiB
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)((i * 13) & 0xFF);
    UfsModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "big.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "old.txt", new byte[16 * 1024]); // 2 blocks

    Assert.That(UfsModifier.RemoveFile(ms, "old.txt"), Is.True);

    UfsModifier.AddFile(ms, "new.txt", new byte[16 * 1024]);

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("old.txt"));
    Assert.That(names, Does.Contain("new.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    using var ms = BuildEmptyImage();
    Assert.That(UfsModifier.RemoveFile(ms, "ghost"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "secret.txt", "TOPSECRET-MARKER-UFS"u8.ToArray());
    UfsModifier.RemoveFile(ms, "secret.txt", wipeData: true);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-UFS"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NoWipe_LeavesDataBytes() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "secret.txt", "LINGER-MARKER-UFS"u8.ToArray());
    UfsModifier.RemoveFile(ms, "secret.txt", wipeData: false);

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Contain("LINGER-MARKER-UFS"));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_DuplicateName_Throws() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "dup.txt", "first"u8.ToArray());
    Assert.Throws<IOException>(() => UfsModifier.AddFile(ms, "dup.txt", "second"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_Oversize_Throws() {
    using var ms = BuildEmptyImage();
    // 12 direct blocks × 8 KiB = 96 KiB max. 13 × 8 = 104 KiB exceeds.
    var huge = new byte[104 * 1024];
    Assert.Throws<IOException>(() => UfsModifier.AddFile(ms, "huge.bin", huge));
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    using var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    UfsModifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    // 16 MiB image (writer floor); per-file touch budget is roughly:
    // SB read (1376) + a few small SB writes, CG block read+write (8 KiB),
    // root dir block read+write (8 KiB), inode read+write (128 each),
    // data block write (8 KiB), fs_cs summary read+write (16). ≤ 64 KiB.
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P2} of the image; should be O(touched bytes).");
  }

  [Test, Category("Performance")]
  public void RemoveSmallFile_DoesNotScaleWithImageSize() {
    using var ms = BuildEmptyImage();
    UfsModifier.AddFile(ms, "tiny.txt", "x"u8.ToArray());
    var counter = new ByteCountingStream(ms);
    Assert.That(UfsModifier.RemoveFile(counter, "tiny.txt"), Is.True);

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
      ((IArchiveModifiable)new UfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);

      ms.Position = 0;
      using var reader = new UfsReader(ms);
      var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("via.txt"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplacesExistingByName() {
    var w = new UfsWriter();
    w.AddFile("dup.txt", "OLD"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "NEW"u8.ToArray());
      ((IArchiveModifiable)new UfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "dup.txt", false)]);

      ms.Position = 0;
      using var reader = new UfsReader(ms);
      var entry = reader.Entries.Single(e => e.Name == "dup.txt");
      Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("NEW"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface() {
    var w = new UfsWriter();
    w.AddFile("remove-me.txt", "DOOMED"u8.ToArray());
    w.AddFile("keep-me.txt", "ALIVE"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    ((IArchiveModifiable)new UfsFormatDescriptor()).Remove(ms, ["remove-me.txt"]);

    ms.Position = 0;
    using var reader = new UfsReader(ms);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("remove-me.txt"));
    Assert.That(names, Does.Contain("keep-me.txt"));
  }

  [Test, Category("HappyPath")]
  public void AddFile_UpdatesFreeCounts() {
    using var ms = BuildEmptyImage();
    var beforeNbfree = ReadCstotalNbfree(ms);
    var beforeNifree = ReadCstotalNifree(ms);

    UfsModifier.AddFile(ms, "x.txt", new byte[16 * 1024]); // 2 blocks

    Assert.That(beforeNbfree - ReadCstotalNbfree(ms), Is.EqualTo(2L),
      "fs_cstotal.nbfree should drop by 2 after a 2-block file is added");
    Assert.That(beforeNifree - ReadCstotalNifree(ms), Is.EqualTo(1L),
      "fs_cstotal.nifree should drop by 1 after a file is added");
  }

  [Test, Category("HappyPath")]
  public void RemoveFile_RestoresFreeCounts() {
    using var ms = BuildEmptyImage();
    var beforeNbfree = ReadCstotalNbfree(ms);
    var beforeNifree = ReadCstotalNifree(ms);

    UfsModifier.AddFile(ms, "x.txt", new byte[16 * 1024]);
    Assert.That(UfsModifier.RemoveFile(ms, "x.txt"), Is.True);

    Assert.That(ReadCstotalNbfree(ms), Is.EqualTo(beforeNbfree));
    Assert.That(ReadCstotalNifree(ms), Is.EqualTo(beforeNifree));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new UfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    // Default writer geometry: 16 MiB image, 8 KiB blocks, single CG.
    // Seed with a throwaway entry so the writer materialises a populated
    // root dir block; the modifier operates on top of it.
    var w = new UfsWriter();
    var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms;
  }

  private static long ReadCstotalNbfree(Stream image) {
    var savedPos = image.Position;
    try {
      image.Position = 8192 + 1008 + 8; // fs_cstotal.cs_nbfree at +8
      var buf = new byte[8];
      image.ReadExactly(buf);
      return BinaryPrimitives.ReadInt64LittleEndian(buf);
    } finally {
      image.Position = savedPos;
    }
  }

  private static long ReadCstotalNifree(Stream image) {
    var savedPos = image.Position;
    try {
      image.Position = 8192 + 1008 + 16; // fs_cstotal.cs_nifree at +16
      var buf = new byte[8];
      image.ReadExactly(buf);
      return BinaryPrimitives.ReadInt64LittleEndian(buf);
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
