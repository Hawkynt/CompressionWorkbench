using Compression.Registry.Streaming;

namespace Compression.Tests.Streaming;

/// <summary>
/// Validates the bounded-write contract: overrun throws at the offending
/// <c>Write</c>, underrun throws on <c>Dispose</c>, and the wrapper never
/// trusts the inner stream's position so the bound is enforced regardless
/// of what the inner does.
/// </summary>
[TestFixture]
public class BoundedWriteStreamTests {

  [Test, Category("Spec")]
  public void Write_ExactlyDeclaredBytes_Succeeds() {
    using var inner = new MemoryStream();
    using var bounded = new BoundedWriteStream(inner, logicalSize: 32);
    var payload = new byte[32];
    Random.Shared.NextBytes(payload);
    bounded.Write(payload, 0, payload.Length);
    Assert.That(bounded.BytesWritten, Is.EqualTo(32));
    Assert.That(bounded.Length, Is.EqualTo(32));
  }

  [Test, Category("Spec")]
  public void Write_PastBound_ThrowsAtOffendingWrite() {
    using var inner = new MemoryStream();
    using var bounded = new BoundedWriteStream(inner, logicalSize: 10);
    bounded.Write(new byte[8], 0, 8);
    Assert.Throws<InvalidOperationException>(() => bounded.Write(new byte[5], 0, 5),
      "writing past LogicalSize must throw");
  }

  [Test, Category("Spec")]
  public void WriteByte_PastBound_Throws() {
    using var inner = new MemoryStream();
    using var bounded = new BoundedWriteStream(inner, logicalSize: 2);
    bounded.WriteByte(0xAA);
    bounded.WriteByte(0xBB);
    Assert.Throws<InvalidOperationException>(() => bounded.WriteByte(0xCC));
  }

  [Test, Category("Spec")]
  public void Underrun_ThrowsOnDispose() {
    using var inner = new MemoryStream();
    var bounded = new BoundedWriteStream(inner, logicalSize: 32, leaveOpen: true);
    bounded.Write(new byte[16], 0, 16); // only half
    var ex = Assert.Throws<InvalidOperationException>(() => bounded.Dispose());
    Assert.That(ex!.Message, Does.Contain("underrun"));
  }

  [Test, Category("Spec")]
  public void Cancel_SuppressesUnderrunCheck() {
    using var inner = new MemoryStream();
    var bounded = new BoundedWriteStream(inner, logicalSize: 32, leaveOpen: true);
    bounded.Write(new byte[8], 0, 8);
    bounded.Cancel();
    // Must not throw on dispose after Cancel.
    Assert.DoesNotThrow(() => bounded.Dispose());
  }

  [Test, Category("Spec")]
  public void CreateBuffered_OnExactLength_CommitsBytes() {
    byte[]? committed = null;
    using (var bounded = BoundedWriteStream.CreateBuffered(8, bytes => committed = bytes)) {
      bounded.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
    }
    Assert.That(committed, Is.Not.Null);
    Assert.That(committed!, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
  }

  [Test, Category("Spec")]
  public void CreateBuffered_OnUnderrun_DoesNotCommit_AndThrows() {
    byte[]? committed = null;
    var bounded = BoundedWriteStream.CreateBuffered(8, bytes => committed = bytes);
    bounded.Write(new byte[] { 1, 2, 3 }, 0, 3); // underrun
    Assert.Throws<InvalidOperationException>(() => bounded.Dispose());
    Assert.That(committed, Is.Null, "commit callback must not fire on torn entry");
  }

  [Test, Category("Spec")]
  public void IsBoundedToSize_IsAlwaysTrue() {
    using var inner = new MemoryStream();
    using var bounded = new BoundedWriteStream(inner, logicalSize: 0);
    Assert.That(bounded.IsBoundedToSize, Is.True);
  }
}
