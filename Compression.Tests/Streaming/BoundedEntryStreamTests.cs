using Compression.Registry.Streaming;

namespace Compression.Tests.Streaming;

/// <summary>
/// Validates the universal per-entry isolation primitive: a
/// <see cref="BoundedEntryStream"/> never produces more than its declared
/// LogicalSize bytes regardless of what the underlying stream contains.
/// Slack bytes, adjacent-entry bytes, and padding past the bound are
/// physically unreachable through the wrapper.
/// </summary>
[TestFixture]
public class BoundedEntryStreamTests {

  [Test, Category("Spec")]
  public void Read_PastBound_ReturnsZero() {
    var inner = new MemoryStream(new byte[100]); // 100 bytes underlying
    using var bounded = new BoundedEntryStream(inner, logicalSize: 32, leaveOpen: true);

    var buf = new byte[64];
    var totalRead = 0;
    int n;
    while ((n = bounded.Read(buf, 0, buf.Length)) > 0)
      totalRead += n;

    Assert.That(totalRead, Is.EqualTo(32), "exactly LogicalSize bytes produced");
    Assert.That(bounded.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past bound returns 0");
  }

  [Test, Category("Spec")]
  public void Length_ReportsLogicalSize_NotInnerLength() {
    var inner = new MemoryStream(new byte[1024]);
    using var bounded = new BoundedEntryStream(inner, logicalSize: 256);
    Assert.That(bounded.Length, Is.EqualTo(256));
    Assert.That(inner.Length, Is.EqualTo(1024));
  }

  [Test, Category("Spec")]
  public void Seek_PastBound_ClampsToLogicalSize() {
    var inner = new MemoryStream(new byte[1024]);
    using var bounded = new BoundedEntryStream(inner, logicalSize: 100);
    var pos = bounded.Seek(500, SeekOrigin.Begin);
    Assert.That(pos, Is.EqualTo(100), "seek past LogicalSize clamps to bound");
    Assert.That(bounded.Read(new byte[16], 0, 16), Is.EqualTo(0), "read at clamped pos returns 0");
  }

  [Test, Category("Spec")]
  public void Seek_FromEndPlusOffset_ClampsCorrectly() {
    var inner = new MemoryStream(new byte[1024]);
    using var bounded = new BoundedEntryStream(inner, logicalSize: 100);
    var pos = bounded.Seek(50, SeekOrigin.End);
    Assert.That(pos, Is.EqualTo(100), "seek End+50 clamps to LogicalSize");
  }

  [Test, Category("Spec")]
  public void Marker_BytesAfterBound_NeverLeakThroughCopyTo() {
    // Lay out: [00..31] valid entry, [32..35] forbidden marker DE AD BE EF,
    // then more padding. CopyTo must produce only the valid 32 bytes.
    var underlying = new byte[100];
    for (var i = 0; i < 32; i++) underlying[i] = (byte)(i & 0xFF);
    underlying[32] = 0xDE; underlying[33] = 0xAD;
    underlying[34] = 0xBE; underlying[35] = 0xEF;

    var inner = new MemoryStream(underlying);
    using var bounded = new BoundedEntryStream(inner, logicalSize: 32);
    using var sink = new MemoryStream();
    bounded.CopyTo(sink);

    var result = sink.ToArray();
    Assert.That(result.Length, Is.EqualTo(32),
      "CopyTo produces exactly LogicalSize bytes, no overflow into slack");
    // Verify the marker is absent from the result.
    for (var i = 0; i + 3 < result.Length; i++) {
      var leak = result[i] == 0xDE && result[i + 1] == 0xAD
              && result[i + 2] == 0xBE && result[i + 3] == 0xEF;
      Assert.That(leak, Is.False, $"forbidden marker leaked into output at offset {i}");
    }
  }

  [Test, Category("Spec")]
  public void Read_AcrossBoundary_TruncatesRequestedCount() {
    var inner = new MemoryStream(new byte[100]);
    using var bounded = new BoundedEntryStream(inner, logicalSize: 10);
    var buf = new byte[64];
    var n = bounded.Read(buf, 0, 64);
    Assert.That(n, Is.EqualTo(10), "first Read returns at most LogicalSize bytes");
    Assert.That(bounded.Read(buf, 0, 64), Is.EqualTo(0));
  }

  [Test, Category("Spec")]
  public void IsBoundedToSize_IsAlwaysTrue() {
    using var bounded = new BoundedEntryStream(new MemoryStream(), 0);
    Assert.That(bounded.IsBoundedToSize, Is.True);
  }

  [Test, Category("Spec")]
  public void Write_Throws_StreamIsReadOnly() {
    using var bounded = new BoundedEntryStream(new MemoryStream(new byte[32]), 32);
    Assert.That(bounded.CanWrite, Is.False);
    Assert.Throws<NotSupportedException>(() => bounded.Write(new byte[4], 0, 4));
  }

  [Test, Category("Spec")]
  public void LeaveOpenFalse_DisposesInner() {
    var inner = new MemoryStream(new byte[16]);
    var bounded = new BoundedEntryStream(inner, 16, leaveOpen: false);
    bounded.Dispose();
    Assert.Throws<ObjectDisposedException>(() => inner.ReadByte());
  }

  [Test, Category("Spec")]
  public void LeaveOpenTrue_InnerStaysOpen() {
    var inner = new MemoryStream(new byte[16]);
    var bounded = new BoundedEntryStream(inner, 16, leaveOpen: true);
    bounded.Dispose();
    // Inner should still be usable.
    inner.Position = 0;
    Assert.That(inner.ReadByte(), Is.EqualTo(0));
  }
}
