using System.Text;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

[TestFixture]
public class ZpaqTests {
  // ── Archive building helpers ─────────────────────────────────────────────

  /// <summary>
  /// Writes a minimal ZPAQ block header ("zPQ" + level + type) to <paramref name="ms"/>.
  /// </summary>
  private static void WriteBlockHeader(MemoryStream ms, byte type, byte level = ZpaqConstants.Level1) {
    ms.Write(ZpaqConstants.BlockPrefix);
    ms.WriteByte(level);
    ms.WriteByte(type);
  }

  /// <summary>
  /// Serialises a ZPAQ level-1 journaling 'c' block payload:
  ///   8 bytes transaction FILETIME
  ///   for each file: attr(1) + name(utf8 NUL) + size(8 LE)
  ///   0xFF end-of-block
  /// </summary>
  private static void WriteHeaderPayload(
      MemoryStream                                 ms,
      long                                         fileTime,
      IEnumerable<(string Name, long Size, byte Attr)> files) {
    ms.Write(BitConverter.GetBytes(fileTime));
    foreach (var (name, size, attr) in files) {
      ms.WriteByte(attr);
      ms.Write(Encoding.UTF8.GetBytes(name));
      ms.WriteByte(0); // null terminator
      ms.Write(BitConverter.GetBytes(size));
    }
    ms.WriteByte(0xFF); // end-of-block
  }

  /// <summary>Converts a UTC <see cref="DateTime"/> to a Windows FILETIME long.</summary>
  private static long ToFileTime(DateTime utc) =>
    utc.ToFileTimeUtc();

