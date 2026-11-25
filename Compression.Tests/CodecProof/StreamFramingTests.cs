using System.Buffers.Binary;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Lzma;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Container framing around the core stream codecs: what may precede, separate or follow
/// the compressed units of a .zst, .xz, .bz2 file, and where the .lzma encoder starts
/// reading its input. The reference CLIs (zstd, xz, bzip2 in WSL) decode the very same
/// bytes in the oracle tests, so each expectation is the reference tool's behaviour.
/// </summary>
[TestFixture]
public class StreamFramingTests {

  private static readonly byte[] PayloadA = "first payload, repeated repeated repeated\n"u8.ToArray();
  private static readonly byte[] PayloadB = "second payload with other words in it\n"u8.ToArray();

  // ── zstd: skippable frames (RFC 8878 §3.1.2) ──────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Zstd_GivenSkippableFrameBeforeFrame_WhenDecoded_ThenItIsSkipped() {
    var file = Concat(Skippable(0, [1, 2, 3, 4, 5]), ZstdCompress(PayloadA));
    Assert.That(ZstdDecompress(file), Is.EqualTo(PayloadA));
  }

  [Category("HappyPath")]
  [Test]
  public void Zstd_GivenSkippableFrameBetweenFrames_WhenDecoded_ThenBothFramesAreReturned() {
    var file = Concat(ZstdCompress(PayloadA), Skippable(7, new byte[300]), ZstdCompress(PayloadB));
    Assert.That(ZstdDecompress(file), Is.EqualTo(Concat(PayloadA, PayloadB)));
  }

  [Category("HappyPath")]
  [Test]
  public void Zstd_GivenSkippableFrameAfterLastFrame_WhenDecoded_ThenPayloadIsUnchanged() {
    var file = Concat(ZstdCompress(PayloadA), Skippable(15, [9]));
    Assert.That(ZstdDecompress(file), Is.EqualTo(PayloadA));
  }

  [Category("Boundary")]
  [TestCase(0x0)]
  [TestCase(0xF)]
  public void Zstd_GivenEveryMagicNibble_WhenDecoded_ThenTheFrameIsSkippable(int nibble) {
    var file = Concat(Skippable(nibble, [0xAA, 0xBB]), ZstdCompress(PayloadA));
    Assert.That(ZstdDecompress(file), Is.EqualTo(PayloadA));
  }

  [Category("Boundary")]
  [Test]
  public void Zstd_GivenZeroLengthSkippableFrames_WhenDecoded_ThenTheyAreSkipped() {
    var file = Concat(Skippable(1, []), Skippable(2, []), ZstdCompress(PayloadA), Skippable(3, []));
    Assert.That(ZstdDecompress(file), Is.EqualTo(PayloadA));
  }

  [Category("EdgeCase")]
  [Test]
  public void Zstd_GivenOnlySkippableFrames_WhenDecoded_ThenOutputIsEmpty() {
    var file = Concat(Skippable(0, [1, 2, 3]), Skippable(4, []));
    Assert.That(ZstdDecompress(file), Is.Empty);
  }

  [Category("Exception")]
  [Test]
  public void Zstd_GivenTruncatedSkippableFrame_WhenDecoded_ThenItThrows() {
    var skippable = Skippable(0, new byte[16]);
    var file = skippable[..^4];
    Assert.That(() => ZstdDecompress(file), Throws.InstanceOf<IOException>().Or.InstanceOf<InvalidDataException>());
  }

  [Category("Exception")]
  [Test]
  public void Zstd_GivenMagicJustAboveSkippableRange_WhenDecoded_ThenItIsRejected() {
    var file = Concat(Le32(0x184D2A60), Le32(0), ZstdCompress(PayloadA));
    Assert.That(() => ZstdDecompress(file), Throws.InstanceOf<InvalidDataException>());
  }

  // ── xz: concatenated streams and stream padding (xz file format 1.2.1 §2) ──

  [Category("HappyPath")]
  [Test]
  public void Xz_GivenEmptyStreamBetweenStreams_WhenDecoded_ThenTheStreamAfterItIsDecoded() {
    var file = Concat(XzCompress(PayloadA), XzCompress([]), XzCompress(PayloadB));
    Assert.That(XzDecompress(file), Is.EqualTo(Concat(PayloadA, PayloadB)));
  }

  [Category("EdgeCase")]
  [Test]
  public void Xz_GivenLeadingEmptyStream_WhenDecoded_ThenTheFollowingStreamIsDecoded() {
    var file = Concat(XzCompress([]), XzCompress(PayloadA));
    Assert.That(XzDecompress(file), Is.EqualTo(PayloadA));
  }

  [Category("Boundary")]
  [Test]
  public void Xz_GivenStreamPaddingAroundAnEmptyStream_WhenDecoded_ThenAllPayloadIsReturned() {
    var file = Concat(XzCompress(PayloadA), new byte[4], XzCompress([]), new byte[8], XzCompress(PayloadB), new byte[4]);
    Assert.That(XzDecompress(file), Is.EqualTo(Concat(PayloadA, PayloadB)));
  }

  [Category("EdgeCase")]
  [Test]
  public void Xz_GivenOnlyEmptyStreams_WhenDecoded_ThenOutputIsEmpty() {
    var file = Concat(XzCompress([]), XzCompress([]));
    Assert.That(XzDecompress(file), Is.Empty);
  }

  // ── bzip2: concatenated streams (what `cat a.bz2 b.bz2` and pbzip2 produce) ──

  [Category("HappyPath")]
  [Test]
  public void Bzip2_GivenTwoConcatenatedStreams_WhenDecoded_ThenBothPayloadsAreReturned() {
    var file = Concat(Bzip2Compress(PayloadA), Bzip2Compress(PayloadB));
    Assert.That(Bzip2Decompress(file), Is.EqualTo(Concat(PayloadA, PayloadB)));
  }

  [Category("EdgeCase")]
  [Test]
  public void Bzip2_GivenEmptyStreamBetweenStreams_WhenDecoded_ThenBothPayloadsAreReturned() {
    var file = Concat(Bzip2Compress(PayloadA), Bzip2Compress([]), Bzip2Compress(PayloadB, blockSize100k: 1));
    Assert.That(Bzip2Decompress(file), Is.EqualTo(Concat(PayloadA, PayloadB)));
  }

  [Category("EdgeCase")]
  [Test]
  public void Bzip2_GivenTrailingGarbage_WhenDecoded_ThenTheStreamPayloadIsReturned() {
    var file = Concat(Bzip2Compress(PayloadA), "garbage"u8.ToArray());
    Assert.That(Bzip2Decompress(file), Is.EqualTo(PayloadA));
  }

  [Category("Exception")]
  [Test]
  public void Bzip2_GivenSecondStreamCorrupted_WhenDecoded_ThenItThrows() {
    var second = Bzip2Compress(PayloadB);
    second[^6] ^= 0x55; // inside the combined CRC of the second stream
    var file = Concat(Bzip2Compress(PayloadA), second);
    Assert.That(() => Bzip2Decompress(file), Throws.InstanceOf<InvalidDataException>());
  }

  // ── lzma-alone: Compress honours the input position ────────────────────

  [Category("HappyPath")]
  [Test]
  public void Lzma_GivenInputPositionedPastItsStart_WhenCompressed_ThenOnlyTheRemainderIsEncoded() {
    var buffer = Concat("HEADER"u8.ToArray(), PayloadA);
    using var input = new MemoryStream(buffer) { Position = 6 };
    using var output = new MemoryStream();
    LzmaStream.Compress(input, output);
    Assert.That(LzmaDecompress(output.ToArray()), Is.EqualTo(PayloadA));
  }

  [Category("Boundary")]
  [Test]
  public void Lzma_GivenInputPositionedAtItsEnd_WhenCompressed_ThenTheStreamIsEmpty() {
    using var input = new MemoryStream(PayloadA) { Position = PayloadA.Length };
    using var output = new MemoryStream();
    LzmaStream.Compress(input, output);
    var compressed = output.ToArray();
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(compressed.AsSpan(5, 8)), Is.Zero);
    Assert.That(LzmaDecompress(compressed), Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void Lzma_GivenExposableMemoryStreamWithOffset_WhenCompressed_ThenOnlyTheVisibleSliceIsEncoded() {
    var backing = Concat("xx"u8.ToArray(), PayloadA, "yy"u8.ToArray());
    using var input = new MemoryStream(backing, 2, PayloadA.Length, writable: false, publiclyVisible: true);
    using var output = new MemoryStream();
    LzmaStream.Compress(input, output);
    Assert.That(LzmaDecompress(output.ToArray()), Is.EqualTo(PayloadA));
  }

  // ── reference CLIs decode the same bytes the same way ──────────────────

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void Zstd_SkippableFrames_ReferenceZstdAgrees() {
    RequireWslTool("zstd");
    var file = Concat(Skippable(0, [1, 2, 3]), ZstdCompress(PayloadA), Skippable(15, new byte[40]), ZstdCompress(PayloadB));
    Assert.That(ReferenceDecode("zstd -dc", file), Is.EqualTo(ZstdDecompress(file)));
  }

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void Zstd_OnlySkippableFrames_ReferenceZstdAgrees() {
    RequireWslTool("zstd");
    var file = Concat(Skippable(0, [1, 2, 3]), Skippable(4, []));
    Assert.That(ReferenceDecode("zstd -dc", file), Is.EqualTo(ZstdDecompress(file)));
  }

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void Xz_PaddedStreamsWithEmptyMiddle_ReferenceXzAgrees() {
    RequireWslTool("xz");
    var file = Concat(XzCompress(PayloadA), new byte[4], XzCompress([]), XzCompress(PayloadB), new byte[8]);
    Assert.That(ReferenceDecode("xz -dc", file), Is.EqualTo(XzDecompress(file)));
  }

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void Bzip2_ConcatenatedStreams_ReferenceBzip2Agrees() {
    RequireWslTool("bzip2");
    var file = Concat(Bzip2Compress(PayloadA), Bzip2Compress([]), Bzip2Compress(PayloadB));
    Assert.That(ReferenceDecode("bzip2 -dc", file), Is.EqualTo(Bzip2Decompress(file)));
  }

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void Bzip2_StreamsConcatenatedByReferenceTool_DecodeInOurs() {
    RequireWslTool("bzip2");
    var dir = Directory.CreateTempSubdirectory("cwb_bz2cat_").FullName;
    try {
      var a = Path.Combine(dir, "a");
      var b = Path.Combine(dir, "b");
      File.WriteAllBytes(a, PayloadA);
      File.WriteAllBytes(b, PayloadB);
      var joined = Path.Combine(dir, "ab.bz2");
      var r = FsInteropToolbox.RunWsl(
        $"bzip2 -c {FsInteropToolbox.WinToWsl(a)} > {FsInteropToolbox.WinToWsl(joined)} && " +
        $"bzip2 -c {FsInteropToolbox.WinToWsl(b)} >> {FsInteropToolbox.WinToWsl(joined)}");
      Assert.That(r.ExitCode, Is.Zero, r.StdErr);
      Assert.That(Bzip2Decompress(File.ReadAllBytes(joined)), Is.EqualTo(Concat(PayloadA, PayloadB)));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private static void RequireWslTool(string tool) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} not reachable in WSL");
  }

  private static byte[] ReferenceDecode(string command, byte[] file) {
    var dir = Directory.CreateTempSubdirectory("cwb_framing_").FullName;
    try {
      var input = Path.Combine(dir, "in.bin");
      var output = Path.Combine(dir, "out.bin");
      File.WriteAllBytes(input, file);
      var r = FsInteropToolbox.RunWsl(
        $"{command} {FsInteropToolbox.WinToWsl(input)} > {FsInteropToolbox.WinToWsl(output)}");
      Assert.That(r.ExitCode, Is.Zero, $"{command} rejected the file:\n{r.StdErr}");
      return File.ReadAllBytes(output);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

  private static byte[] Le32(uint value) {
    var b = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, value);
    return b;
  }

  private static byte[] Skippable(int nibble, byte[] userData)
    => Concat(Le32(ZstdConstants.SkippableFrameMagicBase + (uint)nibble), Le32((uint)userData.Length), userData);

  private static byte[] ZstdCompress(byte[] data) {
    using var ms = new MemoryStream();
    using (var z = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      z.Write(data);
    return ms.ToArray();
  }

  private static byte[] ZstdDecompress(byte[] data) {
    using var z = new ZstdStream(new MemoryStream(data), CompressionStreamMode.Decompress);
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

  private static byte[] Bzip2Compress(byte[] data, int blockSize100k = 9) {
    using var ms = new MemoryStream();
    using (var b = new Bzip2Stream(ms, CompressionStreamMode.Compress, blockSize100k, leaveOpen: true))
      b.Write(data);
    return ms.ToArray();
  }

  private static byte[] Bzip2Decompress(byte[] data) {
    using var b = new Bzip2Stream(new MemoryStream(data), CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    b.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] LzmaDecompress(byte[] data) {
    using var output = new MemoryStream();
    LzmaStream.Decompress(new MemoryStream(data), output);
    return output.ToArray();
  }
}
