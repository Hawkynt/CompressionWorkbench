using System.Security.Cryptography;
using Compression.Core.Deflate;
using Bcl = System.IO.Compression;

namespace Compression.Tests.Deflate;

/// <summary>
/// The streaming hash-chain encoder (levels 1–9) and the table-driven decoder: round trips
/// against the platform's zlib as an independent oracle, the stream-position contract
/// containers rely on, and rejection of malformed bitstreams.
/// </summary>
[TestFixture]
public class DeflateEngineTests {

  private static byte[] Text(int size, int seed = 7) {
    string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "deflate", "window",
      "huffman", "length", "distance", "block", "stream", "header", "trailer", "checksum", "archive", "format"];
    var rng = new Random(seed);
    var sb = new System.Text.StringBuilder(size + 16);
    while (sb.Length < size)
      sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(12) == 0 ? ".\n" : " ");
    return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, size));
  }

  private static byte[] Random(int size, int seed = 42) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static byte[] BclInflate(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var inflate = new Bcl.DeflateStream(input, Bcl.CompressionMode.Decompress);
    using var output = new MemoryStream();
    inflate.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] BclDeflate(byte[] data) {
    using var output = new MemoryStream();
    using (var deflate = new Bcl.DeflateStream(output, Bcl.CompressionLevel.Optimal, leaveOpen: true))
      deflate.Write(data);
    return output.ToArray();
  }

  public static IEnumerable<int> Levels => Enumerable.Range(1, 9);

  public static IEnumerable<int> BoundarySizes => [0, 1, 2, 3, 4, 257, 258, 259, 32767, 32768, 32769, 65535, 65536, 65537, 131073];

  // ── encoder ──────────────────────────────────────────────────────────

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void EveryLevel_Text_ReadableByPlatformZlib([ValueSource(nameof(Levels))] int level) {
    var data = Text(300_000);
    var compressed = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);
    Assert.That(BclInflate(compressed), Is.EqualTo(data));
    Assert.That(DeflateDecompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void EveryLevel_Random_ReadableByPlatformZlib_AndStaysNearInputSize([ValueSource(nameof(Levels))] int level) {
    var data = Random(200_000);
    var compressed = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);
    Assert.That(BclInflate(compressed), Is.EqualTo(data));
    // Incompressible input is stored: five bytes per block of at most 16 Ki symbols.
    Assert.That(compressed.Length, Is.LessThanOrEqualTo(data.Length + 5 * (data.Length / 16384 + 2)));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void BoundarySizes_RoundTrip([ValueSource(nameof(BoundarySizes))] int size,
    [Values(DeflateCompressionLevel.Fast, DeflateCompressionLevel.Default, DeflateCompressionLevel.Best)] DeflateCompressionLevel level) {
    var data = Text(size);
    var compressed = DeflateCompressor.Compress(data, level);
    Assert.That(BclInflate(compressed), Is.EqualTo(data));
    Assert.That(DeflateDecompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void MatchNearTheWindowEdge_IsFound() {
    // Like zlib, the encoder keeps MinLookahead (262) bytes of the window in reserve, so the
    // farthest distance it emits is 32768 - 262.
    const int distance = 32768 - 262;
    var block = Random(1024, seed: 1);
    var data = new byte[distance + block.Length];
    Random(distance, seed: 2).CopyTo(data, 0);
    block.CopyTo(data, 0);
    block.CopyTo(data, distance);

    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Best);
    Assert.That(BclInflate(compressed), Is.EqualTo(data));
    Assert.That(compressed.Length, Is.LessThan(data.Length - 512), "the repeat should be matched");
  }

  [Test, Category("Boundary")]
  public void Decoder_DistanceOf32768_IsAccepted() {
    var history = Random(32768, seed: 3);
    var bits = new LsbBits();
    bits.Put(0, 1).Put(0, 2).Align().Put(32768, 16).Put(32767, 16).Bytes(history);
    bits.Put(1, 1).Put(1, 2);
    bits.Huffman(1, 7);                    // length code 257 → 3
    bits.Huffman(29, 5).Put(8191, 13);     // distance code 29: 24577 + 8191 = 32768
    bits.Huffman(0, 7);                    // end of block

    Assert.That(DeflateDecompressor.Decompress(bits.ToArray()), Is.EqualTo((byte[])[.. history, .. history[..3]]));
  }

  [Test, Category("HappyPath")]
  public void Output_DoesNotDependOnHowInputIsSplit([Values(1, 7, 4096, 65536, 100_003)] int chunk) {
    var data = Text(250_000);
    var whole = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);

    using var output = new MemoryStream();
    var compressor = new DeflateCompressor(output, DeflateCompressionLevel.Default);
    for (var offset = 0; offset < data.Length; offset += chunk)
      compressor.Write(data.AsSpan(offset, Math.Min(chunk, data.Length - offset)));
    compressor.Finish();

    Assert.That(output.ToArray(), Is.EqualTo(whole));
  }

  [Test, Category("Spec")]
  public void Output_IsIdenticalOnEveryPlatform() {
    // Pinned digests of our own output: the windows and linux CI legs both have to produce
    // exactly these bytes, which a platform zlib/brotli never promised.
    var data = Text(200_000);
    Assert.Multiple(() => {
      Assert.That(Sha(DeflateCompressor.Compress(data, DeflateCompressionLevel.Fast)), Is.EqualTo(PinnedFast));
      Assert.That(Sha(DeflateCompressor.Compress(data, DeflateCompressionLevel.Default)), Is.EqualTo(PinnedDefault));
      Assert.That(Sha(DeflateCompressor.Compress(data, DeflateCompressionLevel.Best)), Is.EqualTo(PinnedBest));
    });
  }

  private const string PinnedFast = "12921C92F566F8B78357C6BB8E345701EAB6573DDB53A47F954EC806028ED980";
  private const string PinnedDefault = "0E0F24602132F44EAED9559B30B4FFD853546217F8851EEDD8338A844EE65EF4";
  private const string PinnedBest = "9BF1601DEF1F86D8911FC74C10BB867A03DB3A5D831B9A82E6FFBB1DE7DCF100";

  private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

  [Test, Category("ThemVsUs")]
  public void Ratio_DefaultAndBest_MatchPlatformZlib() {
    var data = Text(1_000_000);
    var bcl = BclDeflate(data).Length;
    var ours = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default).Length;
    var best = DeflateCompressor.Compress(data, DeflateCompressionLevel.Best).Length;
    Assert.That(ours, Is.LessThan(bcl * 1.03), $"ours {ours} vs zlib {bcl}");
    Assert.That(best, Is.LessThanOrEqualTo(ours));
  }

  [Test, Category("EdgeCase")]
  public void Empty_IsAFinalFixedBlock() {
    // BFINAL=1, BTYPE=01, end-of-block: the two bytes zlib emits for no input too.
    Assert.That(DeflateCompressor.Compress([], DeflateCompressionLevel.Default), Is.EqualTo(new byte[] { 0x03, 0x00 }));
  }

  // ── decoder: stream-position contract ────────────────────────────────

  [Test, Category("HappyPath")]
  public void Decoder_SeekableInput_EndsPositionedRightAfterTheDeflateData() {
    var data = Text(100_000);
    var compressed = DeflateCompressor.Compress(data);
    byte[] trailer = [0xDE, 0xAD, 0xBE, 0xEF];
    using var input = new MemoryStream([.. compressed, .. trailer]);

    var decoder = new DeflateDecompressor(input);
    Assert.That(decoder.DecompressAll(), Is.EqualTo(data));
    Assert.That(input.Position, Is.EqualTo(compressed.Length));
    Assert.That(decoder.UnconsumedBytes, Is.Zero);
  }

  [Test, Category("HappyPath")]
  public void Decoder_NonSeekableInput_HandsTheReadAheadBackThroughReadRemainder() {
    var data = Text(50_000);
    var compressed = DeflateCompressor.Compress(data);
    byte[] trailer = [1, 2, 3, 4, 5, 6, 7, 8];
    using var input = new ForwardOnlyStream([.. compressed, .. trailer, 9, 10]);

    var decoder = new DeflateDecompressor(input);
    Assert.That(decoder.DecompressAll(), Is.EqualTo(data));
    var tail = new byte[10];
    Assert.That(decoder.ReadRemainder(tail), Is.EqualTo(10));
    Assert.That(tail, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));
  }

  [Test, Category("EdgeCase")]
  public void Decoder_OneByteReads_StoredAndCompressedBlocks() {
    byte[] data = [.. Random(70_000), .. Text(70_000)];
    var compressed = DeflateCompressor.Compress(data);
    var decoder = new DeflateDecompressor(new ForwardOnlyStream(compressed, maxRead: 1));
    var output = new byte[data.Length + 1];
    var total = 0;
    int n;
    while ((n = decoder.Decompress(output.AsSpan(total, Math.Min(777, output.Length - total)))) > 0)
      total += n;

    Assert.That(output.AsSpan(0, total).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("ThemVsUs")]
  public void Decoder_ReadsPlatformZlibOutput() {
    var data = Text(400_000);
    Assert.That(DeflateDecompressor.Decompress(BclDeflate(data)), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void ReadRemainder_BeforeTheEnd_Throws() {
    var decoder = new DeflateDecompressor(new MemoryStream(DeflateCompressor.Compress(Text(1000))));
    Assert.Throws<InvalidOperationException>(() => decoder.ReadRemainder(new byte[4]));
  }

  // ── decoder: malformed input ─────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Decoder_BlockType3_IsInvalidData() {
    var bits = new LsbBits();
    bits.Put(1, 1).Put(3, 2);
    Assert.Throws<InvalidDataException>(() => DeflateDecompressor.Decompress(bits.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Decoder_OverSubscribedCode_IsInvalidData() {
    // Dynamic block whose code-length code gives all 19 symbols a one-bit code.
    var bits = new LsbBits();
    bits.Put(1, 1).Put(2, 2).Put(0, 5).Put(0, 5).Put(15, 4);
    for (var i = 0; i < 19; ++i)
      bits.Put(1, 3);
    Assert.Throws<InvalidDataException>(() => DeflateDecompressor.Decompress(bits.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Decoder_DistanceBeyondHistory_IsInvalidData() {
    // Fixed block: literal 'a', then length 3 at distance 2 — one byte of history only.
    var bits = new LsbBits();
    bits.Put(1, 1).Put(1, 2);
    bits.Huffman(0x30 + 'a', 8);   // literal 0x61
    bits.Huffman(1, 7);            // length code 257 → 3
    bits.Huffman(1, 5);            // distance code 1 → 2
    bits.Huffman(0, 7);            // end of block
    Assert.Throws<InvalidDataException>(() => DeflateDecompressor.Decompress(bits.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Decoder_StoredLengthComplementMismatch_IsInvalidData() {
    byte[] stream = [0x01, 0x05, 0x00, 0x00, 0x00, 1, 2, 3, 4, 5];
    Assert.Throws<InvalidDataException>(() => DeflateDecompressor.Decompress(stream));
  }

  [Test, Category("ErrorHandling")]
  public void Decoder_TruncatedStream_IsEndOfStream() {
    var compressed = DeflateCompressor.Compress(Text(50_000));
    Assert.Throws<EndOfStreamException>(() => DeflateDecompressor.Decompress(compressed.AsSpan(0, compressed.Length / 2)));
  }

  [Test, Category("EdgeCase")]
  public void Decoder_EmptyInput_IsEmptyOutput()
    => Assert.That(DeflateDecompressor.Decompress([]), Is.Empty);

  // ── stream wrapper ───────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RawDeflateStream_RoundTrips_AndLeavesTheInnerStreamAfterTheData() {
    var data = Text(120_000);
    using var buffer = new MemoryStream();
    using (var deflate = new RawDeflateStream(buffer, Compression.Core.Streams.CompressionStreamMode.Compress, DeflateCompressionLevel.Best, leaveOpen: true))
      deflate.Write(data);
    var compressedLength = buffer.Length;
    buffer.WriteByte(0x5A);

    Assert.That(BclInflate(buffer.ToArray()[..(int)compressedLength]), Is.EqualTo(data));

    buffer.Position = 0;
    using var inflate = new RawDeflateStream(buffer, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    inflate.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
    Assert.That(buffer.ReadByte(), Is.EqualTo(0x5A));
  }

  /// <summary>Packs bits LSB-first, as DEFLATE does; Huffman codes go most-significant bit first.</summary>
  private sealed class LsbBits {
    private readonly List<byte> _bytes = [];
    private int _bit;

    public LsbBits Put(int value, int count) {
      for (var i = 0; i < count; ++i)
        this.Bit((value >> i) & 1);
      return this;
    }

    public LsbBits Huffman(int code, int length) {
      for (var i = length - 1; i >= 0; --i)
        this.Bit((code >> i) & 1);
      return this;
    }

    public LsbBits Align() {
      this._bit = 0;
      return this;
    }

    public LsbBits Bytes(byte[] data) {
      this._bit = 0;
      this._bytes.AddRange(data);
      return this;
    }

    private void Bit(int bit) {
      if (this._bit == 0)
        this._bytes.Add(0);
      this._bytes[^1] |= (byte)(bit << this._bit);
      this._bit = (this._bit + 1) & 7;
    }

    public byte[] ToArray() => [.. this._bytes];
  }

  /// <summary>A stream that cannot seek and hands out at most <c>maxRead</c> bytes per read.</summary>
  private sealed class ForwardOnlyStream(byte[] data, int maxRead = int.MaxValue) : Stream {
    private int _position;
    public override int Read(byte[] buffer, int offset, int count) {
      var n = Math.Min(Math.Min(count, maxRead), data.Length - this._position);
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
