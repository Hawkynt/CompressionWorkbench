using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// The writer's slicing, tape marks, streaming, symlinks and inode metadata, read back by our own
/// reader. dar's verdict on the same output is in <see cref="DarExternalConformanceTests"/>.
/// </summary>
[TestFixture]
public class DarWriterFeatureTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("features");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly DateTime When = new(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);

  internal static List<DarWriter.Item> TreeItems() {
    var items = new List<DarWriter.Item> {
      new("dir", DarWriter.ItemKind.Directory, 0x1E8, When, When),
      new("emptydir", DarWriter.ItemKind.Directory, 0x1ED, When, When),
    };
    items.AddRange(DarFixtures.TreeFiles.Select(f => DarWriter.Item.FromBytes(f.Key, f.Value, 0x1A4, When, When)));
    return items;
  }

  /// <summary>Writes through the internal API into numbered slice files and returns their paths.</summary>
  private string[] WriteSlices(string baseName, IReadOnlyList<DarWriter.Item> items, DarWriter.Settings settings) {
    var basePath = Path.Combine(this._tmp, baseName);
    var opened = new List<FileStream>();
    try {
      DarWriter.Write(n => {
        var fs = File.Create($"{basePath}.{n}.dar");
        opened.Add(fs);
        return fs;
      }, items, settings);
    } finally {
      // Slice 1 belongs to the caller; the writer closed the others, disposing twice is harmless.
      foreach (var fs in opened)
        fs.Dispose();
    }
    return Enumerable.Range(1, opened.Count).Select(n => $"{basePath}.{n}.dar").ToArray();
  }

  private string ExtractFrom(string slicePath) {
    var outDir = Path.Combine(this._tmp, "out_" + Guid.NewGuid().ToString("N"));
    using var fs = File.OpenRead(slicePath);
    new DarFormatDescriptor().Extract(fs, outDir, null, null);
    return outDir;
  }

  private static void AssertTree(string outDir) {
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, path)), Is.EqualTo(content), path);
    Assert.That(Directory.Exists(Path.Combine(outDir, "emptydir")), Is.True);
  }

  private static DarArchive Open(Stream stream, out IDisposable set) {
    var s = DarSliceSet.Open(stream);
    set = s;
    return DarArchive.Read(s.Archive);
  }

  // ── Slicing ────────────────────────────────────────────────────────────────

  [TestCase(2048, 0)]
  [TestCase(2048, 3072)]
  [TestCase(1000, 5000)]
  [TestCase(5000, 1000)]
  public void GivenASliceSize_WhenWritten_ThenEverySliceButTheLastHasExactlyThatSizeAndTheSetReadsBack(int size, int first) {
    var slices = this.WriteSlices("set", TreeItems(), new('n', SliceSize: size, FirstSliceSize: first));
    Assert.That(slices, Has.Length.GreaterThan(2));
    for (var i = 0; i < slices.Length - 1; ++i)
      Assert.That(new FileInfo(slices[i]).Length, Is.EqualTo(i == 0 && first > 0 ? first : size), Path.GetFileName(slices[i]));
    Assert.That(new FileInfo(slices[^1]).Length, Is.LessThanOrEqualTo(size).And.GreaterThan(0));
    AssertTree(this.ExtractFrom(slices[^1]));
    AssertTree(this.ExtractFrom(slices[0]));
  }

  [Test]
  public void GivenASlicedSet_WhenEachHeaderIsParsed_ThenItDefersToTheTrailerAndRecordsTheSizes() {
    var slices = this.WriteSlices("hdr", TreeItems(), new('n', SliceSize: 2048, FirstSliceSize: 3072));
    for (var i = 0; i < slices.Length; ++i) {
      using var fs = File.OpenRead(slices[i]);
      var h = DarSliceSet.ReadHeader(fs);
      Assert.That(h.IsValid, Is.True, h.Problem);
      Assert.That(h.Flag, Is.EqualTo('E'));
      Assert.That(h.Trailer, Is.EqualTo(i == slices.Length - 1 ? 'T' : 'N'));
      Assert.That(h.SliceSize, Is.EqualTo(2048));
      Assert.That(h.FirstSliceSize, Is.EqualTo(3072));
      Assert.That(h.DataName, Is.EqualTo(h.InternalName));
    }
  }

  [Test]
  public void GivenSlicesOfOnePayloadByte_WhenWritten_ThenTheSetStillReadsBack() {
    var headerAndTrailer = DarArchiveSink.SliceHeaderLength(4096, 0) + 1;
    var items = new List<DarWriter.Item> { DarWriter.Item.FromBytes("a.txt", "abc"u8.ToArray(), 0x1A4, When, When) };
    var slices = this.WriteSlices("tiny", items, new('n', SliceSize: headerAndTrailer + 1));
    Assert.That(slices.Length, Is.GreaterThan(50));
    var outDir = this.ExtractFrom(slices[^1]);
    Assert.That(File.ReadAllText(Path.Combine(outDir, "a.txt")), Is.EqualTo("abc"));
  }

  [Test]
  public void GivenAnArchiveThatExactlyFillsItsSlices_WhenWritten_ThenNoEmptySliceFollows() {
    var items = new List<DarWriter.Item> { DarWriter.Item.FromBytes("a.txt", "abc"u8.ToArray(), 0x1A4, When, When) };
    var unsliced = this.WriteSlices("whole", items, new('n'));
    var payload = new FileInfo(unsliced[0]).Length - DarArchiveSink.SliceHeaderLength(0, 0) - 1;
    // Two slices whose payloads add up to the archive exactly (labels differ between runs, their
    // lengths do not).
    var overhead = DarArchiveSink.SliceHeaderLength(4096, 8192) + 1;
    var size = overhead + payload / 2;
    var first = overhead + payload - payload / 2;
    var slices = this.WriteSlices("exact", items, new('n', SliceSize: size, FirstSliceSize: first));
    Assert.That(slices, Has.Length.EqualTo(2));
    Assert.That(new FileInfo(slices[0]).Length, Is.EqualTo(first));
    Assert.That(new FileInfo(slices[1]).Length, Is.EqualTo(size));
    Assert.That(File.ReadAllText(Path.Combine(this.ExtractFrom(slices[1]), "a.txt")), Is.EqualTo("abc"));
  }

  [TestCase(-40)]
  [TestCase(0)]
  [Category("Exceptional")]
  public void GivenASliceWithNoRoomBesideItsHeaderAndTrailer_WhenWritten_ThenItIsRejected(int delta) {
    var size = DarArchiveSink.SliceHeaderLength(1000, 0) + 1 + delta;
    Assert.Throws<ArgumentOutOfRangeException>(() => this.WriteSlices("small", TreeItems(), new('n', SliceSize: size)));
  }

  [Test, Category("Exceptional")]
  public void GivenSlicingAndAStreamThatIsNotASliceFile_WhenCreated_ThenItIsRejectedBeforeWriting() {
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = { [DarFormatDescriptor.SliceSizeOption] = "4k" } };
    Assert.Throws<ArgumentException>(() => new DarFormatDescriptor().Create(ms, DarWriterTests.SampleInputs(), options));
    Assert.That(ms.Length, Is.Zero);
  }

  [Test, Category("Exceptional")]
  public void GivenAFirstSliceSizeAlone_WhenCreated_ThenItIsRejected() {
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = { [DarFormatDescriptor.FirstSliceSizeOption] = "4k" } };
    Assert.Throws<ArgumentException>(() => new DarFormatDescriptor().Create(ms, DarWriterTests.SampleInputs(), options));
  }

  [Test]
  public void GivenSliceOptions_WhenCreatedThroughTheDescriptor_ThenTheSlicesLandBesideTheFirst() {
    var first = Path.Combine(this._tmp, "desc.1.dar");
    var options = new FormatCreateOptions {
      MethodName = "stored",
      FormatSpecific = { [DarFormatDescriptor.SliceSizeOption] = "1k", [DarFormatDescriptor.FirstSliceSizeOption] = "2k" },
    };
    using (var fs = File.Create(first))
      new DarFormatDescriptor().Create(fs, DarWriterTests.SampleInputs(), options);
    Assert.That(new FileInfo(first).Length, Is.EqualTo(2048));
    Assert.That(File.Exists(Path.Combine(this._tmp, "desc.2.dar")), Is.True);
    AssertTree(this.ExtractFrom(first));
  }

  [TestCase("", 0L)]
  [TestCase("0", 0L)]
  [TestCase("1500", 1500L)]
  [TestCase("16k", 16384L)]
  [TestCase("2M", 2097152L)]
  [TestCase("3G", 3221225472L)]
  [TestCase("1T", 1099511627776L)]
  public void GivenADarSize_WhenParsed_ThenSuffixesCountIn1024s(string text, long expected)
    => Assert.That(DarFormatDescriptor.ParseSize(text, "SliceSize"), Is.EqualTo(expected));

  [TestCase("x")]
  [TestCase("-1")]
  [TestCase("1.5k")]
  [TestCase("99999999999T")]
  [Category("Exceptional")]
  public void GivenSomethingThatIsNotASize_WhenParsed_ThenItIsRejected(string text)
    => Assert.Throws<ArgumentException>(() => DarFormatDescriptor.ParseSize(text, "SliceSize"));

  // ── Tape marks ─────────────────────────────────────────────────────────────

  [TestCaseSource(typeof(DarWriterTests), nameof(DarWriterTests.Methods))]
  public void GivenTapeMarks_WhenWrittenAndReadBack_ThenEveryFileIsByteIdentical(string method) {
    var slices = this.WriteSlices("tape", TreeItems(), new(DarWriter.AlgorithmFor(method), TapeMarks: true));
    AssertTree(this.ExtractFrom(slices[0]));
  }

  [Test]
  public void GivenTapeMarks_WhenWritten_ThenTheMarksComeInDarsOrder() {
    var items = new List<DarWriter.Item> {
      new("d", DarWriter.ItemKind.Directory, 0x1ED, When, When),
      DarWriter.Item.FromBytes("d/f.txt", "data"u8.ToArray(), 0x1A4, When, When),
      new("l", DarWriter.ItemKind.Symlink, 0x1FF, When, When) { LinkTarget = "d/f.txt" },
    };
    var image = File.ReadAllBytes(this.WriteSlices("order", items, new('n', TapeMarks: true))[0]);
    var marks = new List<char>();
    var prefix = new byte[] { 0xAD, 0xFD, 0xEA, 0x77, 0x21 };
    for (var at = image.AsSpan().IndexOf(prefix); at >= 0;) {
      marks.Add((char)image[at + 5]);
      var next = image.AsSpan(at + 6).IndexOf(prefix);
      at = next < 0 ? -1 : at + 6 + next;
    }
    // data name, in-place path, d, f.txt + its data checksum, end of d, l, catalogue
    Assert.That(new string([.. marks]), Is.EqualTo("DPFFRFFC"));
  }

  [Test]
  public void GivenDataHoldingTheEscapePrefix_WhenWrittenWithTapeMarks_ThenItIsEscapedAndComesBack() {
    var data = DarFixtures.TreeFiles["escape.bin"];
    var items = new List<DarWriter.Item> { DarWriter.Item.FromBytes("e.bin", data, 0x1A4, When, When) };
    var path = this.WriteSlices("esc", items, new('n', TapeMarks: true))[0];
    var image = File.ReadAllBytes(path);
    Assert.That(image.AsSpan().IndexOf(new byte[] { 0xAD, 0xFD, 0xEA, 0x77, 0x21, (byte)'X', 0x46 }), Is.GreaterThan(0));
    Assert.That(File.ReadAllBytes(Path.Combine(this.ExtractFrom(path), "e.bin")), Is.EqualTo(data));
  }

  [TestCase(1)]
  [TestCase(3)]
  [TestCase(5)]
  public void GivenTheEscapePrefixSplitAcrossWrites_WhenEscaped_ThenTheXStillFollowsIt(int split) {
    using var ms = new MemoryStream();
    using (var sink = new DarArchiveSink(_ => ms, new byte[10], 0, 0, escape: true)) {
      byte[] data = [1, 0xAD, 0xFD, 0xEA, 0x77, 0x21, 2];
      sink.Data(data.AsSpan(0, split));
      sink.Data(data.AsSpan(split));
      sink.Finish();
    }
    var header = DarArchiveSink.SliceHeaderLength(0, 0);
    Assert.That(ms.ToArray()[header..^1], Is.EqualTo(new byte[] { 1, 0xAD, 0xFD, 0xEA, 0x77, 0x21, (byte)'X', 2 }));
  }

  [Test]
  public void GivenTapeMarksAndSlices_WhenWritten_ThenTheSetReadsBack() {
    var slices = this.WriteSlices("tapeslices", TreeItems(), new('z', TapeMarks: true, SliceSize: 400, FirstSliceSize: 700));
    Assert.That(slices, Has.Length.GreaterThan(2));
    AssertTree(this.ExtractFrom(slices[^1]));
  }

  // ── Streaming ──────────────────────────────────────────────────────────────

  private static byte[] Pattern(int size) {
    var data = new byte[size];
    var x = 12345u;
    for (var i = 0; i < size; ++i) {
      x = x * 1103515245 + 12345;
      data[i] = (i % 300) < 200 ? (byte)(i % 251) : (byte)(x >> 24); // compressible runs and noise
    }
    return data;
  }

  [TestCaseSource(typeof(DarWriterTests), nameof(DarWriterTests.Methods))]
  public void GivenAFileAboveTheInMemoryLimit_WhenWritten_ThenItIsCompressedOnTheFlyAndReadsBack(string method) {
    var data = Pattern(700_000);
    var items = new List<DarWriter.Item> { DarWriter.Item.FromBytes("big.bin", data, 0x1A4, When, When) };
    var settings = new DarWriter.Settings(DarWriter.AlgorithmFor(method), TapeMarks: true) { InMemoryLimit = 1000 };
    var path = this.WriteSlices("stream", items, settings)[0];
    using (var fs = File.OpenRead(path)) {
      var archive = Open(fs, out var set);
      using (set)
        Assert.That(archive.Entries.Single().Algorithm, Is.EqualTo(DarWriter.AlgorithmFor(method)));
    }
    Assert.That(File.ReadAllBytes(Path.Combine(this.ExtractFrom(path), "big.bin")), Is.EqualTo(data));
  }

  [TestCaseSource(typeof(DarWriterTests), nameof(DarWriterTests.Methods))]
  public void GivenABlockSize_WhenWritten_ThenTheHeaderRecordsBlockModeAndEveryFileReadsBack(string method) {
    var algorithm = DarWriter.AlgorithmFor(method);
    var items = TreeItems();
    var big = Pattern(100_000);
    items.Add(DarWriter.Item.FromBytes("dir/big.bin", big, 0x1A4, When, When));
    var path = this.WriteSlices("blocks", items, new(algorithm, TapeMarks: true, BlockSize: 4096) { InMemoryLimit = 50_000 })[0];
    using (var fs = File.OpenRead(path)) {
      var archive = Open(fs, out var set);
      using (set) {
        Assert.That(archive.Trailer.BlockSize, Is.EqualTo(algorithm == 'n' ? 0 : 4096));
        Assert.That((archive.Trailer.Flags & 0x0800) != 0, Is.EqualTo(algorithm != 'n'));
      }
    }
    var outDir = this.ExtractFrom(path);
    AssertTree(outDir);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "dir", "big.bin")), Is.EqualTo(big));
  }

  [TestCase(-1)]
  [TestCase((64 << 20) + 1)]
  [Category("Exceptional")]
  public void GivenABlockSizeOutOfRange_WhenWritten_ThenItIsRejected(int blockSize)
    => Assert.Throws<ArgumentOutOfRangeException>(() => DarWriter.Write(new MemoryStream(), TreeItems(), new('z', BlockSize: blockSize)));

  [Test]
  public void GivenAZstdFileBeyondOneFrame_WhenWritten_ThenBlockModeCarriesItAndItReadsBack() {
    const long size = (64L << 20) + 12345;
    var item = new DarWriter.Item("huge.bin", DarWriter.ItemKind.File, 0x1A4, When, When) {
      Open = () => new PatternStream(size), Length = size,
    };
    var path = this.WriteSlices("huge", [item], new('d'))[0];
    using var fs = File.OpenRead(path);
    var archive = Open(fs, out var set);
    using (set) {
      Assert.That(archive.Trailer.BlockSize, Is.EqualTo(8 << 20));
      using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      using (var src = new PatternStream(size)) {
        var buffer = new byte[1 << 20];
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
          expected.AppendData(buffer, 0, n);
      }
      var sink = new HashingSink();
      archive.CopyData(archive.Entries.Single(), sink);
      Assert.That(sink.Length, Is.EqualTo(size));
      Assert.That(sink.Hash.GetHashAndReset(), Is.EqualTo(expected.GetHashAndReset()));
    }
  }

  /// <summary>Repeating, compressible bytes of a given length, generated on the fly.</summary>
  private sealed class PatternStream(long length) : Stream {
    private long _at;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => this._at; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) {
      var n = (int)Math.Min(count, length - this._at);
      for (var i = 0; i < n; ++i) {
        var p = this._at + i;
        buffer[offset + i] = (byte)((p >> 12) * 31 + (p & 0xFF));
      }
      this._at += n;
      return n;
    }
  }

  private sealed class HashingSink : Stream {
    public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this._length;
    public override long Position { get => this._length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) {
      this.Hash.AppendData(buffer, offset, count);
      this._length += count;
    }
  }

  [Test]
  public void GivenIncompressibleDataAboveTheInMemoryLimit_WhenWritten_ThenTheCompressedFormIsKeptAndStillReadsBack() {
    var data = new byte[50_000];
    new Random(5).NextBytes(data);
    var items = new List<DarWriter.Item> { DarWriter.Item.FromBytes("noise.bin", data, 0x1A4, When, When) };
    var path = this.WriteSlices("noise", items, new('z') { InMemoryLimit = 1000 })[0];
    Assert.That(File.ReadAllBytes(Path.Combine(this.ExtractFrom(path), "noise.bin")), Is.EqualTo(data));
  }

  [TestCase(+1)]
  [TestCase(-1)]
  [Category("Exceptional")]
  public void GivenASourceWhoseSizeChanges_WhenWritten_ThenTheWriteFails(int delta) {
    var data = Pattern(5000);
    foreach (var limit in new[] { 1000, 1 << 20 }) {
      var item = new DarWriter.Item("moving.bin", DarWriter.ItemKind.File, 0x1A4, When, When) {
        Open = () => new MemoryStream(data), Length = data.Length - delta,
      };
      Assert.Throws<IOException>(() => DarWriter.Write(new MemoryStream(), [item], new('z') { InMemoryLimit = limit }), $"limit {limit}");
      Assert.Throws<IOException>(() => DarWriter.Write(new MemoryStream(), [item], new('n') { InMemoryLimit = limit }), $"stored, limit {limit}");
    }
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  [TestCase(8)]
  [TestCase(12)]
  [TestCase(20)]
  public void GivenDataFedInPieces_WhenChecksummed_ThenItEqualsTheOneShotRing(int width) {
    var data = Pattern(10_007);
    var expected = DarPrimitives.Checksum(data, width);
    foreach (var cut in new[] { 0, 1, 3, 7, 8, 9, 63, 1000, 10_006, 10_007 }) {
      var crc = new DarChecksum(width);
      crc.Update(data.AsSpan(0, cut));
      crc.Update(data.AsSpan(cut, Math.Min(5, data.Length - cut)));
      crc.Update(data.AsSpan(Math.Min(cut + 5, data.Length)));
      Assert.That(crc.Value, Is.EqualTo(expected), $"cut {cut}");
      Assert.That(crc.Length, Is.EqualTo(data.Length));
    }
  }

  [Test]
  public void GivenAnOnDiskInput_WhenCreated_ThenItIsReadFromTheFileNotLoadedUpFront() {
    var src = Path.Combine(this._tmp, "src.bin");
    var data = Pattern(300_000);
    File.WriteAllBytes(src, data);
    var archive = Path.Combine(this._tmp, "disk.1.dar");
    using (var fs = File.Create(archive))
      new DarFormatDescriptor().Create(fs, [new ArchiveInputInfo(src, "src.bin", false)], new FormatCreateOptions { MethodName = "zstd" });
    Assert.That(File.ReadAllBytes(Path.Combine(this.ExtractFrom(archive), "src.bin")), Is.EqualTo(data));
  }

  // ── Symlinks and inode metadata ────────────────────────────────────────────

  [Test]
  public void GivenASymlink_WhenWritten_ThenItIsListedWithItsTarget() {
    var items = new List<DarWriter.Item> {
      DarWriter.Item.FromBytes("target.txt", "t"u8.ToArray(), 0x1A4, When, When),
      new("link", DarWriter.ItemKind.Symlink, 0x1FF, When, When) { LinkTarget = "target.txt" },
      new("dangling", DarWriter.ItemKind.Symlink, 0x1FF, When, When) { LinkTarget = "../nowhere/at all" },
    };
    foreach (var tape in new[] { false, true }) {
      using var ms = new MemoryStream();
      DarWriter.Write(ms, items, new('n', TapeMarks: tape));
      ms.Position = 0;
      var entries = new DarFormatDescriptor().List(ms, null).ToDictionary(e => e.Name, StringComparer.Ordinal);
      Assert.That(entries["link"].IsSymlink, Is.True);
      Assert.That(entries["link"].LinkTarget, Is.EqualTo("target.txt"));
      Assert.That(entries["dangling"].LinkTarget, Is.EqualTo("../nowhere/at all"));
    }
  }

  [Test, Category("Exceptional")]
  public void GivenASymlinkWithoutATarget_WhenWritten_ThenItIsRejected()
    => Assert.Throws<ArgumentException>(() => DarWriter.Write(new MemoryStream(),
      [new("link", DarWriter.ItemKind.Symlink, 0x1FF, When, When)], new('n')));

  [Test]
  public void GivenEntriesBelowASymlinkedDirectory_WhenWritten_ThenOnlyTheLinkIsStored() {
    var items = new List<DarWriter.Item> {
      new("linkdir", DarWriter.ItemKind.Symlink, 0x1FF, When, When) { LinkTarget = "elsewhere" },
      DarWriter.Item.FromBytes("linkdir/inside.txt", "x"u8.ToArray(), 0x1A4, When, When),
    };
    using var ms = new MemoryStream();
    DarWriter.Write(ms, items, new('n'));
    ms.Position = 0;
    Assert.That(new DarFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "linkdir" }));
  }

  [Test]
  public void GivenOwnershipModeAndTimes_WhenCreatedFromMetadata_ThenTheCatalogueRecordsThem() {
    var modified = new DateTimeOffset(2011, 12, 13, 14, 15, 16, TimeSpan.Zero).AddTicks(1234567);
    var accessed = modified.AddDays(1);
    var changed = modified.AddDays(2);
    var meta = new ArchiveEntryMetadata(LastAccessTimeUtc: accessed, LastWriteTimeUtc: modified, UnixUserId: 1234, UnixGroupId: 5678,
      UnixMode: 0x8000 | 0x1ED | 0x800, StatusChangeTimeUtc: changed); // S_IFREG | 0755 | setuid
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("owned.sh", "#!/bin/sh\n"u8.ToArray(), meta)], new FormatCreateOptions());
    ms.Position = 0;
    var archive = Open(ms, out var set);
    using (set) {
      var e = archive.Entries.Single();
      Assert.That(e.Uid, Is.EqualTo(1234));
      Assert.That(e.Gid, Is.EqualTo(5678));
      Assert.That(e.Permissions, Is.EqualTo(0x9ED));
      Assert.That(e.LastModified, Is.EqualTo(modified.UtcDateTime));
      Assert.That(e.LastAccess, Is.EqualTo(accessed.UtcDateTime));
      Assert.That(e.LastChange, Is.EqualTo(changed.UtcDateTime));
    }
  }

  [Test]
  public void GivenNoMetadata_WhenCreatedInMemory_ThenOwnershipIsRootAndTheModeIsTheDefault() {
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("plain.txt", "p"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    var archive = Open(ms, out var set);
    using (set) {
      var e = archive.Entries.Single();
      Assert.That((e.Uid, e.Gid), Is.EqualTo((0UL, 0UL)));
      Assert.That(e.Permissions, Is.EqualTo(0x1A4));
    }
  }
}
