using Compression.Core.Streams;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Where a decoder stops: a zstd decoder embedded in a container must leave the bytes after its
/// last frame unread, and xz stream padding must be a multiple of four bytes as xz requires
/// (`xz -t` is the oracle).
/// </summary>
[TestFixture]
public class StreamBoundaryTests {

  private static readonly byte[] Payload = "boundary payload, boundary payload, boundary payload\n"u8.ToArray();
  private static readonly byte[] Trailer = "TRAILER-RECORD"u8.ToArray();

  // ── zstd: nothing past the last frame is consumed ─────────────────────

  [Category("HappyPath")]
  [Test]
  public void Zstd_GivenDataAfterTheLastFrame_WhenDecoded_ThenThePositionIsTheEndOfTheFrame() {
    var frame = ZstdCompress(Payload);
    using var input = new MemoryStream([.. frame, .. Trailer]);
    Assert.That(ZstdDecompress(input), Is.EqualTo(Payload));
    Assert.That(input.Position, Is.EqualTo(frame.Length));
  }

  [Category("Boundary")]
  [TestCase(1)]
  [TestCase(3)]
  public void Zstd_GivenFewerThanFourBytesAfterTheLastFrame_WhenDecoded_ThenTheyAreLeftUnread(int count) {
    var frame = ZstdCompress(Payload);
    using var input = new MemoryStream([.. frame, .. Trailer[..count]]);
    Assert.That(ZstdDecompress(input), Is.EqualTo(Payload));
    Assert.That(input.Position, Is.EqualTo(frame.Length));
  }

  [Category("EdgeCase")]
  [Test]
  public void Zstd_GivenSkippableFrameThenData_WhenDecoded_ThenTheSkippableFrameIsConsumedButNotTheData() {
    var frame = ZstdCompress(Payload);
    byte[] skippable = [0x50, 0x2A, 0x4D, 0x18, 2, 0, 0, 0, 0xAA, 0xBB];
    using var input = new MemoryStream([.. frame, .. skippable, .. Trailer]);
    Assert.That(ZstdDecompress(input), Is.EqualTo(Payload));
    Assert.That(input.Position, Is.EqualTo(frame.Length + skippable.Length));
  }

  // ── xz: stream padding is a multiple of four ──────────────────────────

  [Category("Boundary")]
  [TestCase(0)]
  [TestCase(4)]
  [TestCase(8)]
  public void Xz_GivenTrailingPaddingInFourByteUnits_WhenDecoded_ThenAccepted(int padding)
    => Assert.That(XzDecompress([.. XzCompress(Payload), .. new byte[padding]]), Is.EqualTo(Payload));

  [Category("Exception")]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(5)]
  public void Xz_GivenTrailingPaddingNotInFourByteUnits_WhenDecoded_ThenInvalidData(int padding)
    => Assert.Throws<InvalidDataException>(() => XzDecompress([.. XzCompress(Payload), .. new byte[padding]]));

  [Category("Exception")]
  [TestCase(2)]
  [TestCase(6)]
  public void Xz_GivenPaddingBetweenStreamsNotInFourByteUnits_WhenDecoded_ThenInvalidData(int padding)
    => Assert.Throws<InvalidDataException>(() => XzDecompress([.. XzCompress(Payload), .. new byte[padding], .. XzCompress(Payload)]));

  [Category("OsIntegration")]
  [Category("Wsl")]
  [TestCase(0, true)]
  [TestCase(4, true)]
  [TestCase(8, true)]
  [TestCase(1, false)]
  [TestCase(2, false)]
  [TestCase(3, false)]
  [TestCase(5, false)]
  public void Xz_TrailingPadding_ReferenceXzAgrees(int padding, bool valid) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("xz"))
      Assert.Ignore("xz not reachable in WSL");
    var file = XzCompress(Payload).Concat(new byte[padding]).ToArray();
    var dir = Directory.CreateTempSubdirectory("cwb_xzpad_").FullName;
    try {
      var path = Path.Combine(dir, "p.xz");
      File.WriteAllBytes(path, file);
      var r = FsInteropToolbox.RunWsl($"xz -t {FsInteropToolbox.WinToWsl(path)}");
      Assert.That(r.ExitCode == 0, Is.EqualTo(valid), $"xz -t: {r.StdErr}");
      if (valid)
        Assert.That(XzDecompress(file), Is.EqualTo(Payload));
      else
        Assert.Throws<InvalidDataException>(() => XzDecompress(file));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private static byte[] ZstdCompress(byte[] data) {
    using var ms = new MemoryStream();
    using (var z = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      z.Write(data);
    return ms.ToArray();
  }

  private static byte[] ZstdDecompress(Stream input) {
    using var z = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    z.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] XzCompress(byte[] data) {
    using var ms = new MemoryStream();
    using (var x = new XzStream(ms, CompressionStreamMode.Compress, 1 << 20, leaveOpen: true))
      x.Write(data);
    return ms.ToArray();
  }

  private static byte[] XzDecompress(byte[] data) {
    using var x = new XzStream(new MemoryStream(data), CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    x.CopyTo(output);
    return output.ToArray();
  }
}