  // ── Invalid input ────────────────────────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void Constructor_NullStream_ThrowsArgumentNullException() {
    Assert.That(
      () => new ZpaqReader(null!),
      Throws.ArgumentNullException);
  }

  [Category("Exception")]
  [Test]
  public void Constructor_WriteOnlyStream_ThrowsArgumentException() {
    using var ms = new MemoryStream([], writable: true);
    var wo = new WriteOnlyStreamWrapper(ms);
    Assert.That(
      () => new ZpaqReader(wo),
      Throws.ArgumentException);
  }

  [Category("Exception")]
  [Test]
  public void Constructor_NoZpaqBlocks_ThrowsInvalidDataException() {
    var data = "This is not a ZPAQ archive."u8.ToArray();
    using var ms = new MemoryStream(data);
    Assert.That(
      () => new ZpaqReader(ms),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Empty / minimal archives ─────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void EmptyHeaderBlock_ProducesNoEntries() {
    // A 'c' block with timestamp but no file entries.
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), []);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void SingleFile_ParsedCorrectly() {
    var ts   = new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    var ft  = ToFileTime(ts);

    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ft, [("readme.txt", 42L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));

    var entry = reader.Entries[0];
    Assert.That(entry.FileName,    Is.EqualTo("readme.txt"));
    Assert.That(entry.Size,        Is.EqualTo(42L));
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.Version,     Is.GreaterThan(0));

    // Timestamp should be within a 2-second window of the source value.
    Assert.That(entry.LastModified, Is.Not.Null);
    Assert.That(
      Math.Abs((entry.LastModified!.Value.ToUniversalTime() - ts).TotalSeconds),
      Is.LessThanOrEqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void DirectoryEntry_FlaggedCorrectly() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("subdir/", 0L, 0x10)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsDirectory, Is.True);
    Assert.That(reader.Entries[0].Size, Is.EqualTo(0L));
  }

  [Category("HappyPath")]
  [Test]
  public void MultipleFilesInOneTransaction_AllParsed() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [
      ("a.txt", 10L, 0x20),
      ("b.txt", 20L, 0x20),
      ("c.txt", 30L, 0x20),
    ]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    var names = reader.Entries.Select(e => e.FileName).ToList();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.txt"));
    Assert.That(names, Does.Contain("c.txt"));
  }

  // ── Journaling (multi-transaction) ───────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void LaterTransaction_SupersedesEarlierForSameFile() {
    // Transaction 1: file.txt has size 100.
    // Transaction 2: file.txt has size 200 (update).
    // Expected: Entries contains size 200.
    using var ms = new MemoryStream();

    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
      [("file.txt", 100L, 0x20)]);

    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
      [("file.txt", 200L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(200L));
  }

  [Category("HappyPath")]
  [Test]
  public void TwoTransactions_DifferentFiles_BothPresent() {
    using var ms = new MemoryStream();

    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("first.bin", 5L, 0x20)]);

    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("second.bin", 7L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
  }

  // ── Data block / CompressedSize ──────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void DataBlock_CompressedSizePopulated() {
    const int dataPayloadBytes = 256;

    using var ms = new MemoryStream();

    // Header block with one file.
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("data.bin", 1000L, 0x20)]);

    // Data block with a known-size payload.
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeData);
    ms.Write(new byte[dataPayloadBytes]); // arbitrary payload

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    // CompressedSize should reflect the data block bytes we wrote.
    Assert.That(reader.Entries[0].CompressedSize, Is.GreaterThan(0));
  }

  // ── Extract ──────────────────────────────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void Extract_AlwaysThrowsNotSupportedException() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("f.txt", 1L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(
      () => reader.Extract(reader.Entries[0]),
      Throws.InstanceOf<NotSupportedException>());
  }

  // ── Non-seekable stream ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void NonSeekableStream_ParsedWithoutError() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("ns.txt", 99L, 0x20)]);

    ms.Position = 0;
    using var nsStream = new NonSeekableStreamWrapper(ms);
    using var reader   = new ZpaqReader(nsStream, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("ns.txt"));
    Assert.That(reader.Entries[0].Size,     Is.EqualTo(99L));
  }

  // ── leaveOpen ────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void LeaveOpen_True_StreamRemainsUsable() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("keep.txt", 1L, 0x20)]);
    ms.Position = 0;

    using (var reader = new ZpaqReader(ms, leaveOpen: true)) {
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
    }

    // Stream must still be accessible after reader disposal.
    Assert.That(ms.CanRead, Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void LeaveOpen_False_StreamDisposedWithReader() {
    var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("close.txt", 1L, 0x20)]);
    ms.Position = 0;

    var reader = new ZpaqReader(ms, leaveOpen: false);
    reader.Dispose();

    Assert.That(ms.CanRead, Is.False);
  }

  // ── Prefixed garbage / robustness ────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void GarbageBeforeBlock_IgnoredSuccessfully() {
    using var ms = new MemoryStream();

    // Write 200 bytes of noise first.
    ms.Write(new byte[200]);

    // Then a valid block.
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("after_noise.txt", 7L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("after_noise.txt"));
  }

  [Category("EdgeCase")]
  [Test]
  public void ZpaqPrefixInPayload_NotMisidentifiedAsNewBlock() {
    // The payload of a 'c' block itself contains the bytes "zPQ" — the reader
    // must not misidentify that sequence as the start of a new block while
    // parsing the payload (the scanner only looks for "zPQ" between blocks).
    using var ms = new MemoryStream();

    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader);

    // Build a payload that embeds "zPQ" inside a filename.
    var ft = ToFileTime(DateTime.UtcNow);
    ms.Write(BitConverter.GetBytes(ft));
    ms.WriteByte(0x20);                      // attr
    ms.Write("file_zPQ_name.txt"u8);
    ms.WriteByte(0);                          // null terminator
    ms.Write(BitConverter.GetBytes(55L));    // size
    ms.WriteByte(0xFF);                      // end-of-block

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    // The entry must be present and its filename must be intact.
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Does.Contain("zPQ"));
  }

  // ── Writer round-trip tests ──────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_SingleFile() {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true))
      w.AddFile("hello.txt", "Hello, ZPAQ!"u8.ToArray());

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(12));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_MultipleFiles() {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true)) {
      w.AddFile("a.txt", new byte[100]);
      w.AddFile("b.txt", new byte[200]);
    }

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var names = reader.Entries.Select(e => e.FileName).ToList();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.txt"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_Directory() {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true)) {
      w.AddDirectory("mydir/");
      w.AddFile("mydir/file.txt", [1, 2, 3]);
    }

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);
    var dirs = reader.Entries.Where(e => e.IsDirectory).ToList();
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(dirs, Has.Count.EqualTo(1));
    Assert.That(files, Has.Count.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Writer_Magic_IsZPQ() {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true))
      w.AddFile("x", [1]);

    ms.Position = 0;
    Span<byte> magic = stackalloc byte[3];
    ms.ReadExactly(magic);
    Assert.That(magic[0], Is.EqualTo((byte)'z'));
    Assert.That(magic[1], Is.EqualTo((byte)'P'));
    Assert.That(magic[2], Is.EqualTo((byte)'Q'));
  }

  // ── Level 2 blocks accepted ───────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Level2Block_ParsedWithoutError() {
    using var ms = new MemoryStream();
    WriteBlockHeader(ms, ZpaqConstants.BlockTypeHeader, ZpaqConstants.Level2);
    WriteHeaderPayload(ms, ToFileTime(DateTime.UtcNow), [("v2.txt", 3L, 0x20)]);

    ms.Position = 0;
    using var reader = new ZpaqReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("v2.txt"));
  }
}

// ── Test stream wrappers ────────────────────────────────────────────────────

/// <summary>Wraps a stream and exposes it as non-seekable.</summary>
internal sealed class NonSeekableStreamWrapper(Stream inner) : Stream {
  public override bool   CanRead  => inner.CanRead;
  public override bool   CanSeek  => false;
  public override bool   CanWrite => inner.CanWrite;
  public override long   Length   => throw new NotSupportedException();
  public override long   Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }
  public override void  Flush()                              => inner.Flush();
  public override int   Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
  public override long  Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void  SetLength(long value)                => throw new NotSupportedException();
  public override void  Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
  protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

/// <summary>Wraps a stream and exposes it as write-only (CanRead = false).</summary>
internal sealed class WriteOnlyStreamWrapper(Stream inner) : Stream {
  public override bool  CanRead  => false;
  public override bool  CanSeek  => false;
  public override bool  CanWrite => true;
  public override long  Length   => throw new NotSupportedException();
  public override long  Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override void  Flush()                              => inner.Flush();
  public override int   Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long  Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void  SetLength(long value)                => throw new NotSupportedException();
  public override void  Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
  protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
