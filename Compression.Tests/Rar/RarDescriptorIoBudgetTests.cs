using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public sealed class RarDescriptorIoBudgetTests {
  [Test]
  public void PureAdd_DoesNotSnapshotUntouchedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x524152).NextBytes(large);
    var descriptor = new RarFormatDescriptor();
    using var archive = Expandable(Build([
      ("large.bin", large),
      ("small.txt", "small"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.txt", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "RAR5 pure add should inspect headers, not copy the 4 MiB packed payload.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "RAR5 pure add should write only the new FILE block and ENDARC.");

    archive.Position = 0;
    using var reader = new RarReader(archive, leaveOpen: true);
    Assert.That(reader.Entries.Select(entry => entry.Name),
      Is.EqualTo(new[] { "large.bin", "small.txt", "new.txt" }));
    Assert.That(reader.Extract(reader.Entries.ToList().FindIndex(entry => entry.Name == "large.bin")), Is.EqualTo(large));
  }

  [Test]
  public void RemoveLastSmallFile_DoesNotSnapshotUntouchedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x524D5652).NextBytes(large);
    var descriptor = new RarFormatDescriptor();
    using var archive = Expandable(Build([
      ("large.bin", large),
      ("remove.txt", "delete me"u8.ToArray()),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "Removing the final small FILE block should read headers plus the tiny shifted ENDARC, not the large payload.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "Removing the final small FILE block should shift only ENDARC and truncate.");

    archive.Position = 0;
    using var reader = new RarReader(archive, leaveOpen: true);
    Assert.That(reader.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "large.bin" }));
    Assert.That(reader.Extract(0), Is.EqualTo(large));
  }

  private static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> entries) {
    using var output = new MemoryStream();
    using (var writer = new RarWriter(output, leaveOpen: true, method: RarConstants.MethodStore, solid: false)) {
      foreach (var (name, data) in entries)
        writer.AddFile(name, data);
      writer.Finish();
    }
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 128 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
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
