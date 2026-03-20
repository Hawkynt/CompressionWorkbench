using Compression.Core.Streams;

namespace Compression.Tests.Streams;

/// <summary>
/// A concrete test implementation of CompressionStream that passes data through unchanged.
/// </summary>
internal sealed class PassthroughCompressionStream : CompressionStream {
  private bool _finished;

  public bool FinishCalled { get; private set; }

  public PassthroughCompressionStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
  }

  protected override int DecompressBlock(byte[] buffer, int offset, int count) =>
    InnerStream.Read(buffer, offset, count);

  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    InnerStream.Write(buffer, offset, count);
  }

  protected override void FinishCompression() {
    if (!this._finished) {
      FinishCalled = true;
      this._finished = true;
    }
  }
}

[TestFixture]
public class CompressionStreamTests {
  [Category("HappyPath")]
  [Test]
  public void DecompressMode_CanRead_NotCanWrite() {
    var inner = new MemoryStream([1, 2, 3]);
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Decompress);

    Assert.That(stream.CanRead, Is.True);
    Assert.That(stream.CanWrite, Is.False);
    Assert.That(stream.CanSeek, Is.False);
  }

  [Category("HappyPath")]
  [Test]
  public void CompressMode_CanWrite_NotCanRead() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.That(stream.CanRead, Is.False);
    Assert.That(stream.CanWrite, Is.True);
    Assert.That(stream.CanSeek, Is.False);
  }

  [Category("Exception")]
  [Test]
  public void Read_InCompressMode_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<InvalidOperationException>(() => _ = stream.Read(new byte[10], 0, 10));
  }

  [Category("Exception")]
  [Test]
  public void Write_InDecompressMode_Throws() {
    var inner = new MemoryStream([1, 2, 3]);
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Decompress);

    Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[10], 0, 10));
  }

  [Category("Exception")]
  [Test]
  public void Seek_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
  }

  [Category("Exception")]
  [Test]
  public void Length_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<NotSupportedException>(() => _ = stream.Length);
  }

  [Category("Exception")]
  [Test]
  public void Position_Get_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<NotSupportedException>(() => _ = stream.Position);
  }

  [Category("Exception")]
  [Test]
  public void Position_Set_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<NotSupportedException>(() => stream.Position = 0);
  }

  [Category("Exception")]
  [Test]
  public void SetLength_Throws() {
    var inner = new MemoryStream();
    using var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress);

    Assert.Throws<NotSupportedException>(() => stream.SetLength(10));
  }

  [Category("HappyPath")]
  [Test]
  public void LeaveOpen_False_DisposesInnerStream() {
    var inner = new MemoryStream();
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress, leaveOpen: false);
    stream.Dispose();

    Assert.Throws<ObjectDisposedException>(() => inner.WriteByte(0));
  }

  [Category("HappyPath")]
  [Test]
  public void LeaveOpen_True_DoesNotDisposeInnerStream() {
    var inner = new MemoryStream();
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress, leaveOpen: true);
    stream.Dispose();

    // Inner stream should still be usable
    Assert.DoesNotThrow(() => inner.WriteByte(0));
  }

  [Category("HappyPath")]
  [Test]
  public void FinishCompression_CalledOnDispose_InCompressMode() {
    var inner = new MemoryStream();
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress, leaveOpen: true);
    stream.Dispose();

    Assert.That(stream.FinishCalled, Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void FinishCompression_NotCalledOnDispose_InDecompressMode() {
    var inner = new MemoryStream([1, 2, 3]);
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Decompress, leaveOpen: true);
    stream.Dispose();

    Assert.That(stream.FinishCalled, Is.False);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Passthrough_RoundTrip() {
    byte[] data = [10, 20, 30, 40, 50];

    // Compress
    var compressed = new MemoryStream();
    using (var cs = new PassthroughCompressionStream(compressed, CompressionStreamMode.Compress, leaveOpen: true)) {
      cs.Write(data, 0, data.Length);
    }

    // Decompress
    compressed.Position = 0;
    using var ds = new PassthroughCompressionStream(compressed, CompressionStreamMode.Decompress);
    byte[] result = new byte[data.Length];
    int bytesRead = ds.Read(result, 0, result.Length);

    Assert.That(bytesRead, Is.EqualTo(data.Length));
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Read_AfterDispose_Throws() {
    var inner = new MemoryStream([1, 2, 3]);
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Decompress, leaveOpen: true);
    stream.Dispose();

    Assert.Throws<ObjectDisposedException>(() => _ = stream.Read(new byte[10], 0, 10));
  }

  [Category("Exception")]
  [Test]
  public void Write_AfterDispose_Throws() {
    var inner = new MemoryStream();
    var stream = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress, leaveOpen: true);
    stream.Dispose();

    Assert.Throws<ObjectDisposedException>(() => stream.Write(new byte[10], 0, 10));
  }

  [Category("HappyPath")]
  [Test]
  public void Mode_ReturnsCorrectValue() {
    var inner = new MemoryStream();
    using var compress = new PassthroughCompressionStream(inner, CompressionStreamMode.Compress, leaveOpen: true);
    Assert.That(compress.Mode, Is.EqualTo(CompressionStreamMode.Compress));

    using var decompress = new PassthroughCompressionStream(inner, CompressionStreamMode.Decompress, leaveOpen: true);
    Assert.That(decompress.Mode, Is.EqualTo(CompressionStreamMode.Decompress));
  }
}
