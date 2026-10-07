using Compression.Core.Streams;
using FileFormat.Gzip;
using Bcl = System.IO.Compression;

namespace Compression.Tests.Gzip;

/// <summary>
/// <see cref="GzipStream"/> on inputs the platform's stream also handles: forward-only inner
/// streams, several members, and bytes after the last member.
/// </summary>
[TestFixture]
public class GzipStreamingTests {

  private static byte[] BclGzip(byte[] data) {
    using var output = new MemoryStream();
    using (var gzip = new Bcl.GZipStream(output, Bcl.CompressionLevel.Optimal, leaveOpen: true))
      gzip.Write(data);
    return output.ToArray();
  }

  private static byte[] ReadAll(Stream compressed) {
    using var gzip = new GzipStream(compressed, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    gzip.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] Text(string seed, int repeat) => System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(seed, repeat)));

  [Test, Category("ThemVsUs")]
  public void ForwardOnlyInput_MultiMember_ReadsEveryMember() {
    var a = Text("first member ", 5000);
    var b = Text("second member ", 7000);
    byte[] file = [.. BclGzip(a), .. BclGzip(b)];

    Assert.That(ReadAll(new ForwardOnlyStream(file)), Is.EqualTo((byte[])[.. a, .. b]));
  }

  [Test, Category("EdgeCase")]
  public void TrailingBytesAfterTheLastMember_AreLeftAlone([Values] bool seekable) {
    var a = Text("payload ", 4000);
    byte[] file = [.. BclGzip(a), 0x00, 0x00, 0x13, 0x37];
    Stream input = seekable ? new MemoryStream(file) : new ForwardOnlyStream(file);
    Assert.That(ReadAll(input), Is.EqualTo(a));
  }

  [Test, Category("EdgeCase")]
  public void EmptyInput_ReadsNothing()
    => Assert.That(ReadAll(new ForwardOnlyStream([])), Is.Empty);

  [Test, Category("ErrorHandling")]
  public void CorruptCrc_IsInvalidData() {
    var file = BclGzip(Text("crc ", 100));
    file[^8] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => ReadAll(new MemoryStream(file)));
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void OurOutput_StreamsIntoTheInnerStream_AndPlatformReadsIt() {
    var data = new byte[300_000];
    new Random(5).NextBytes(data);
    using var output = new MemoryStream();
    using (var gzip = new GzipStream(output, CompressionStreamMode.Compress, leaveOpen: true)) {
      gzip.Write(data.AsSpan(0, 200_000));
      // The compressor writes as it goes rather than holding the deflate data back.
      Assert.That(output.Length, Is.GreaterThan(100_000));
      gzip.Write(data.AsSpan(200_000));
    }

    output.Position = 0;
    using var bcl = new Bcl.GZipStream(output, Bcl.CompressionMode.Decompress);
    using var back = new MemoryStream();
    bcl.CopyTo(back);
    Assert.That(back.ToArray(), Is.EqualTo(data));
  }

  private sealed class ForwardOnlyStream(byte[] data) : Stream {
    private int _position;
    public override int Read(byte[] buffer, int offset, int count) {
      var n = Math.Min(Math.Min(count, 4093), data.Length - this._position);
      Array.Copy(data, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
