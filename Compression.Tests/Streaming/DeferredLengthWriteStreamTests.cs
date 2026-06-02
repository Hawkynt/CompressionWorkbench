using Compression.Registry.Streaming;

namespace Compression.Tests.Streaming;

/// <summary>
/// Validates the deferred-length write contract: writes accumulate in
/// memory up to a configurable threshold, then spill to a temp file; the
/// commit callback fires on dispose with the final byte count and a
/// re-opener that owns the spilled file's lifetime; Cancel() suppresses
/// the callback and cleans up the temp file.
/// </summary>
[TestFixture]
public class DeferredLengthWriteStreamTests {

  private static string MakeSpillDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-dlw-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryClean(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
  }

  [Test, Category("HappyPath")]
  public void Write_BelowThreshold_StaysInMemory() {
    var dir = MakeSpillDir();
    try {
      long capturedSize = -1;
      Func<Stream>? capturedFactory = null;
      using (var dlw = new DeferredLengthWriteStream(
          (size, factory) => { capturedSize = size; capturedFactory = factory; },
          spillThresholdBytes: 4096,
          spillDirectory: dir)) {
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);
        dlw.Write(payload, 0, payload.Length);
        Assert.That(dlw.BytesWritten, Is.EqualTo(1024));
        Assert.That(dlw.HasSpilled, Is.False, "1 KB under 4 KB threshold must NOT spill");
        Assert.That(Directory.GetFiles(dir), Is.Empty, "no spill file must be created");
      }
      Assert.That(capturedSize, Is.EqualTo(1024));
      Assert.That(capturedFactory is not null, Is.True);
      // Consume the in-memory snapshot.
      using var s = capturedFactory!();
      Assert.That(s.Length, Is.EqualTo(1024));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void Write_AboveThreshold_SpillsToTempFile() {
    var dir = MakeSpillDir();
    try {
      string? observedSpillPath = null;
      using (var dlw = new DeferredLengthWriteStream(
          (_, _) => { },
          spillThresholdBytes: 256,
          spillDirectory: dir)) {
        dlw.Write(new byte[1024], 0, 1024);
        observedSpillPath = dlw.SpillPath;
        Assert.That(dlw.HasSpilled, Is.True, "1024 bytes over 256 threshold must spill");
        Assert.That(observedSpillPath, Is.Not.Null);
        Assert.That(Path.GetFileName(observedSpillPath!), Does.StartWith("cwb_dlw_"));
        Assert.That(File.Exists(observedSpillPath!), Is.True);
        // BytesWritten tracks the total regardless of where the bytes
        // landed (we can't reliably probe the spill file's on-disk size
        // here because the FileStream may still be buffering).
        Assert.That(dlw.BytesWritten, Is.EqualTo(1024));
        // Cancel so this leaves no leftover.
        dlw.Cancel();
      }
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void Dispose_AfterSpill_DeletesTempFileAfterContentRead() {
    var dir = MakeSpillDir();
    try {
      Func<Stream>? capturedFactory = null;
      string? spillPathSnapshot = null;
      using (var dlw = new DeferredLengthWriteStream(
          (_, factory) => { capturedFactory = factory; },
          spillThresholdBytes: 64,
          spillDirectory: dir)) {
        var payload = new byte[256];
        Random.Shared.NextBytes(payload);
        dlw.Write(payload, 0, payload.Length);
        spillPathSnapshot = dlw.SpillPath;
      }

      Assert.That(spillPathSnapshot, Is.Not.Null);
      Assert.That(File.Exists(spillPathSnapshot!), Is.True,
        "tempfile must still exist until the consumer reads + disposes the re-opened stream");

      using (var s = capturedFactory!()) {
        Assert.That(s.Length, Is.EqualTo(256));
        using var sink = new MemoryStream();
        s.CopyTo(sink);
        Assert.That(sink.Length, Is.EqualTo(256));
      }
      Assert.That(File.Exists(spillPathSnapshot!), Is.False,
        "tempfile must be deleted when the consumer disposes the returned stream");
    } finally { TryClean(dir); }
  }

  [Test, Category("ErrorHandling")]
  public void Cancel_BeforeDispose_SuppressesCallback() {
    var dir = MakeSpillDir();
    try {
      var callbackFired = false;
      using (var dlw = new DeferredLengthWriteStream(
          (_, _) => callbackFired = true,
          spillThresholdBytes: 4096,
          spillDirectory: dir)) {
        dlw.Write(new byte[100], 0, 100);
        dlw.Cancel();
      }
      Assert.That(callbackFired, Is.False, "onClose must NOT be invoked after Cancel");
    } finally { TryClean(dir); }
  }

  [Test, Category("ErrorHandling")]
  public void Cancel_AfterSpill_DeletesTempFile() {
    var dir = MakeSpillDir();
    try {
      string? spillPath;
      using (var dlw = new DeferredLengthWriteStream(
          (_, _) => Assert.Fail("callback must not fire after Cancel"),
          spillThresholdBytes: 64,
          spillDirectory: dir)) {
        dlw.Write(new byte[256], 0, 256);
        spillPath = dlw.SpillPath;
        Assert.That(File.Exists(spillPath!), Is.True);
        dlw.Cancel();
      }
      Assert.That(File.Exists(spillPath!), Is.False,
        "spill file must be cleaned up on Cancel + Dispose");
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void MultipleParallelStreams_DontInterfere() {
    var dir = MakeSpillDir();
    try {
      byte[]? captured1 = null;
      byte[]? captured2 = null;

      using var dlw1 = new DeferredLengthWriteStream(
        (_, factory) => {
          using var s = factory();
          using var sink = new MemoryStream();
          s.CopyTo(sink);
          captured1 = sink.ToArray();
        }, spillThresholdBytes: 4096, spillDirectory: dir);

      using var dlw2 = new DeferredLengthWriteStream(
        (_, factory) => {
          using var s = factory();
          using var sink = new MemoryStream();
          s.CopyTo(sink);
          captured2 = sink.ToArray();
        }, spillThresholdBytes: 4096, spillDirectory: dir);

      // Interleave writes from two distinct patterns; if the streams shared
      // any state, the captured payloads would mix.
      var pattern1 = Enumerable.Range(0, 1000).Select(i => (byte)(i & 0xFF)).ToArray();
      var pattern2 = Enumerable.Range(0, 1500).Select(i => (byte)((i ^ 0xA5) & 0xFF)).ToArray();
      dlw1.Write(pattern1, 0, 500);
      dlw2.Write(pattern2, 0, 700);
      dlw1.Write(pattern1, 500, 500);
      dlw2.Write(pattern2, 700, 800);

      dlw1.Dispose();
      dlw2.Dispose();

      Assert.That(captured1, Is.EqualTo(pattern1));
      Assert.That(captured2, Is.EqualTo(pattern2));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void CanWrite_True_CanReadOrSeek_False() {
    using var dlw = new DeferredLengthWriteStream((_, _) => { });
    Assert.That(dlw.CanWrite, Is.True);
    Assert.That(dlw.CanRead, Is.False);
    Assert.That(dlw.CanSeek, Is.False);
    Assert.Throws<NotSupportedException>(() => { _ = dlw.Read(new byte[1], 0, 1); });
    Assert.Throws<NotSupportedException>(() => dlw.Seek(0, SeekOrigin.Begin));
    Assert.Throws<NotSupportedException>(() => dlw.SetLength(0));
    dlw.Cancel();
  }

  [Test, Category("Spec")]
  public void BytesWritten_TracksAccurately_AcrossSpill() {
    var dir = MakeSpillDir();
    try {
      using var dlw = new DeferredLengthWriteStream(
        (_, _) => { },
        spillThresholdBytes: 100,
        spillDirectory: dir);

      dlw.Write(new byte[50], 0, 50);
      Assert.That(dlw.BytesWritten, Is.EqualTo(50));
      Assert.That(dlw.HasSpilled, Is.False);

      // This write pushes total to 200 > threshold 100 → spill.
      dlw.Write(new byte[150], 0, 150);
      Assert.That(dlw.BytesWritten, Is.EqualTo(200));
      Assert.That(dlw.HasSpilled, Is.True);

      // Further writes accumulate against the spilled file.
      dlw.Write(new byte[37], 0, 37);
      Assert.That(dlw.BytesWritten, Is.EqualTo(237));

      // WriteByte path.
      dlw.WriteByte(0xFF);
      Assert.That(dlw.BytesWritten, Is.EqualTo(238));

      // Span path.
      dlw.Write(new byte[] { 1, 2, 3, 4 }.AsSpan());
      Assert.That(dlw.BytesWritten, Is.EqualTo(242));

      dlw.Cancel();
    } finally { TryClean(dir); }
  }
}
