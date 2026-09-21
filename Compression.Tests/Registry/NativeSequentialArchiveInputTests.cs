using Compression.Registry;
using FileFormat.Cpio;
using FileFormat.Tar;

namespace Compression.Tests.Registry;

/// <summary>
/// Proves formats whose on-disk layout is genuinely sequential bypass the
/// universal seek/spool bridge rather than merely accepting a non-seekable
/// source after it has already been copied in full.
/// </summary>
[TestFixture]
public sealed class NativeSequentialArchiveInputTests {
  private const int TrailingJunkSize = 64 * 1024;

  [Test]
  [Category("Spec")]
  public void Tar_ListStreaming_StopsBeforeTrailingJunk() {
    var image = BuildTar();
    using var source = new CountingForwardOnlyStream(AppendJunk(image));
    IArchiveFormatOperations operations = new TarFormatDescriptor();

    var entries = operations.ListStreaming(source, null);

    Assert.That(entries.Select(x => x.Name), Is.EqualTo(new[] { "first.txt", "second.txt" }));
    Assert.That(source.BytesRead, Is.LessThan(source.TotalLength),
      "A native sequential TAR reader must not pre-spool the complete source.");
  }

  [Test]
  [Category("Spec")]
  public void Tar_OpenEntryStreaming_DoesNotReadPastRequestedEntry() {
    var image = BuildTar();
    using var source = new CountingForwardOnlyStream(AppendJunk(image));
    IArchiveFormatOperations operations = new TarFormatDescriptor();

    using var entry = operations.OpenEntryStreaming(source, "first.txt", null);
    using var result = new MemoryStream();
    entry.CopyTo(result);

    Assert.That(result.ToArray(), Is.EqualTo("first"u8.ToArray()));
    Assert.That(source.BytesRead, Is.LessThan(image.Length),
      "Opening the first TAR entry should not require consuming the rest of the archive.");
  }

  [Test]
  [Category("Spec")]
  public void Cpio_ListStreaming_StopsAtTrailerBeforeTrailingJunk() {
    var image = BuildCpio();
    using var source = new CountingForwardOnlyStream(AppendJunk(image));
    IArchiveFormatOperations operations = new CpioFormatDescriptor();

    var entries = operations.ListStreaming(source, null);

    Assert.That(entries.Select(x => x.Name), Is.EqualTo(new[] { "first.txt", "second.txt" }));
    Assert.That(source.BytesRead, Is.LessThan(source.TotalLength),
      "A native sequential CPIO reader must not pre-spool the complete source.");
  }

  [Test]
  [Category("Spec")]
  public void Cpio_ExtractStreaming_StopsAtTrailerBeforeTrailingJunk() {
    var image = BuildCpio();
    using var source = new CountingForwardOnlyStream(AppendJunk(image));
    IArchiveFormatOperations operations = new CpioFormatDescriptor();
    var directory = Path.Combine(Path.GetTempPath(), $"cwb-cpio-stream-{Guid.NewGuid():N}");

    try {
      operations.ExtractStreaming(source, directory, null, ["second.txt"]);

      Assert.That(File.ReadAllBytes(Path.Combine(directory, "second.txt")), Is.EqualTo("second"u8.ToArray()));
      Assert.That(source.BytesRead, Is.LessThan(source.TotalLength),
        "Native CPIO extraction should stop at TRAILER!!! instead of reading trailing transport bytes.");
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  private static byte[] BuildTar() {
    using var output = new MemoryStream();
    using (var writer = new TarWriter(output, leaveOpen: true)) {
      writer.AddEntry(new TarEntry { Name = "first.txt" }, "first"u8);
      writer.AddEntry(new TarEntry { Name = "second.txt" }, "second"u8);
    }

    return output.ToArray();
  }

  private static byte[] BuildCpio() {
    using var output = new MemoryStream();
    using (var writer = new CpioWriter(output, leaveOpen: true)) {
      writer.AddFile("first.txt", "first"u8);
      writer.AddFile("second.txt", "second"u8);
    }

    return output.ToArray();
  }

  private static byte[] AppendJunk(byte[] archive) {
    var result = new byte[archive.Length + TrailingJunkSize];
    archive.CopyTo(result, 0);
    result.AsSpan(archive.Length).Fill(0xA5);
    return result;
  }

  private sealed class CountingForwardOnlyStream(byte[] data) : Stream {
    private int _position;

    public long BytesRead { get; private set; }
    public long TotalLength => data.LongLength;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) {
      var read = this.Read(buffer.AsSpan(offset, count));
      return read;
    }

    public override int Read(Span<byte> buffer) {
      var available = data.Length - this._position;
      if (available <= 0)
        return 0;

      var count = Math.Min(buffer.Length, available);
      data.AsSpan(this._position, count).CopyTo(buffer);
      this._position += count;
      this.BytesRead += count;
      return count;
    }

    public override int ReadByte() {
      if (this._position >= data.Length)
        return -1;
      ++this.BytesRead;
      return data[this._position++];
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
