#pragma warning disable CS1591
using Compression.Core.Deflate;
using FileFormat.Zip;
using FileFormat.Zlib;
using SysCompression = System.IO.Compression;

namespace Compression.Tests.Deflate;

/// <summary>
/// Spec-compliance hardening tests that validate our DEFLATE output can be
/// read by <see cref="SysCompression.DeflateStream"/> / <see cref="SysCompression.ZLibStream"/>
/// and that our reader can accept ZIPs produced by <see cref="SysCompression.ZipArchive"/>
/// (which uses data descriptors by default).
/// </summary>
[TestFixture]
[Category("Hardening")]
[Category("ThemVsUs")]
public class DeflateHardeningTests {
  private static byte[] InflateWithSystem(byte[] compressed) {
    using var src = new MemoryStream(compressed);
    using var ds = new SysCompression.DeflateStream(src, SysCompression.CompressionMode.Decompress);
    using var dst = new MemoryStream();
    ds.CopyTo(dst);
    return dst.ToArray();
  }

  // --- Test 1: All block types produce readable output ---

  [Test]
  public void BlockType_Stored_ReadableBySystem() {
    // DeflateCompressionLevel.None emits uncompressed (stored) blocks.
    var data = new byte[512];
    new Random(42).NextBytes(data);

    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
  }

