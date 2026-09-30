#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace Compression.Tests.Zip;

/// <summary>
/// Streaming <see cref="ZipModifier.AddFile(Stream, string, Stream, DateTime?)"/> and the
/// descriptor path built on it. Content, CRC and timestamps are checked with
/// <see cref="SysZipArchive"/> and <see cref="System.IO.Hashing.Crc32"/> as independent
/// oracles, never with our own reader alone.
/// </summary>
[TestFixture]
public class ZipModifierStreamingTests {

  private const int LfhVersionNeeded = 4;
  private const int LfhMethod = 8;
  private const int LfhCompressedSize = 18;
  private const int LfhUncompressedSize = 22;
  private const int LfhNameLength = 26;
  private const int LfhExtraLength = 28;

  // ── Content and CRC ────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_GivenCompressibleStream_WhenAdded_ThenOracleReadsContentAndCrc() {
    var data = Compressible(300_000);
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "text.txt", new MemoryStream(data));

    var entry = OracleEntry(zip, "text.txt");
    Assert.That(entry.Content, Is.EqualTo(data));
    Assert.That(entry.Crc32, Is.EqualTo(System.IO.Hashing.Crc32.HashToUInt32(data)));
    Assert.That(entry.CompressedLength, Is.LessThan(data.Length), "compressible data must be deflated");
  }

  [TestCase(1, TestName = "AddFile_GivenOneByteStream_WhenAdded_ThenOracleReadsIt")]
  [TestCase(32767, TestName = "AddFile_GivenStreamOneBelowDeflateBlock_WhenAdded_ThenOracleReadsIt")]
  [TestCase(32768, TestName = "AddFile_GivenStreamOfExactlyOneDeflateBlock_WhenAdded_ThenOracleReadsIt")]
  [TestCase((1 << 20) + 1, TestName = "AddFile_GivenStreamOneAboveCopyBuffer_WhenAdded_ThenOracleReadsIt")]
  [Category("RoundTrip")]
  public void AddFile_GivenBoundarySizes_WhenAdded_ThenOracleReadsContent(int size) {
    var data = Compressible(size);
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "b.bin", new MemoryStream(data));

    var entry = OracleEntry(zip, "b.bin");
    Assert.That(entry.Content, Is.EqualTo(data));
    Assert.That(entry.Crc32, Is.EqualTo(System.IO.Hashing.Crc32.HashToUInt32(data)));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenEmptyStream_WhenAdded_ThenStoredWithZeroSizes() {
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "empty.txt", new MemoryStream());

    var entry = OracleEntry(zip, "empty.txt");
    Assert.That(entry.Content, Is.Empty);
    Assert.That(entry.CompressedLength, Is.Zero);
    Assert.That(entry.Crc32, Is.Zero);
    Assert.That(OurEntry(zip, "empty.txt").CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenIncompressibleSeekableStream_WhenAdded_ThenStoredVerbatim() {
    var data = Random(200_000, seed: 7);
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "noise.bin", new MemoryStream(data));

    var ours = OurEntry(zip, "noise.bin");
    Assert.That(ours.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(ours.CompressedSize, Is.EqualTo(data.Length));
    var entry = OracleEntry(zip, "noise.bin");
    Assert.That(entry.Content, Is.EqualTo(data));
    Assert.That(entry.Crc32, Is.EqualTo(System.IO.Hashing.Crc32.HashToUInt32(data)));
    Assert.That(zip.Length, Is.LessThan(data.Length + 400), "the discarded Deflate attempt must be truncated away");
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenIncompressibleNonSeekableStream_WhenAdded_ThenKeptDeflatedAndValid() {
    var data = Random(100_000, seed: 8);
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "noise.bin", new ForwardOnlyStream(data));

    Assert.That(OurEntry(zip, "noise.bin").CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate),
      "a forward-only source cannot be re-read for Store");
    var entry = OracleEntry(zip, "noise.bin");
    Assert.That(entry.Content, Is.EqualTo(data));
    Assert.That(entry.Crc32, Is.EqualTo(System.IO.Hashing.Crc32.HashToUInt32(data)));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenStreamPositionedMidway_WhenAdded_ThenOnlyRemainderIsStored() {
    var data = Compressible(10_000);
    using var source = new MemoryStream(data) { Position = 4_000 };
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "tail.txt", source);

    Assert.That(OracleEntry(zip, "tail.txt").Content, Is.EqualTo(data[4_000..]));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenIncompressibleStreamPositionedMidway_WhenStoreFallback_ThenRewindsToStartPositionNotZero() {
    var data = Random(50_000, seed: 9);
    using var source = new MemoryStream(data) { Position = 20_000 };
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "tail.bin", source);

    Assert.That(OurEntry(zip, "tail.bin").CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(OracleEntry(zip, "tail.bin").Content, Is.EqualTo(data[20_000..]));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenByteArrayOverload_WhenAdded_ThenMatchesStreamOverloadByteForByte() {
    var data = Compressible(70_000);
    var stamp = new DateTime(2024, 2, 29, 23, 59, 58);
    using var viaBytes = SeedZip();
    using var viaStream = SeedZip();

    ZipModifier.AddFile(viaBytes, "x.txt", data, stamp);
    ZipModifier.AddFile(viaStream, "x.txt", new MemoryStream(data), stamp);

    Assert.That(viaBytes.ToArray(), Is.EqualTo(viaStream.ToArray()));
  }

  // ── Timestamps ─────────────────────────────────────────────────────

  [TestCase(31, 30, TestName = "AddFile_GivenOddSecond_WhenAdded_ThenOracleSeesItRoundedDownToEven")]
  [TestCase(30, 30, TestName = "AddFile_GivenEvenSecond_WhenAdded_ThenOracleSeesItExactly")]
  [TestCase(59, 58, TestName = "AddFile_GivenLastSecondOfMinute_WhenAdded_ThenOracleSeesSecond58")]
  [TestCase(0, 0, TestName = "AddFile_GivenSecondZero_WhenAdded_ThenOracleSeesSecondZero")]
  [Category("RoundTrip")]
  public void AddFile_GivenTimestamp_WhenAdded_ThenDosTwoSecondGranularityIsPreserved(int second, int expectedSecond) {
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "t.txt", new MemoryStream("x"u8.ToArray()), new DateTime(2023, 5, 17, 13, 45, second));

    Assert.That(OracleEntry(zip, "t.txt").LastWrite, Is.EqualTo(new DateTime(2023, 5, 17, 13, 45, expectedSecond)));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenNoTimestamp_WhenAdded_ThenDosEpochIsUsed() {
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "t.txt", new MemoryStream("x"u8.ToArray()));

    Assert.That(OracleEntry(zip, "t.txt").LastWrite, Is.EqualTo(new DateTime(1980, 1, 1)));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenTimestampBeforeDosEpoch_WhenAdded_ThenClampedToDosEpoch() {
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "t.txt", new MemoryStream("x"u8.ToArray()), new DateTime(1970, 1, 1));

    Assert.That(OracleEntry(zip, "t.txt").LastWrite, Is.EqualTo(new DateTime(1980, 1, 1)));
  }

  // ── Existing content ───────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_GivenArchiveWithCommentAndEntries_WhenAdded_ThenPrefixIsByteIdenticalAndCommentKept() {
    using var zip = new MemoryStream();
    using (var sys = new SysZipArchive(zip, SysZipArchiveMode.Create, leaveOpen: true)) {
      sys.Comment = "archive comment survives";
      WriteSysEntry(sys, "a.txt", Compressible(5_000));
      WriteSysEntry(sys, "dir/b.bin", Random(3_000, seed: 3));
    }
    var oldCdOffset = CentralDirectoryOffset(zip);
    var prefix = zip.ToArray()[..(int)oldCdOffset];

    ZipModifier.AddFile(zip, "new.txt", new MemoryStream(Compressible(4_000)));

    Assert.That(zip.ToArray()[..(int)oldCdOffset], Is.EqualTo(prefix), "existing local headers + data must not move or change");
    zip.Position = 0;
    using var check = new SysZipArchive(zip, SysZipArchiveMode.Read, leaveOpen: true);
    Assert.That(check.Comment, Is.EqualTo("archive comment survives"));
    Assert.That(check.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "a.txt", "dir/b.bin", "new.txt" }));
    Assert.That(ReadAll(check.GetEntry("a.txt")!), Is.EqualTo(Compressible(5_000)));
    Assert.That(ReadAll(check.GetEntry("dir/b.bin")!), Is.EqualTo(Random(3_000, seed: 3)));
  }

  [TestCase(0, TestName = "AddFile_GivenSourceFailingOnFirstRead_ThenArchiveIsLeftByteIdentical")]
  [TestCase(3 << 20, TestName = "AddFile_GivenSourceFailingAfterPayloadWasWritten_ThenArchiveIsLeftByteIdentical")]
  [Category("RoundTrip")]
  public void AddFile_GivenSourceThatFails_ThenArchiveIsRolledBack(int failAfter) {
    using var zip = SeedZip();
    ZipModifier.AddFile(zip, "kept.txt", Compressible(1_000));
    var before = zip.ToArray();

    Assert.Throws<IOException>(() =>
      ZipModifier.AddFile(zip, "broken.bin", new FailingStream(HalfEntropy(4 << 20, seed: 12), failAfter)));

    Assert.That(zip.ToArray(), Is.EqualTo(before));
    Assert.That(OracleEntry(zip, "kept.txt").Content, Is.EqualTo(Compressible(1_000)));
  }

  [Test, Category("RoundTrip")]
  public void DescriptorAdd_GivenExistingEntryOfSameName_WhenAdded_ThenReplacedNotDuplicated() {
    using var zip = SeedZip();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "second version"u8.ToArray());

      ((IArchiveModifiable)new ZipFormatDescriptor()).Add(zip, [new ArchiveInputInfo(tmp, "seed.txt", false)]);

      zip.Position = 0;
      using var check = new SysZipArchive(zip, SysZipArchiveMode.Read, leaveOpen: true);
      Assert.That(check.Entries.Count(e => e.FullName == "seed.txt"), Is.EqualTo(1));
      Assert.That(Encoding.ASCII.GetString(ReadAll(check.GetEntry("seed.txt")!)), Is.EqualTo("second version"));
    } finally { File.Delete(tmp); }
  }

  // ── Descriptor path ────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void DescriptorAdd_GivenOnDiskFile_WhenAdded_ThenItsLastWriteTimeIsKept() {
    using var zip = SeedZip();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, Compressible(2_000));
      File.SetLastWriteTime(tmp, new DateTime(2021, 7, 4, 10, 20, 33));

      ((IArchiveModifiable)new ZipFormatDescriptor()).Add(zip, [new ArchiveInputInfo(tmp, "dated.txt", false)]);

      var entry = OracleEntry(zip, "dated.txt");
      Assert.That(entry.LastWrite, Is.EqualTo(new DateTime(2021, 7, 4, 10, 20, 32)));
      Assert.That(entry.Content, Is.EqualTo(Compressible(2_000)));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void DescriptorAdd_GivenInMemoryInput_WhenAdded_ThenContentIsTakenFromMemory() {
    using var zip = SeedZip();

    ((IArchiveModifiable)new ZipFormatDescriptor()).Add(zip,
      [ArchiveInputInfo.InMemory("mem.txt", "from memory"u8)]);

    Assert.That(Encoding.ASCII.GetString(OracleEntry(zip, "mem.txt").Content), Is.EqualTo("from memory"));
  }

  [Test, Category("RoundTrip")]
  public void DescriptorAdd_GivenDirectoryInput_WhenAdded_ThenItIsSkipped() {
    using var zip = SeedZip();
    var before = zip.ToArray();

    ((IArchiveModifiable)new ZipFormatDescriptor()).Add(zip,
      [new ArchiveInputInfo(Path.GetTempPath(), "somedir", IsDirectory: true)]);

    Assert.That(zip.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("RoundTrip")]
  public void DescriptorAdd_GivenMissingFile_WhenAdded_ThenThrowsFileNotFound() {
    using var zip = SeedZip();
    var missing = Path.Combine(Path.GetTempPath(), $"cwb_missing_{Guid.NewGuid():N}.txt");

    Assert.Throws<FileNotFoundException>(() =>
      ((IArchiveModifiable)new ZipFormatDescriptor()).Add(zip, [new ArchiveInputInfo(missing, "m.txt", false)]));
  }

  // ── Streaming and ZIP64 reservation ────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_GivenForwardOnlySource_WhenAdded_ThenCompressedOutputIsWrittenBeforeSourceIsExhausted() {
    // A buffer-everything implementation writes the first payload byte only after the
    // last source byte was read; a streaming one interleaves the two.
    const int total = 16 << 20;
    var data = HalfEntropy(total, seed: 5); // roughly 2:1, so output flows steadily
    var source = new ForwardOnlyStream(data);
    using var inner = SeedZip();
    var archive = new WriteObservingStream(inner, () => source.Consumed);

    ZipModifier.AddFile(archive, "big.txt", source);

    Assert.That(source.Consumed, Is.EqualTo(total));
    Assert.That(archive.ConsumedAtFirstLargeWrite, Is.Not.Null.And.LessThan(total / 4),
      "payload must reach the archive while most of the source is still unread");
    Assert.That(OracleEntry(inner, "big.txt").Content, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenForwardOnlySource_WhenAdded_ThenLocalHeaderReservesZip64AndCentralDirectoryStays32Bit() {
    var data = Compressible(5_000);
    using var zip = SeedZip();
    var lfh = CentralDirectoryOffset(zip);

    ZipModifier.AddFile(zip, "fwd.txt", new ForwardOnlyStream(data));

    var bytes = zip.ToArray();
    var header = bytes.AsSpan((int)lfh);
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(header[LfhVersionNeeded..]), Is.EqualTo(45));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(header[LfhCompressedSize..]), Is.EqualTo(0xFFFFFFFFu));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(header[LfhUncompressedSize..]), Is.EqualTo(0xFFFFFFFFu));
    var extra = Zip64Extra(header);
    Assert.That(extra, Is.Not.Null, "unknown-length source must reserve the ZIP64 extra field");
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(extra!.Value.Span), Is.EqualTo(data.Length), "uncompressed size first (APPNOTE 4.5.3)");
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(extra.Value.Span[8..]), Is.EqualTo(OurEntry(zip, "fwd.txt").CompressedSize));

    var ours = OurEntry(zip, "fwd.txt");
    Assert.That(ours.IsZip64, Is.False, "the central directory needs no ZIP64 for a small entry");
    Assert.That(OracleEntry(zip, "fwd.txt").Content, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenSeekableSourceOfKnownSmallLength_WhenAdded_ThenNoZip64IsReserved() {
    using var zip = SeedZip();
    var lfh = CentralDirectoryOffset(zip);

    ZipModifier.AddFile(zip, "s.txt", new MemoryStream(Compressible(5_000)));

    var header = zip.ToArray().AsSpan((int)lfh);
    Assert.That(Zip64Extra(header), Is.Null);
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(header[LfhVersionNeeded..]), Is.EqualTo(20));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(header[LfhMethod..]), Is.EqualTo((ushort)ZipCompressionMethod.Deflate));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenSourceAnnouncingFourGiBButEndingEarly_WhenAdded_ThenZip64ReservedAndOnlyRealBytesStored() {
    // A file truncated while it is being added: the announced length decides the header
    // layout up front, the bytes actually read decide the sizes.
    var real = Random(1_000, seed: 11);
    using var zip = SeedZip();
    var lfh = CentralDirectoryOffset(zip);

    ZipModifier.AddFile(zip, "short.bin", new ShrinkingStream(real, announcedLength: uint.MaxValue));

    Assert.That(Zip64Extra(zip.ToArray().AsSpan((int)lfh)), Is.Not.Null);
    Assert.That(OracleEntry(zip, "short.bin").Content, Is.EqualTo(real));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_GivenTwoAddsAfterZip64ReservedEntry_WhenReread_ThenEveryEntryIsValid() {
    using var zip = SeedZip();

    ZipModifier.AddFile(zip, "fwd.txt", new ForwardOnlyStream(Compressible(3_000)));
    ZipModifier.AddFile(zip, "next.txt", new MemoryStream(Compressible(2_000)));
    ZipModifier.RemoveFile(zip, "seed.txt");

    Assert.That(OracleEntry(zip, "fwd.txt").Content, Is.EqualTo(Compressible(3_000)));
    Assert.That(OracleEntry(zip, "next.txt").Content, Is.EqualTo(Compressible(2_000)));
  }

  // ── Argument validation ────────────────────────────────────────────

  [Test, Category("Contract")]
  public void AddFile_GivenNullArguments_ThenThrowsArgumentNull() {
    using var zip = SeedZip();
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => ZipModifier.AddFile(null!, "n", new MemoryStream()));
      Assert.Throws<ArgumentNullException>(() => ZipModifier.AddFile(zip, null!, new MemoryStream()));
      Assert.Throws<ArgumentNullException>(() => ZipModifier.AddFile(zip, "n", (Stream)null!));
      Assert.Throws<ArgumentNullException>(() => ZipModifier.AddFile(zip, "n", (byte[])null!));
    });
  }

  [Test, Category("Contract")]
  public void AddFile_GivenUnreadableSource_ThenThrowsArgumentAndLeavesArchiveUntouched() {
    using var zip = SeedZip();
    var before = zip.ToArray();
    var unreadable = new MemoryStream();
    unreadable.Dispose();

    Assert.Throws<ArgumentException>(() => ZipModifier.AddFile(zip, "n", unreadable));
    Assert.That(zip.ToArray(), Is.EqualTo(before));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  internal sealed record OracleView(byte[] Content, uint Crc32, long CompressedLength, DateTime LastWrite);

  internal static OracleView OracleEntry(Stream zip, string name) {
    zip.Position = 0;
    using var sys = new SysZipArchive(zip, SysZipArchiveMode.Read, leaveOpen: true);
    var entry = sys.GetEntry(name) ?? throw new AssertionException($"oracle does not see '{name}'");
    return new(ReadAll(entry), entry.Crc32, entry.CompressedLength, entry.LastWriteTime.DateTime);
  }

  private static ZipEntry OurEntry(Stream zip, string name) {
    zip.Position = 0;
    return new ZipReader(zip).Entries.Single(e => e.FileName == name);
  }

  private static byte[] ReadAll(System.IO.Compression.ZipArchiveEntry entry) {
    using var s = entry.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteSysEntry(SysZipArchive sys, string name, byte[] data) {
    using var s = sys.CreateEntry(name).Open();
    s.Write(data);
  }

  internal static MemoryStream SeedZip() {
    var ms = new MemoryStream();
    var w = new ZipWriter(ms, leaveOpen: true);
    w.AddEntry("seed.txt", "seed-content"u8.ToArray(), ZipCompressionMethod.Store);
    w.Finish();
    return ms;
  }

  private static long CentralDirectoryOffset(Stream zip) => ZipEndOfCentralDirectory.Read(zip).Offset;

  internal static byte[] Compressible(int length) {
    var text = "The quick brown fox jumps over the lazy dog; streaming ZIP add 0123456789.\n"u8;
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = text[i % text.Length];
    return data;
  }

  private static byte[] HalfEntropy(int length, int seed) {
    var data = Random(length, seed);
    for (var i = 0; i < data.Length; ++i)
      data[i] &= 0x0F;
    return data;
  }

  private static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static ReadOnlyMemory<byte>? Zip64Extra(ReadOnlySpan<byte> localHeader) {
    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[LfhNameLength..]);
    var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[LfhExtraLength..]);
    var extra = localHeader.Slice(30 + nameLength, extraLength).ToArray();
    for (var pos = 0; pos + 4 <= extra.Length;) {
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(pos));
      var size = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(pos + 2));
      if (tag == 0x0001)
        return extra.AsMemory(pos + 4, size);
      pos += 4 + size;
    }
    return null;
  }

  /// <summary>Readable, non-seekable source that reports how much has been consumed.</summary>
  internal sealed class ForwardOnlyStream(byte[] data) : Stream {
    private int _position;
    public long Consumed => this._position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = Math.Min(count, data.Length - this._position);
      Array.Copy(data, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Forward-only source that throws an I/O error once <c>failAfter</c> bytes were read.</summary>
  private sealed class FailingStream(byte[] data, int failAfter) : Stream {
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) {
      if (this._position >= failAfter)
        throw new IOException("simulated read failure");
      var n = Math.Min(Math.Min(count, data.Length - this._position), failAfter - this._position);
      Array.Copy(data, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Seekable source whose announced length exceeds what it actually delivers.</summary>
  private sealed class ShrinkingStream(byte[] data, long announcedLength) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => announcedLength;
    public override long Position { get => this._position; set => this._position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = (int)Math.Max(0, Math.Min(count, data.Length - this._position));
      Array.Copy(data, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
      SeekOrigin.Begin => offset, SeekOrigin.Current => this._position + offset, _ => announcedLength + offset,
    };
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Archive wrapper that records how much source was consumed at the first bulk payload write.</summary>
  private sealed class WriteObservingStream(Stream inner, Func<long> consumed) : Stream {
    public long? ConsumedAtFirstLargeWrite { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) {
      // Headers are small; the first write of at least a few KiB is compressed payload.
      if (buffer.Length >= 4096 && this.ConsumedAtFirstLargeWrite == null)
        this.ConsumedAtFirstLargeWrite = consumed();
      inner.Write(buffer);
    }
  }
}
