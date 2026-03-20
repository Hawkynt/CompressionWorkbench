using Compression.Core.Streams;

namespace Compression.Tests.Streams;

[TestFixture]
public class ConcatenatedStreamTests {
  [Category("HappyPath")]
  [Test]
  public void Read_SingleSegment() {
    byte[] data = [1, 2, 3, 4, 5];
    using var cs = new ConcatenatedStream([new MemoryStream(data)]);

    Assert.That(cs.Length, Is.EqualTo(5));
    var buf = new byte[5];
    int read = cs.Read(buf, 0, 5);
    Assert.That(read, Is.EqualTo(5));
    Assert.That(buf, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Read_MultipleSegments() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5];
    byte[] seg3 = [6, 7, 8, 9];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2), new MemoryStream(seg3)
    ]);

    Assert.That(cs.Length, Is.EqualTo(9));
    var buf = new byte[9];
    int read = cs.Read(buf, 0, 9);
    Assert.That(read, Is.EqualTo(9));
    Assert.That(buf, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
  }

  [Category("Boundary")]
  [Test]
  public void Read_AcrossSegmentBoundary() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5, 6];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2)
    ]);

    cs.Position = 2;
    var buf = new byte[3];
    int read = cs.Read(buf, 0, 3);
    Assert.That(read, Is.EqualTo(3));
    Assert.That(buf, Is.EqualTo(new byte[] { 3, 4, 5 }));
  }

  [Category("HappyPath")]
  [Test]
  public void Seek_Begin() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5, 6];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2)
    ]);

    cs.Seek(4, SeekOrigin.Begin);
    Assert.That(cs.Position, Is.EqualTo(4));
    var buf = new byte[1];
    cs.ReadExactly(buf);
    Assert.That(buf[0], Is.EqualTo(5));
  }

  [Category("HappyPath")]
  [Test]
  public void Seek_End() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5, 6];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2)
    ]);

    cs.Seek(-2, SeekOrigin.End);
    Assert.That(cs.Position, Is.EqualTo(4));
  }

  [Category("HappyPath")]
  [Test]
  public void Seek_Current() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5, 6];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2)
    ]);

    cs.Position = 2;
    cs.Seek(2, SeekOrigin.Current);
    Assert.That(cs.Position, Is.EqualTo(4));
  }

  [Category("HappyPath")]
  [Test]
  public void Properties() {
    using var cs = new ConcatenatedStream([new MemoryStream([1, 2, 3])]);

    Assert.Multiple(() => {
      Assert.That(cs.CanRead, Is.True);
      Assert.That(cs.CanSeek, Is.True);
      Assert.That(cs.CanWrite, Is.False);
    });
  }

  [Category("Boundary")]
  [Test]
  public void Read_PartialFromEnd() {
    byte[] seg1 = [1, 2, 3];
    byte[] seg2 = [4, 5];
    using var cs = new ConcatenatedStream([
      new MemoryStream(seg1), new MemoryStream(seg2)
    ]);

    cs.Position = 3;
    var buf = new byte[10];
    cs.ReadExactly(buf.AsSpan(0, 2));
    Assert.That(buf[0], Is.EqualTo(4));
    Assert.That(buf[1], Is.EqualTo(5));
  }

  [Category("Exception")]
  [Test]
  public void Write_Throws() {
    using var cs = new ConcatenatedStream([new MemoryStream([1, 2, 3])]);
    Assert.Throws<NotSupportedException>(() => cs.Write([0], 0, 1));
  }

  [Category("HappyPath")]
  [Test]
  public void Dispose_ClosesSegments() {
    var seg = new MemoryStream([1, 2, 3]);
    var cs = new ConcatenatedStream([seg]);
    cs.Dispose();
    Assert.Throws<ObjectDisposedException>(() => { _ = seg.Length; });
  }

  [Category("HappyPath")]
  [Test]
  public void Dispose_LeaveOpen() {
    var seg = new MemoryStream([1, 2, 3]);
    var cs = new ConcatenatedStream([seg], leaveOpen: true);
    cs.Dispose();
    Assert.That(seg.Length, Is.EqualTo(3)); // still accessible
  }
}
