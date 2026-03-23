using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipReaderTests {
  [Category("Exception")]
  [Test]
  public void Read_InvalidSignature_Throws() {
    var ms = new MemoryStream(new byte[32]);
    Assert.Throws<InvalidDataException>(() => _ = new SevenZipReader(ms));
  }

  [Category("Exception")]
  [Test]
  public void Read_NonSeekableStream_Throws() {
    using var ms = new MemoryStream();
    using var nonSeekable = new NonSeekableStream(ms);
    Assert.Throws<ArgumentException>(() => _ = new SevenZipReader(nonSeekable));
  }

  [Category("Exception")]
  [Test]
  public void Extract_InvalidIndex_Throws() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "test.bin", Size = data.Length }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.Extract(-1));
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.Extract(1));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_DirectoryEntry_ReturnsEmpty() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddDirectory("testdir");
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    var result = reader.Extract(0);
    Assert.That(result, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_ToStream() {
    var data = System.Text.Encoding.UTF8.GetBytes("stream output");
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "out.txt", Size = data.Length }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    using var output = new MemoryStream();
    reader.Extract(0, output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  /// <summary>
  /// A non-seekable wrapper around a stream for testing.
  /// </summary>
  private sealed class NonSeekableStream : Stream {
    private readonly Stream _inner;

    public NonSeekableStream(Stream inner) => this._inner = inner;
    public override bool CanRead => this._inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => this._inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
