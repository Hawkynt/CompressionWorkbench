using Compression.Registry;
using FileFormat.Mpq;

namespace Compression.Tests.Mpq;

[TestFixture]
public sealed class MpqInPlaceEditTests {
  [Test]
  public void Add_RewritesTablesWithoutReadingUntouchedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x4D505141).NextBytes(large);
    var descriptor = new MpqFormatDescriptor();
    using var archive = Expandable(Build([
      ("large.bin", large),
      ("small.txt", "small"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new MpqReader(archive);
    var largeOffset = before.Entries.Single(entry => entry.FileName == "large.bin").FileOffset;
    var oldHashOffset = ReadHashTableOffset(archive);

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.txt", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "MPQ add should read the listfile and encrypted tables, not the 4 MiB untouched file block.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "MPQ add should write only changed stored payloads, listfile, tables, and header fields.");

    archive.Position = 0;
    var after = new MpqReader(archive);
    Assert.That(after.Entries.Single(entry => entry.FileName == "large.bin").FileOffset, Is.EqualTo(largeOffset));
    Assert.That(after.Entries.Single(entry => entry.FileName == "new.txt").FileOffset, Is.EqualTo(oldHashOffset));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.FileName == "large.bin")), Is.EqualTo(large));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.FileName == "new.txt")), Is.EqualTo("new payload"u8.ToArray()));
  }

  [Test]
  public void Replace_AppendsReplacementAndWipesOnlySupersededBlock() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x4D505152).NextBytes(large);
    var descriptor = new MpqFormatDescriptor();
    using var archive = Expandable(Build([
      ("large.bin", large),
      ("replace.txt", "old value"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new MpqReader(archive);
    var largeOffset = before.Entries.Single(entry => entry.FileName == "large.bin").FileOffset;
    var replaced = before.Entries.Single(entry => entry.FileName == "replace.txt");
    var oldHashOffset = ReadHashTableOffset(archive);

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("replace.txt", "replacement"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024));
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));
    Assert.That(archive.ToArray().AsSpan(checked((int)replaced.FileOffset), checked((int)replaced.CompressedSize)).ToArray(),
      Is.All.EqualTo((byte)0), "The superseded MPQ payload should be wiped in place.");

    archive.Position = 0;
    var after = new MpqReader(archive);
    Assert.That(after.Entries.Single(entry => entry.FileName == "large.bin").FileOffset, Is.EqualTo(largeOffset));
    var replacement = after.Entries.Single(entry => entry.FileName == "replace.txt");
    Assert.That(replacement.FileOffset, Is.EqualTo(oldHashOffset));
    Assert.That(after.Extract(replacement), Is.EqualTo("replacement"u8.ToArray()));
  }

  [Test]
  public void Remove_RewritesListfileAndTables_WithoutMovingSurvivors() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x4D505144).NextBytes(large);
    var descriptor = new MpqFormatDescriptor();
    using var archive = Expandable(Build([
      ("large.bin", large),
      ("remove.txt", "delete me"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new MpqReader(archive);
    var largeOffset = before.Entries.Single(entry => entry.FileName == "large.bin").FileOffset;
    var removed = before.Entries.Single(entry => entry.FileName == "remove.txt");

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "MPQ remove should inspect metadata/listfile without reading surviving payloads.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "MPQ remove should rewrite the listfile/tables and wipe only the removed payload.");
    Assert.That(archive.ToArray().AsSpan(checked((int)removed.FileOffset), checked((int)removed.CompressedSize)).ToArray(),
      Is.All.EqualTo((byte)0));

    archive.Position = 0;
    var after = new MpqReader(archive);
    Assert.That(after.Entries.Select(entry => entry.FileName), Does.Not.Contain("remove.txt"));
    Assert.That(after.Entries.Single(entry => entry.FileName == "large.bin").FileOffset, Is.EqualTo(largeOffset));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.FileName == "large.bin")), Is.EqualTo(large));
  }

  [Test]
  public void RemoveUnknownName_IsAReadOnlyNoOp() {
    var descriptor = new MpqFormatDescriptor();
    using var archive = Expandable(Build([("keep.txt", "keep"u8.ToArray())]));
    var before = archive.ToArray();

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["missing.txt"]);

    Assert.That(counted.BytesWritten, Is.Zero);
    Assert.That(archive.ToArray(), Is.EqualTo(before));
  }

  private static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files) {
    var writer = new MpqWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    using var output = new MemoryStream();
    writer.WriteTo(output);
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 128 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
  }

  private static uint ReadHashTableOffset(Stream archive) {
    var oldPosition = archive.Position;
    Span<byte> header = stackalloc byte[32];
    archive.Position = 0;
    archive.ReadExactly(header);
    archive.Position = oldPosition;
    return BitConverter.ToUInt32(header[16..20]);
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
