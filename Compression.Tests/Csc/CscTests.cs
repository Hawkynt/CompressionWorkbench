using System.Buffers.Binary;
using Compression.Core.Dictionary.Csc;
using FileFormat.Csc;

namespace Compression.Tests.Csc;

[TestFixture]
public class CscTests {
  private static byte[] RoundTrip(byte[] data, CscEncoderOptions? options = null) =>
    CscCodec.Decompress(CscCodec.Compress(data, options));

  // ── Round trips over the equivalence classes of input ──────────────────────

  [Test, Category("RoundTrip")]
  public void Empty_RoundTrips() => Assert.That(RoundTrip([]), Is.Empty);

  [Test, Category("RoundTrip")]
  public void SingleByte_RoundTrips() => Assert.That(RoundTrip([0x99]), Is.EqualTo(new byte[] { 0x99 }));

  [Test, Category("RoundTrip")]
  public void OverlappingMatch_RoundTrips() {
    var data = "abababababababababababababababab"u8.ToArray();
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndShrinks() {
    var data = new byte[100_000];
    var compressed = CscCodec.Compress(data);
    Assert.Multiple(() => {
      Assert.That(CscCodec.Decompress(compressed), Is.EqualTo(data));
      Assert.That(compressed.Length, Is.LessThan(200));
    });
  }

  [Test, Category("RoundTrip")]
  public void RandomData_IsStoredWithLittleOverhead() {
    var data = CscSamples.RandomBytes(50_000);
    var compressed = CscCodec.Compress(data);
    Assert.Multiple(() => {
      Assert.That(CscCodec.Decompress(compressed), Is.EqualTo(data));
      Assert.That(compressed.Length, Is.LessThan(data.Length + 100));
    });
  }

  [TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5)]
  [Category("RoundTrip")]
  public void EveryLevel_RoundTripsEveryCoder(int level) {
    var data = CscSamples.Mixed();
    Assert.That(RoundTrip(data, new(Level: level)), Is.EqualTo(data));
  }

  [TestCase(1), TestCase(2), TestCase(3), TestCase(4), TestCase(5)]
  [Category("RoundTrip")]
  public void SmallWindow_WrapsAndRoundTrips(int level) {
    // 200 KB through a 42 KiB window: the window wraps several times and matches must not straddle it.
    byte[] data = [.. CscSamples.Text(120_000, 11), .. CscSamples.X86(40_000, 12), .. CscSamples.Text(40_000, 11)];
    Assert.That(RoundTrip(data, new(Level: level, DictionarySize: 32 * 1024)), Is.EqualTo(data));
  }

  [TestCase(false, false, false), TestCase(true, false, false), TestCase(false, true, false), TestCase(false, false, true)]
  [Category("RoundTrip")]
  public void FilterSwitches_RoundTrip(bool delta, bool text, bool executable) {
    var data = CscSamples.Mixed();
    Assert.That(RoundTrip(data, new(DeltaFilter: delta, TextFilter: text, ExecutableFilter: executable)), Is.EqualTo(data));
  }

  // ── Boundaries: analyzer step (8 KiB), segment (2 MiB) ─────────────────────

