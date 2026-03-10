using Compression.Core.DataStructures;

namespace Compression.Tests.DataStructures;

[TestFixture]
public class SlidingWindowTests {
  [Test]
  public void WriteByte_IncreasesCount() {
    var window = new SlidingWindow(8);

    window.WriteByte(0xAA);
    Assert.That(window.Count, Is.EqualTo(1));

    window.WriteByte(0xBB);
    Assert.That(window.Count, Is.EqualTo(2));
  }

  [Test]
  public void GetByte_ReturnsCorrectByte() {
    var window = new SlidingWindow(8);
    window.WriteByte(0x10);
    window.WriteByte(0x20);
    window.WriteByte(0x30);

    Assert.That(window.GetByte(1), Is.EqualTo(0x30)); // last written
    Assert.That(window.GetByte(2), Is.EqualTo(0x20));
    Assert.That(window.GetByte(3), Is.EqualTo(0x10));
  }

  [Test]
  public void WrapAround_OverwritesOldData() {
    var window = new SlidingWindow(4);

    for (byte i = 0; i < 6; i++)
      window.WriteByte(i);

    // Window should contain [2, 3, 4, 5], count clamped at 4
    Assert.That(window.Count, Is.EqualTo(4));
    Assert.That(window.GetByte(1), Is.EqualTo(5));
    Assert.That(window.GetByte(2), Is.EqualTo(4));
    Assert.That(window.GetByte(3), Is.EqualTo(3));
    Assert.That(window.GetByte(4), Is.EqualTo(2));
  }

  [Test]
  public void CopyFromWindow_SimpleCopy() {
    var window = new SlidingWindow(32);
    byte[] data = [0x41, 0x42, 0x43];
    foreach (byte b in data)
      window.WriteByte(b);

    var output = new byte[3];
    window.CopyFromWindow(3, 3, output);

    Assert.That(output, Is.EqualTo(new byte[] { 0x41, 0x42, 0x43 }));
  }

  [Test]
  public void CopyFromWindow_OverlappingCopy_RunLengthRepeat() {
    // distance=1, length=5 should repeat the last byte 5 times
    var window = new SlidingWindow(32);
    window.WriteByte(0xAA);

    var output = new byte[5];
    window.CopyFromWindow(1, 5, output);

    Assert.That(output, Is.EqualTo(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }));
  }

  [Test]
  public void CopyFromWindow_OverlappingCopy_PatternRepeat() {
    // distance=2, length=6 should repeat a 2-byte pattern 3 times
    var window = new SlidingWindow(32);
    window.WriteByte(0xAB);
    window.WriteByte(0xCD);

    var output = new byte[6];
    window.CopyFromWindow(2, 6, output);

    Assert.That(output, Is.EqualTo(new byte[] { 0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD }));
  }

  [Test]
  public void CopyFromWindow_DistanceExceedsCount_Throws() {
    var window = new SlidingWindow(32);
    window.WriteByte(0xFF);

    var output = new byte[1];
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      window.CopyFromWindow(5, 1, output));
  }

  [Test]
  public void CopyFromWindow_DistanceZero_Throws() {
    var window = new SlidingWindow(32);
    window.WriteByte(0xFF);

    var output = new byte[1];
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      window.CopyFromWindow(0, 1, output));
  }

  [Test]
  public void WrapAround_CopyAcrossWrapBoundary() {
    var window = new SlidingWindow(4);

    // Fill: [0, 1, 2, 3]
    for (byte i = 0; i < 4; i++)
      window.WriteByte(i);

    // Overwrite position 0: [4, 1, 2, 3]
    window.WriteByte(4);
    // Now: [4, 1, 2, 3] with position at 1, so data is 1, 2, 3, 4

    // Copy distance=4, length=2: start at "1"
    var output = new byte[2];
    window.CopyFromWindow(4, 2, output);

    Assert.That(output, Is.EqualTo(new byte[] { 1, 2 }));
  }

  [Test]
  public void WriteBytes_BulkWrite() {
    var window = new SlidingWindow(16);
    byte[] data = [1, 2, 3, 4, 5];
    window.WriteBytes(data);

    Assert.That(window.Count, Is.EqualTo(5));
    Assert.That(window.GetByte(1), Is.EqualTo(5));
    Assert.That(window.GetByte(5), Is.EqualTo(1));
  }

  [Test]
  public void WriteBytes_WrapsAround() {
    var window = new SlidingWindow(4);
    byte[] data = [1, 2, 3, 4, 5, 6];
    window.WriteBytes(data);

    Assert.That(window.Count, Is.EqualTo(4));
    Assert.That(window.GetByte(1), Is.EqualTo(6));
    Assert.That(window.GetByte(4), Is.EqualTo(3));
  }

  [Test]
  public void WriteBytes_ThenCopyFromWindow() {
    var window = new SlidingWindow(32);
    byte[] data = [0xAA, 0xBB, 0xCC];
    window.WriteBytes(data);

    var output = new byte[3];
    window.CopyFromWindow(3, 3, output);

    Assert.That(output, Is.EqualTo(data));
  }

  [Test]
  public void WriteBytes_ExactWindowSize() {
    var window = new SlidingWindow(4);
    byte[] data = [10, 20, 30, 40];
    window.WriteBytes(data);

    Assert.That(window.Count, Is.EqualTo(4));
    Assert.That(window.GetByte(1), Is.EqualTo(40));
    Assert.That(window.GetByte(4), Is.EqualTo(10));
  }

  [Test]
  public void WriteBytes_EmptySpan() {
    var window = new SlidingWindow(8);
    window.WriteBytes(ReadOnlySpan<byte>.Empty);
    Assert.That(window.Count, Is.EqualTo(0));
  }

  [Test]
  public void WriteBytes_EquivalentToByteByByte() {
    // Verify bulk write produces identical state as byte-by-byte
    var window1 = new SlidingWindow(8);
    var window2 = new SlidingWindow(8);

    byte[] data = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    foreach (byte b in data)
      window1.WriteByte(b);
    window2.WriteBytes(data);

    Assert.That(window2.Count, Is.EqualTo(window1.Count));
    for (int d = 1; d <= window1.Count; d++)
      Assert.That(window2.GetByte(d), Is.EqualTo(window1.GetByte(d)));
  }
}
