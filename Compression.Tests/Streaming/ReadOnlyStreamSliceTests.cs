using Compression.Registry.Streaming;

namespace Compression.Tests.Streaming;

/// <summary>
/// Spec tests for <see cref="ReadOnlyStreamSlice"/> — the seekable bounded
/// view used by positional archive descriptors (game archives, raw bounded
/// FULL.* synthetic entries). Covers the boundary classes called out in
/// the step 4B spec: seek-from-Begin/Current/End, read-past-bound,
/// Length, Position clamping.
/// </summary>
[TestFixture]
public class ReadOnlyStreamSliceTests {

  private static MemoryStream MakeStream(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; i++) data[i] = (byte)(i & 0xFF);
    return new MemoryStream(data, writable: false);
  }

  [Test, Category("Spec")]
  public void Read_NeverProducesMoreThanLength() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 200);
    var buf = new byte[1000];
    var read = slice.Read(buf, 0, buf.Length);
    Assert.That(read, Is.EqualTo(200));
    Assert.That(slice.Position, Is.EqualTo(200));
    // The bytes should be inner[100..300).
    for (var i = 0; i < 200; i++) Assert.That(buf[i], Is.EqualTo((byte)((100 + i) & 0xFF)));
  }

  [Test, Category("Spec")]
  public void ReadPastBound_Returns0AsEof() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 50);
    var buf = new byte[200];
    var first = slice.Read(buf, 0, buf.Length);
    Assert.That(first, Is.EqualTo(50));
    var second = slice.Read(buf, 0, buf.Length);
    Assert.That(second, Is.EqualTo(0), "Subsequent read past bound must be 0 (EOF)");
  }

  [Test, Category("Spec")]
  public void Length_ExposesSliceLength_NotInnerLength() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 50, length: 25);
    Assert.That(slice.Length, Is.EqualTo(25));
  }

  [Test, Category("Spec")]
  public void Seek_FromBegin_ClampsToRange() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 100);
    Assert.That(slice.Seek(50, SeekOrigin.Begin), Is.EqualTo(50));
    Assert.That(slice.Seek(-10, SeekOrigin.Begin), Is.EqualTo(0), "Negative target clamps to 0");
    Assert.That(slice.Seek(500, SeekOrigin.Begin), Is.EqualTo(100), "Beyond-length target clamps to length");
  }

  [Test, Category("Spec")]
  public void Seek_FromCurrent_ClampsToRange() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 100);
    slice.Seek(50, SeekOrigin.Begin);
    Assert.That(slice.Seek(20, SeekOrigin.Current), Is.EqualTo(70));
    Assert.That(slice.Seek(-100, SeekOrigin.Current), Is.EqualTo(0));
    Assert.That(slice.Seek(500, SeekOrigin.Current), Is.EqualTo(100));
  }

  [Test, Category("Spec")]
  public void Seek_FromEnd_ClampsToRange() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 100);
    Assert.That(slice.Seek(0, SeekOrigin.End), Is.EqualTo(100));
    Assert.That(slice.Seek(-50, SeekOrigin.End), Is.EqualTo(50));
    Assert.That(slice.Seek(50, SeekOrigin.End), Is.EqualTo(100), "Beyond-end target clamps to length");
    Assert.That(slice.Seek(-200, SeekOrigin.End), Is.EqualTo(0), "Before-origin target clamps to 0");
  }

  [Test, Category("Spec")]
  public void Position_Setter_ClampsToRange() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 100, length: 100);
    slice.Position = 50;
    Assert.That(slice.Position, Is.EqualTo(50));
    slice.Position = 500;
    Assert.That(slice.Position, Is.EqualTo(100), "Position set beyond length clamps");
  }

  [Test, Category("Spec")]
  public void Write_Throws() {
    using var inner = MakeStream(1000);
    using var slice = new ReadOnlyStreamSlice(inner, origin: 0, length: 100);
    Assert.That(() => slice.Write(new byte[10], 0, 10), Throws.InstanceOf<NotSupportedException>());
    Assert.That(() => slice.SetLength(10), Throws.InstanceOf<NotSupportedException>());
    Assert.That(slice.CanWrite, Is.False);
  }

  [Test, Category("Spec")]
  public void NonSeekableInner_Throws() {
    var nonSeek = new NonSeekableStream();
    Assert.That(() => new ReadOnlyStreamSlice(nonSeek, 0, 100), Throws.ArgumentException);
  }

  private sealed class NonSeekableStream : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