  [TestCase(511), TestCase(512), TestCase(8191), TestCase(8192), TestCase(8193), TestCase(16384), TestCase(16385)]
  [Category("Boundary")]
  public void AnalyzerBlockBoundaries_RoundTrip(int length) {
    var data = CscSamples.Text(length, 3);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [TestCase(2 * 1024 * 1024 - 1), TestCase(2 * 1024 * 1024), TestCase(2 * 1024 * 1024 + 1)]
  [Category("Boundary")]
  public void SegmentBoundaries_RoundTrip(int length) {
    var data = CscSamples.Ramp(length);
    data[length / 3] ^= 0x5A;
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  // ── Header, as the reference tool writes it ───────────────────────────────

  [TestCase(0, 32u * 1024)]
  [TestCase(100, 32u * 1024)]
  [TestCase(22_528, 32u * 1024)]
  [TestCase(22_529, 22_529u + 10 * 1024)]
  [TestCase(1_000_000, 1_000_000u + 10 * 1024)]
  [Category("Spec")]
  public void Header_WindowIsInputLengthPlus10KiB_ClampedTo32KiB(int length, uint expectedWindow) {
    var compressed = CscCodec.Compress(new byte[length]);
    var properties = CscStreamProperties.Read(compressed);
    Assert.That(properties, Is.EqualTo(new CscStreamProperties(expectedWindow, 64 * 1024, 2 * 1024 * 1024)));
  }

  [Test, Category("Spec")]
  public void Header_RequestedWindowSmallerThanInput_Wins() {
    var compressed = CscCodec.Compress(new byte[200_000], new(DictionarySize: 64 * 1024));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(compressed), Is.EqualTo(64u * 1024 + 10 * 1024));
  }

  [Test, Category("Spec")]
  public void NonSeekableInput_ProducesTheSameStreamAsSeekable() {
    var data = CscSamples.Text(30_000, 4);
    using var seekable = new MemoryStream(data);
    using var expected = new MemoryStream();
    CscCodec.Compress(seekable, expected);

    using var forwardOnly = new ForwardOnlyStream(data);
    using var actual = new MemoryStream();
    CscCodec.Compress(forwardOnly, actual);

    Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
  }

  [Test, Category("Spec")]
  public void NonSeekableInputOverOneSegment_UsesTheRequestedWindow() {
    var data = CscSamples.Ramp(2 * 1024 * 1024 + 5);
    using var forwardOnly = new ForwardOnlyStream(data);
    using var compressed = new MemoryStream();
    CscCodec.Compress(forwardOnly, compressed, new(DictionarySize: 1024 * 1024));

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(compressed.ToArray()), Is.EqualTo(1024u * 1024 + 10 * 1024));
      Assert.That(CscCodec.Decompress(compressed.ToArray()), Is.EqualTo(data));
    });
  }

  [Test, Category("Spec")]
  public void Stream_ApiMatchesCodec() {
    var data = CscSamples.Text(5000, 9);
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CscStream.Compress(input, compressed);
    compressed.Position = 0;
    using var output = new MemoryStream();
    CscStream.Decompress(compressed, output);

    Assert.Multiple(() => {
      Assert.That(compressed.ToArray(), Is.EqualTo(CscCodec.Compress(data)));
      Assert.That(output.ToArray(), Is.EqualTo(data));
    });
  }

  // ── Invalid arguments and damaged streams ─────────────────────────────────

  [TestCase(0), TestCase(6)]
  [Category("Exception")]
  public void Compress_LevelOutOfRange_Throws(int level) =>
    Assert.Throws<ArgumentOutOfRangeException>(() => CscCodec.Compress([1, 2, 3], new(Level: level)));

  [TestCase(32 * 1024 - 1), TestCase(1024L * 1024 * 1024)]
  [Category("Exception")]
  public void Compress_WindowOutOfRange_Throws(long window) =>
    Assert.Throws<ArgumentOutOfRangeException>(() => CscCodec.Compress([1, 2, 3], new(DictionarySize: window)));

  [Test, Category("Exception")]
  public void Compress_NullStreams_Throw() {
    using var ms = new MemoryStream();
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => CscCodec.Compress(null!, ms));
      Assert.Throws<ArgumentNullException>(() => CscCodec.Compress(ms, null!));
      Assert.Throws<ArgumentNullException>(() => CscCodec.Decompress(null!, ms));
    });
  }

  [TestCase(0), TestCase(9)]
  [Category("ErrorHandling")]
  public void Decompress_TruncatedHeader_Throws(int length) =>
    Assert.Throws<InvalidDataException>(() => CscCodec.Decompress(new byte[length]));

  [TestCase(0u), TestCase(32u * 1024 - 1), TestCase(1024u * 1024 * 1024 + 1)]
  [Category("ErrorHandling")]
  public void Decompress_WindowOutsideLibcscLimits_Throws(uint window) {
    var stream = CscCodec.Compress("hello"u8);
    BinaryPrimitives.WriteUInt32BigEndian(stream, window);
    Assert.Throws<InvalidDataException>(() => CscCodec.Decompress(stream));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_TruncatedBody_Throws() {
    var stream = CscCodec.Compress(CscSamples.Text(20_000));
    Assert.Throws<InvalidDataException>(() => CscCodec.Decompress(stream.AsSpan(0, stream.Length / 2)));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_BlockLargerThanHeaderAllows_Throws() {
    var stream = CscCodec.Compress("hello"u8);
    stream[4] = stream[5] = 0;
    stream[6] = 4; // compressed block size 4: the first block is longer
    Assert.Throws<InvalidDataException>(() => CscCodec.Decompress(stream));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_CorruptedBytes_FailCleanlyOrDecode() {
    var original = CscCodec.Compress(CscSamples.Mixed());
    var random = new Random(77);
    for (var trial = 0; trial < 200; ++trial) {
      var damaged = (byte[])original.Clone();
      for (var flips = 0; flips < 4; ++flips)
        damaged[10 + random.Next(damaged.Length - 10)] ^= (byte)(1 << random.Next(8));

      try {
        CscCodec.Decompress(damaged);
      } catch (InvalidDataException) {
        // the only acceptable failure
      }
    }
  }

  /// <summary>A read-only stream that cannot seek, like a pipe.</summary>
  private sealed class ForwardOnlyStream(byte[] data) : Stream {
    private readonly MemoryStream _inner = new(data, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, Math.Min(count, 1000));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
