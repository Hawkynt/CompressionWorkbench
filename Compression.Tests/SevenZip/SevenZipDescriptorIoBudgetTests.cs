using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public sealed class SevenZipDescriptorIoBudgetTests {
  [Test]
  public void SevenZipPureAdd_DoesNotSnapshotUntouchedPackedStream() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x375A4950).NextBytes(large);
    var descriptor = new SevenZipFormatDescriptor();
    using var archive = Expandable(BuildIndependentBlocks([
      ("large.bin", large),
      ("small.txt", "small"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.txt", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "A pure 7z append should read the next-header metadata, not copy the 4 MiB untouched packed stream.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "A pure 7z append should write only the new packed block, replacement next-header, and 32-byte signature header.");

    AssertArchive(archive, new[] { "large.bin", "small.txt", "new.txt" }, "large.bin", large);
  }

  [Test]
  public void SevenZipRemoveLastFolder_DoesNotSnapshotUntouchedPackedStream() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x375A524D).NextBytes(large);
    var descriptor = new SevenZipFormatDescriptor();
    using var archive = Expandable(BuildIndependentBlocks([
      ("large.bin", large),
      ("remove.txt", "delete me"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "Removing the final independent 7z folder should inspect metadata without reading the preceding 4 MiB packed stream.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "Removing the final folder should rewrite only the next-header/signature and truncate; no surviving packed bytes need to move.");

    AssertArchive(archive, new[] { "large.bin" }, "large.bin", large);
  }

  [Test]
  public void Cb7PureAdd_DoesNotReintroduceWholeArchiveStaging() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x43423741).NextBytes(large);
    var descriptor = new FileFormat.Cb7.Cb7FormatDescriptor();
    using var archive = Expandable(BuildIndependentBlocks([
      ("page01.png", large),
      ("page02.png", "page two"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("page03.png", "page three"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "CB7 must pass the native 7z append through instead of wrapping it in an O(total bytes) transaction copy.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));

    AssertArchive(archive, new[] { "page01.png", "page02.png", "page03.png" }, "page01.png", large);
  }

  [Test]
  public void Cb7RemoveLastPageFolder_DoesNotReintroduceWholeArchiveStaging() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x43423752).NextBytes(large);
    var descriptor = new FileFormat.Cb7.Cb7FormatDescriptor();
    using var archive = Expandable(BuildIndependentBlocks([
      ("page01.png", large),
      ("page02.png", "remove page"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["page02.png"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "CB7 remove must preserve the underlying 7z metadata-only final-folder removal path.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));

    AssertArchive(archive, new[] { "page01.png" }, "page01.png", large);
  }

  private static byte[] BuildIndependentBlocks(IReadOnlyList<(string Name, byte[] Data)> entries) {
    using var output = new MemoryStream();
    using (var writer = new SevenZipWriter(output, SevenZipCodec.Copy, leaveOpen: true)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(new SevenZipEntry { Name = name, Size = data.Length }, data);

      writer.FinishWithBlocks([
        .. Enumerable.Range(0, entries.Count).Select(index => new SevenZipWriter.BlockDescriptor {
          EntryIndices = [index],
          Codec = SevenZipCodec.Copy,
        }),
      ]);
    }
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 128 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
  }

  private static void AssertArchive(
      MemoryStream archive,
      IReadOnlyList<string> expectedNames,
      string contentName,
      byte[] expectedContent) {
    archive.Position = 0;
    using var reader = new SevenZipReader(archive, leaveOpen: true);
    Assert.That(reader.Entries.Select(entry => entry.Name), Is.EqualTo(expectedNames));
    var index = reader.Entries.ToList().FindIndex(entry => entry.Name == contentName);
    Assert.That(index, Is.GreaterThanOrEqualTo(0));
    Assert.That(reader.Extract(index), Is.EqualTo(expectedContent));
  }

  private sealed class CountingStream(Stream inner) : Stream {
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) {
      var read = inner.Read(buffer, offset, count);
      this.BytesRead += read;
      return read;
    }

    public override int Read(Span<byte> buffer) {
      var read = inner.Read(buffer);
      this.BytesRead += read;
      return read;
    }

    public override void Write(byte[] buffer, int offset, int count) {
      inner.Write(buffer, offset, count);
      this.BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      inner.Write(buffer);
      this.BytesWritten += buffer.Length;
    }
  }
}