  [Test]
  public void BlockType_StaticHuffman_ReadableBySystem() {
    // Short input with static Huffman block type.
    // Fast level compares static vs. uncompressed and emits static when beneficial.
    var data = "hello"u8.ToArray();

    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Fast);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
  }

  [Test]
  public void BlockType_DynamicHuffman_ReadableBySystem() {
    // Longer structured input — Default level will pick dynamic Huffman.
    var sb = new System.Text.StringBuilder();
    for (var i = 0; i < 500; ++i)
      sb.Append("The quick brown fox jumps over the lazy dog. ");
    var data = System.Text.Encoding.UTF8.GetBytes(sb.ToString());

    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
    // Dynamic Huffman should compress substantially.
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  // --- Test 2: Edge cases ---

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(258)] // max match length
  [TestCase(32768)] // DEFLATE window size
  [TestCase(65536)] // cross-window
  public void EdgeCase_Size_ReadableBySystem(int size) {
    var data = new byte[size];
    new Random(size + 1).NextBytes(data);

    var compressed = DeflateCompressor.Compress(data);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(30_000)]
  public void EdgeCase_1MB_Random_ReadableBySystem() {
    var data = new byte[1024 * 1024];
    new Random(123).NextBytes(data);

    var compressed = DeflateCompressor.Compress(data);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(30_000)]
  public void EdgeCase_1MB_Zeros_ReadableBySystem() {
    var data = new byte[1024 * 1024];

    var compressed = DeflateCompressor.Compress(data);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
    // 1MB of zeros should compress to a tiny payload.
    Assert.That(compressed.Length, Is.LessThan(10_000));
  }

  [Test]
  [CancelAfter(30_000)]
  public void EdgeCase_1MB_RepeatingPattern_ReadableBySystem() {
    var pattern = "ABCDEFGHIJ"u8.ToArray();
    var data = new byte[1024 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    var compressed = DeflateCompressor.Compress(data);
    var round = InflateWithSystem(compressed);

    Assert.That(round, Is.EqualTo(data));
    Assert.That(compressed.Length, Is.LessThan(10_000));
  }

  // --- Test 3: zlib format ---

  [Test]
  public void ZlibWrapper_ReadableBySystemZLibStream() {
    // Verify ZlibStream output (DEFLATE + 2-byte zlib header + Adler-32 trailer)
    // is consumable by System.IO.Compression.ZLibStream.
    var data = "The quick brown fox jumps over the lazy dog, repeatedly, for a while. "u8.ToArray();
    // Widen to force real compression work.
    var widened = new byte[data.Length * 100];
    for (var i = 0; i < widened.Length; ++i)
      widened[i] = data[i % data.Length];

    var compressed = ZlibStream.Compress(widened);

    using var src = new MemoryStream(compressed);
    using var zs = new SysCompression.ZLibStream(src, SysCompression.CompressionMode.Decompress);
    using var dst = new MemoryStream();
    zs.CopyTo(dst);

    Assert.That(dst.ToArray(), Is.EqualTo(widened));
  }

  [Test]
  public void ZlibWrapper_Empty_ReadableBySystemZLibStream() {
    var compressed = ZlibStream.Compress(ReadOnlySpan<byte>.Empty);

    using var src = new MemoryStream(compressed);
    using var zs = new SysCompression.ZLibStream(src, SysCompression.CompressionMode.Decompress);
    using var dst = new MemoryStream();
    zs.CopyTo(dst);

    Assert.That(dst.ToArray(), Is.Empty);
  }

  // --- Test 4: Data descriptor support in ZIP ---

  [Test]
  public void OurReader_AcceptsZipWithDataDescriptors_FromSystemZipArchive() {
    // System.IO.Compression.ZipArchive in Create mode uses data descriptors
    // (general purpose bit 3 set) because it writes to a non-seekable context
    // for the local headers. Our writer does NOT use data descriptors, so this
    // test verifies the READER correctly handles data-descriptor entries by
    // relying on central-directory sizes (which it does — ExtractEntry uses
    // entry.CompressedSize from the central directory).
    var data = "Payload written by System.IO.Compression.ZipArchive with data descriptors."u8.ToArray();
    var longer = new byte[data.Length * 50];
    for (var i = 0; i < longer.Length; ++i)
      longer[i] = data[i % data.Length];

    // System.IO.Compression.ZipArchive only emits data descriptors when the
    // target stream is non-seekable. Wrap a MemoryStream in a forwarding stream
    // that hides seekability.
    byte[] zipBytes;
    using (var backing = new MemoryStream()) {
      using (var nonSeekable = new NonSeekableStream(backing)) {
        using var archive = new SysCompression.ZipArchive(nonSeekable, SysCompression.ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("payload.txt", SysCompression.CompressionLevel.Optimal);
        using var es = entry.Open();
        es.Write(longer, 0, longer.Length);
      }
      zipBytes = backing.ToArray();
    }

    // Confirm System ZipArchive did set the data-descriptor bit on at least one local header.
    // Local header layout: sig(4) ver(2) flags(2) ...
    // Signature 0x04034b50 is little-endian.
    var anyDataDescriptor = false;
    for (var i = 0; i + 8 < zipBytes.Length; ++i) {
      if (zipBytes[i] != 0x50 || zipBytes[i + 1] != 0x4B || zipBytes[i + 2] != 0x03 || zipBytes[i + 3] != 0x04)
        continue;
      var flags = (ushort)(zipBytes[i + 6] | (zipBytes[i + 7] << 8));
      if ((flags & 0x0008) != 0) {
        anyDataDescriptor = true;
        break;
      }
    }
    Assert.That(anyDataDescriptor, Is.True,
      "Expected System.IO.Compression.ZipArchive to emit a local header with the data-descriptor flag set.");

    using var zipMs = new MemoryStream(zipBytes);
    using var reader = new ZipReader(zipMs);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(longer));
  }

  // --- Test 5: Compression ratio comparison ---

  private static byte[] SystemDeflate(byte[] data) {
    using var ms = new MemoryStream();
    using (var ds = new SysCompression.DeflateStream(ms, SysCompression.CompressionLevel.Optimal, leaveOpen: true))
      ds.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  [Test]
  public void Ratio_RandomData_CloseToSystem() {
    var data = new byte[32 * 1024];
    new Random(999).NextBytes(data);

    var oursSize = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default).Length;
    var sysSize = SystemDeflate(data).Length;

    TestContext.Out.WriteLine(
      $"Random 32KB: ours={oursSize} bytes, system={sysSize} bytes, ratio={(double)oursSize / sysSize:F3}");

    // Random data is incompressible — both outputs should be within ~5% of the
    // input size of each other.
    var diff = Math.Abs(oursSize - sysSize);
    Assert.That(diff, Is.LessThan(data.Length / 20),
      $"Random-data output size differs by more than 5% of input size (ours={oursSize}, sys={sysSize}).");
  }

  [Test]
  public void Ratio_RepeatingText_CloseToSystem() {
    var sb = new System.Text.StringBuilder();
    for (var i = 0; i < 1000; ++i)
      sb.Append("The quick brown fox jumps over the lazy dog. ");
    var data = System.Text.Encoding.UTF8.GetBytes(sb.ToString());

    var oursSize = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default).Length;
    var sysSize = SystemDeflate(data).Length;

    TestContext.Out.WriteLine(
      $"Repeating text ({data.Length} bytes): ours={oursSize}, system={sysSize}, ratio={(double)oursSize / sysSize:F3}");

    // Accept up to 30% larger — documented acceptable band for a clean-room encoder.
    Assert.That(oursSize, Is.LessThan(sysSize * 1.30),
      $"Our repeating-text output more than 30% larger than system (ours={oursSize}, sys={sysSize}).");
  }

  /// <summary>
  /// Forwarding stream that reports <c>CanSeek = false</c>, so
  /// <see cref="SysCompression.ZipArchive"/> writes data descriptors.
  /// </summary>
  private sealed class NonSeekableStream(Stream inner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
  }

  [Test]
  public void Ratio_Zeros10KB_CloseToSystem() {
    var data = new byte[10 * 1024];

    var oursSize = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default).Length;
    var sysSize = SystemDeflate(data).Length;

    TestContext.Out.WriteLine(
      $"Zeros 10KB: ours={oursSize}, system={sysSize}, ratio={(double)oursSize / sysSize:F3}");

    // Both should compress to under a few hundred bytes.
    Assert.That(oursSize, Is.LessThan(500));
    Assert.That(sysSize, Is.LessThan(500));
  }
}
